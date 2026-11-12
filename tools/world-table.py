# -*- coding: utf-8 -*-
"""프로브가 남긴 시스템 순서 덤프 3개를 합쳐 World 배치표를 만든다. 표준 라이브러리만 쓴다.

사용: python tools/world-table.py measurements/<RunId> docs/data/world-placement.tsv

읽는 파일 (실행 폴더 안)
  server/systems-ServerWorld.tsv          서버 World
  client1/systems-ClientWorld.tsv         클라이언트 World
  client1/systems-ThinClientWorld1.tsv    씬 클라이언트 World

만드는 파일: 시스템 하나가 한 줄인 표
  system   시스템 타입 이름
  origin   출처. probe(프로브) / sample(샘플) / 그 밖에는 패키지 이름(NetCode, Entities, Physics, ...)
  server   서버 World에서 부모 그룹 안 순번. 그 World에 없으면 빈칸
  client   클라이언트 World에서의 순번. 없으면 빈칸
  thin     씬 클라이언트 World에서의 순번. 없으면 빈칸
  parent   부모 그룹 이름 (서버 → 클라이언트 → 씬 클라이언트 순으로 먼저 있는 쪽의 값)
  kind     group / managed / unmanaged (parent와 같은 쪽의 값)

같은 이름의 시스템이 한 덤프에 두 번 나오면 뒤의 줄 하나만 남는다.
"""
import csv
import os
import sys


def load_dump(path):
    """덤프 하나를 읽어 {시스템 이름: 그 줄} 로 돌려준다."""
    with open(path, encoding="utf-8", newline="") as f:
        return {row["system"]: row for row in csv.DictReader(f, delimiter="\t")}


def origin_of(system_name):
    """시스템 이름의 네임스페이스로 출처를 가른다."""
    if system_name.startswith("ServerProbe."):
        return "probe"
    if system_name.startswith("Unity.NetCode.Samples") or not system_name.startswith("Unity."):
        return "sample"
    return system_name.split(".")[1]  # "Unity.NetCode.GhostSendSystem" → "NetCode"


def order_in(dump, system_name):
    """그 World에서의 순번. 그 World에 없는 시스템이면 빈칸."""
    return dump[system_name]["order"] if system_name in dump else ""


def main():
    if len(sys.argv) < 3:
        print(__doc__)
        return 2
    run = sys.argv[1]          # 실행 폴더 (measurements/<RunId>)
    output_path = sys.argv[2]  # 만들 표의 경로

    server = load_dump(os.path.join(run, "server", "systems-ServerWorld.tsv"))
    client = load_dump(os.path.join(run, "client1", "systems-ClientWorld.tsv"))
    thin = load_dump(os.path.join(run, "client1", "systems-ThinClientWorld1.tsv"))

    # 세 World 중 한 곳에라도 있는 시스템을 모두 모아, 출처별 → 이름순으로 늘어놓는다.
    all_names = set(server) | set(client) | set(thin)
    names = sorted(all_names, key=lambda name: (origin_of(name), name))

    os.makedirs(os.path.dirname(output_path), exist_ok=True)
    with open(output_path, "w", encoding="utf-8", newline="\n") as f:
        f.write("system\torigin\tserver\tclient\tthin\tparent\tkind\n")
        for name in names:
            # parent와 kind는 그 시스템이 있는 첫 World의 줄에서 가져온다.
            row = server.get(name) or client.get(name) or thin.get(name)
            columns = [
                name,
                origin_of(name),
                order_in(server, name),
                order_in(client, name),
                order_in(thin, name),
                row["parent"],
                row["kind"],
            ]
            f.write("\t".join(columns) + "\n")

    print(f"rows={len(names)} server={len(server)} client={len(client)} thin={len(thin)} -> {output_path}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
