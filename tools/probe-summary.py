# -*- coding: utf-8 -*-
"""한 회차(measurements/<RunId>)의 프로브 기록을 요약해 화면에 낸다. 표준 라이브러리만 쓴다.

사용: python tools/probe-summary.py measurements/<RunId> [--skip 초]

  --skip 초   server-ticks.csv 에서 처음 몇 초를 빼고 집계한다 (기본 0)

읽는 파일과 내놓는 것
  server/server-ticks.csv     틱 수와 빈도, 틱 처리 시간(dur_us) 분위수, 프레임당 틱 수
  server/server-conns.csv     연결별 명령·ack 증가량과 추정 RTT, 접속 판정 (N/N)
  client*/client-conns.csv    World별 스냅샷 수신 여부와 추정 RTT

T-03 단계의 확인용이다. 기준값 집계(워밍업 제외, 회차 중앙값)는 T-06에서 따로 만든다.
"""
import csv
import glob
import os
import sys
from collections import Counter, defaultdict

CLIENT_WORLDS_SHOWN = 3  # client-conns 요약에서 World별 줄을 몇 개까지 보여 줄지 (이름순)


# ── 공통 도우미 ──────────────────────────────────────────────────────────────

def percentile(sorted_values, p):
    """가장 가까운 순위 방식의 백분위수. 값이 없으면 None."""
    if not sorted_values:
        return None
    last_index = len(sorted_values) - 1
    rank = int(round(p / 100.0 * len(sorted_values) + 0.5)) - 1
    return sorted_values[max(0, min(last_index, rank))]


def read_csv(path):
    """CSV를 읽어 행(dict)의 목록으로 돌려준다."""
    with open(path, encoding="utf-8", newline="") as f:
        return list(csv.DictReader(f))


def increase(rows, column):
    """첫 표본에서 마지막 표본까지 그 열의 값이 얼마나 늘었는지."""
    return int(rows[-1][column]) - int(rows[0][column])


def late_half_rtt_median(rows):
    """뒤쪽 절반 표본의 추정 RTT 중앙값. 접속 직후 값의 영향을 줄이려고 앞쪽 절반은 뺀다."""
    late_half = rows[len(rows) // 2:]
    return percentile(sorted(float(r["est_rtt_ms"]) for r in late_half), 50)


# ── server-ticks.csv : 서버 틱 처리 시간 ─────────────────────────────────────

def summarize_server_ticks(run, skip_seconds):
    path = os.path.join(run, "server", "server-ticks.csv")
    if not os.path.exists(path):
        print("[server-ticks] 파일 없음:", path)
        return
    rows = read_csv(path)
    if not rows:
        return

    # 첫 줄의 시작 시각을 0으로 보고, 처음 skip_seconds 초의 줄은 뺀다.
    first_begin_us = int(rows[0]["begin_us"])
    used = [r for r in rows if (int(r["begin_us"]) - first_begin_us) / 1e6 >= skip_seconds]
    if not used:
        print(f"[server-ticks] rows={len(rows)} used=0 (skip {skip_seconds}s) 남은 줄이 없다")
        return

    durations = sorted(int(r["dur_us"]) for r in used)

    # 집계 구간의 길이(초)와 초당 틱 수
    span = (int(used[-1]["begin_us"]) - int(used[0]["begin_us"])) / 1e6 if len(used) > 1 else 0
    rate = len(used) / span if span else 0

    # 프레임 하나에 틱이 몇 번 돌았는지의 분포. 예: {1: 3240} = 모든 프레임이 틱 1번
    ticks_in_frame = Counter(r["frame"] for r in used)
    ticks_per_frame = Counter(ticks_in_frame.values())

    # 이웃한 줄 사이에 서버 틱 번호가 몇씩 늘었는지의 분포. 1이 아니면 틱이 묶였거나 건너뛴 것이다
    server_ticks = [int(r["server_tick"]) for r in used]
    tick_steps = Counter(later - earlier for earlier, later in zip(server_ticks, server_ticks[1:]))

    batch_sizes = Counter(r["batch_size"] for r in used)
    max_connections = max(int(r["connections"]) for r in used)
    last = used[-1]

    print(f"[server-ticks] rows={len(rows)} used={len(used)} (skip {skip_seconds}s) span={span:.1f}s rate={rate:.2f}/s")
    print(f"  dur_us p50={percentile(durations, 50)} p99={percentile(durations, 99)} max={durations[-1]} min={durations[0]}")
    print(f"  ticks per frame: {dict(sorted(ticks_per_frame.items()))}")
    print(f"  server_tick step between rows: {dict(sorted(tick_steps.items()))}")
    print(f"  batch_size values: {dict(batch_sizes)}")
    print(f"  connections max={max_connections} last={last['connections']} in_game last={last['in_game']}")


# ── server-conns.csv : 서버가 본 연결별 상태 ─────────────────────────────────

def summarize_server_conns(run):
    path = os.path.join(run, "server", "server-conns.csv")
    if not os.path.exists(path):
        print("[server-conns] 파일 없음:", path)
        return
    rows = read_csv(path)

    # 연결 번호별로 표본을 모은다 (파일은 시간순이므로 묶음 안에서도 시간순이다).
    rows_by_connection = defaultdict(list)
    for r in rows:
        rows_by_connection[int(r["network_id"])].append(r)

    print(f"[server-conns] rows={len(rows)} connections={len(rows_by_connection)}")

    commands_increased = 0  # 명령 도착 수가 늘어난 연결 수
    ack_advanced = 0        # 스냅샷 ack 틱이 전진한 연결 수
    for network_id in sorted(rows_by_connection):
        samples = rows_by_connection[network_id]
        last = samples[-1]

        command_increase = increase(samples, "cmds_arrived")
        ack_tick_increase = increase(samples, "last_snapshot_acked_by_remote")
        if command_increase > 0:
            commands_increased += 1
        if ack_tick_increase > 0:
            ack_advanced += 1

        # 이동 입력이 0이 아니었던 표본 수. 0이면 그 연결은 움직이는 입력을 보내지 않은 것이다
        moving_samples = sum(1 for r in samples if r["move_x"] not in ("0", "") or r["move_y"] not in ("0", ""))

        print(f"  id={network_id:3d} samples={len(samples):3d} in_game={last['in_game']} cmds+={command_increase} late={last['cmds_too_late']} "
              f"ack_tick+={ack_tick_increase} prefabs={last['num_loaded_prefabs']} rtt_ms(median of 2nd half)={late_half_rtt_median(samples)} "
              f"dev={last['dev_rtt_ms']} age_q8={last['server_command_age_q8']} nonzero_move_samples={moving_samples}")

    total = len(rows_by_connection)
    print(f"  입력 도착이 늘어난 연결 {commands_increased}/{total}, 스냅샷 ack 틱이 전진한 연결 {ack_advanced}/{total}")


# ── client-conns.csv : 클라이언트가 본 연결 상태 (클라이언트 프로세스마다 하나) ──

def summarize_client_conns(run):
    for path in sorted(glob.glob(os.path.join(run, "client*", "client-conns.csv"))):
        rows = read_csv(path)

        # World별로 표본을 모은다.
        rows_by_world = defaultdict(list)
        for r in rows:
            rows_by_world[r["world"]].append(r)

        snapshot_advanced = 0  # 받은 스냅샷 틱이 전진한 World 수
        worlds = []            # World별 요약
        for world, samples in rows_by_world.items():
            last = samples[-1]
            snapshot_tick_increase = increase(samples, "last_snapshot_received_by_local")
            if snapshot_tick_increase > 0:
                snapshot_advanced += 1
            worlds.append({
                "world": world,
                "rtt": late_half_rtt_median(samples),
                "dev": last["dev_rtt_ms"],
                "snapshot_tick_increase": snapshot_tick_increase,
                "in_game": last["in_game"],
                "has_target": last["has_target"],
            })

        # World별 RTT 중앙값의 최소·중앙·최대
        rtts = sorted(w["rtt"] for w in worlds)
        rtt_min = rtts[0] if rtts else None
        rtt_max = rtts[-1] if rtts else None

        total = len(rows_by_world)
        print(f"[{os.path.relpath(path, run)}] rows={len(rows)} worlds={total} 스냅샷 수신 틱이 전진한 World {snapshot_advanced}/{total} "
              f"rtt_ms(world median) min={rtt_min} p50={percentile(rtts, 50)} max={rtt_max}")

        # World가 많으면 줄이 길어지므로 이름순으로 앞의 몇 개만 보여 준다.
        for w in sorted(worlds, key=lambda w: w["world"])[:CLIENT_WORLDS_SHOWN]:
            print(f"  {w['world']} rtt_ms={w['rtt']} dev={w['dev']} snapshot_tick+={w['snapshot_tick_increase']} "
                  f"in_game={w['in_game']} has_target={w['has_target']}")


# ── 실행 ─────────────────────────────────────────────────────────────────────

def main():
    if len(sys.argv) < 2:
        print(__doc__)
        return 2

    run = sys.argv[1]  # 실행 폴더 (measurements/<RunId>)
    skip_seconds = 0.0
    if "--skip" in sys.argv:
        skip_seconds = float(sys.argv[sys.argv.index("--skip") + 1])

    summarize_server_ticks(run, skip_seconds)
    summarize_server_conns(run)
    summarize_client_conns(run)
    return 0


if __name__ == "__main__":
    sys.exit(main())
