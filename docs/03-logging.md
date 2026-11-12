# 로그 구성: 무엇을 어디에서 남기는가

- 작성일: 2026-11-12
- 기준 코드: `Unity_Netcode\NetcodeSamples\Assets\ServerProbe\` (2026-11-12 구조 정리 뒤), `tools\probe-run.ps1`, `tools\unity-build.ps1`
- 상태: 초안 (작성자 검토 전)
- 관련 문서: 실행 인자와 측정 구간의 근거는 [02-environment.md](02-environment.md) 6절, 반증 검토 결과와 알려진 한계는 같은 문서 10절

이 문서는 실행 한 번에 생기는 기록 파일을 **파일별로** 정리한다. 각 파일을 누가, 언제, 어떤 내용으로 남기는지와 열의 뜻을 적는다.

## 1. 한눈에 보기

실행 한 번의 기록은 `E:\homework\measurements\<실행 ID>\` 폴더 하나에 모인다. 남기는 주체는 세 곳이다.

| 주체 | 남기는 것 | 위치 |
| --- | --- | --- |
| 프로브 코드 (`ServerProbe`, 게임 프로세스 안) | CSV 3종, `meta.txt`, 시스템 순서 덤프 | `<실행 ID>\server\`, `<실행 ID>\clientN\` |
| Unity 플레이어 | 일반 로그 (프로브의 `Debug.Log` 줄이 섞여 있음) | `<실행 ID>\server.log`, `<실행 ID>\clientN.log` |
| 실행 스크립트 (`tools\probe-run.ps1`) | 실행 조건, CPU·메모리 표본 | `<실행 ID>\run.txt`, `<실행 ID>\cpu.csv` |

```
measurements\<실행 ID>\
├─ run.txt                        실행 스크립트
├─ cpu.csv                        실행 스크립트
├─ envN.json                      실행 스크립트 (프로파일 실행일 때만, 사용한 설정의 사본)
├─ server.log                     Unity (서버 프로세스)
├─ client1.log, client2.log ...   Unity (클라이언트 프로세스마다 하나)
├─ server\                        프로브 (서버 프로세스)
│   ├─ meta.txt
│   ├─ server-ticks.csv
│   ├─ server-conns.csv
│   └─ systems-ServerWorld.tsv
└─ client1\ ...                   프로브 (클라이언트 프로세스마다 하나)
    ├─ meta.txt
    ├─ client-conns.csv
    ├─ systems-ClientWorld.tsv
    └─ systems-ThinClientWorld1.tsv
```

- 프로브는 실행 인자에 `-probeOut <폴더>`가 있을 때만 켜진다. 없으면 프로브 파일은 하나도 생기지 않는다.
- `measurements\` 전체는 `.gitignore`에 들어 있다. 원본 기록은 git에 올리지 않고, 집계 결과만 `docs\`에 커밋한다.
- 덤프 폴더에는 `systems-LoadingWorld*.tsv`도 생긴다(행 0개). 분석에는 쓰지 않는다.

## 2. 프로브 코드가 남기는 파일

### 2.1 `server-ticks.csv` — 서버 틱 처리 시간

- 남기는 곳: `ServerTickEndProbeSystem` (`ServerTickProbeSystems.cs`)
- 시점: 서버 틱마다 한 줄
- 용도: 서버지표1(서버 틱 처리 시간)

| 열 | 뜻 |
| --- | --- |
| `frame` | Unity 프레임 번호 |
| `idx_in_frame` | 그 프레임 안에서 몇 번째 틱인지 (0부터) |
| `server_tick` | 넷코드 서버 틱 번호 |
| `batch_size` | `NetworkTime.SimulationStepBatchSize` 값 |
| `begin_us` | 틱 시작 시각 (마이크로초, 2.6절의 0점 기준) |
| `dur_us` | 틱 처리 시간 = 끝 시각 − 시작 시각 (마이크로초) |
| `connections` | 연결 수 (`NetworkId`가 있는 엔티티) |
| `in_game` | 그중 게임에 들어간 연결 수 (`NetworkStreamInGame`이 있는 엔티티) |

측정 구간은 서버 World의 `SimulationSystemGroup` 맨 앞(`ServerTickBeginProbeSystem`)부터 맨 뒤(`GhostSendSystem` 뒤의 `ServerTickEndProbeSystem`)까지다. 실제 위치는 실행마다 `systems-ServerWorld.tsv`로 확인한다.

### 2.2 `server-conns.csv` — 서버가 본 연결별 상태

- 남기는 곳: `ServerConnectionProbeSystem` (`ConnectionProbeSystems.cs`)
- 시점: 1초마다, 연결 하나가 한 줄
- 용도: 접속·입력 수신·스냅샷 전송 여부 확인, 추정 RTT

| 열 | 뜻 |
| --- | --- |
| `t_us` | 기록 시각 (마이크로초). 같은 표본의 줄은 모두 같은 값 |
| `network_id` | 연결 번호 (`NetworkId`) |
| `in_game` | 게임에 들어갔으면 1 |
| `est_rtt_ms`, `dev_rtt_ms` | 넷코드의 추정 RTT와 그 편차 (ms) |
| `last_snapshot_acked_by_remote` | 상대가 받았다고 알린 마지막 스냅샷 틱 (없으면 0) |
| `cmds_arrived` | 도착한 명령 수 |
| `cmd_packets_arrived` | 도착한 명령 패킷 수 |
| `cmds_too_late` | 늦게 도착한 명령 수 |
| `server_command_age_q8` | `NetworkSnapshotAck.ServerCommandAge` 원본 값 |
| `num_loaded_prefabs` | `NetworkSnapshotAck.NumLoadedPrefabs` |
| `has_target` | 입력을 받는 캐릭터 엔티티가 지정됐으면 1 (`CommandTarget`) |
| `move_x`, `move_y` | 그 캐릭터에 지금 들어 있는 이동 입력 |

판정에 쓰는 방법:

- 입력을 받고 있는가: `cmds_arrived`가 늘어나는지로 본다.
- 스냅샷을 보내고 있는가: `last_snapshot_acked_by_remote`가 전진하는지로 본다.

### 2.3 `client-conns.csv` — 클라이언트가 본 연결 상태

- 남기는 곳: `ClientConnectionProbeSystem` (`ConnectionProbeSystems.cs`)
- 시점: 1초마다, World 하나가 한 줄. 한 프로세스의 클라이언트 World와 씬 클라이언트 World들이 같은 파일에 쓴다
- 용도: 클라이언트 쪽에서 스냅샷을 받고 있는지 확인, 추정 RTT

| 열 | 뜻 |
| --- | --- |
| `t_us` | 기록 시각 (마이크로초) |
| `world` | World 이름 (`ClientWorld`, `ThinClientWorld1`, …) |
| `network_id` | 연결 번호 (`NetworkId`) |
| `in_game` | 게임에 들어갔으면 1 |
| `est_rtt_ms`, `dev_rtt_ms` | 넷코드의 추정 RTT와 그 편차 (ms) |
| `last_snapshot_received_by_local` | 이 World가 받은 마지막 스냅샷 틱 (없으면 0) |
| `server_command_age_q8` | `NetworkSnapshotAck.ServerCommandAge` 원본 값 |
| `has_target` | 입력 엔티티가 지정됐으면 1 (`CommandTarget`) |

### 2.4 `meta.txt` — 실행 조건

- 남기는 곳: `ProbeDriver` (`ProbeDriver.cs`)
- 시점: 프로세스 시작 때 한 번. 서버 프로세스는 틱레이트 줄을 조금 뒤에 한 줄 덧붙인다
- 형식: `키=값` 줄

| 키 | 뜻 |
| --- | --- |
| `runId` | 실행 ID (`-probeRunId`) |
| `commandLine` | 프로세스가 받은 실행 인자 전체 |
| `unityVersion`, `platform` | Unity 버전과 플랫폼 (`WindowsServer` 등) |
| `isDebugBuild`, `isBatchMode` | 개발 빌드 여부, 배치 모드 여부 |
| `define.UNITY_SERVER`, `define.UNITY_CLIENT`, `define.NETCODE_DEBUG` | 빌드에 그 컴파일 정의가 있었으면 1 |
| `networkSimulator.Enabled`, `networkSimulator.params` | 네트워크 시뮬레이터 설정. `NETCODE_DEBUG`가 정의된 빌드에서만 나온다 |
| `stopwatchFrequency` | Stopwatch의 초당 틱 수 |
| `startLocalTime` | 기록을 시작한 PC 시각. 프로세스 사이의 시각 대조에 쓴다 |
| `processId` | 프로세스 ID |
| `clientServerTickRate.…` | 서버 World의 틱레이트 설정 (서버 World가 있는 프로세스만). `singletonExists=False`면 기본값이다 |

### 2.5 `systems-<World 이름>.tsv` — 시스템 순서 덤프

- 남기는 곳: `SystemOrderDump` (`SystemOrderDump.cs`). `ProbeDriver`가 부른다
- 시점: 시작 8초 뒤 한 번 (`-probeDump`로 바꾼다. 0이면 끈다)
- 용도: World별 시스템 구성 확인, 프로브 시스템이 의도한 자리에 놓였는지 확인. `tools\world-table.py`가 이 파일로 World 배치표 데이터를 만든다
- 씬 클라이언트 World는 첫 번째 것만 남긴다

| 열 | 뜻 |
| --- | --- |
| `world`, `flags` | World 이름과 종류 플래그 |
| `depth` | 그룹 중첩 깊이 (최상위 그룹이 0) |
| `order` | 부모 그룹 안에서의 순번 |
| `system` | 시스템 타입 이름 |
| `kind` | `group` / `managed` / `unmanaged` |
| `enabled` | 켜져 있으면 1 |
| `parent` | 부모 그룹 이름 (최상위 그룹은 `-`) |

### 2.6 시각 열의 기준과 쓰기 방식

- `t_us`, `begin_us`의 0점은 프로세스 시작이 아니라 **그 프로세스에서 기록 클래스(`ProbeLog`)가 처음 쓰인 시점**이다. 프로세스마다 0점이 다르므로, 프로세스 사이의 시각을 맞출 때는 `meta.txt`의 `startLocalTime`과 `run.txt`를 쓴다.
- CSV 3종은 `ProbeLog`가 메모리에 쌓아 두고 별도 스레드가 0.5초마다 파일에 쓴다. 틱 측정 구간 안에서는 파일 쓰기가 일어나지 않는다.
- `meta.txt`와 `.tsv`는 `ProbeDriver`와 `SystemOrderDump`가 바로 쓴다.
- 숫자는 PC의 지역 설정과 무관하게 소수점 `.`으로 쓴다. 파일은 BOM 없는 UTF-8이다.

## 3. Unity 로그에 섞여 남는 줄 (`server.log`, `clientN.log`)

Unity가 `-logFile` 인자로 쓰는 일반 플레이어 로그다. 엔진과 넷코드 샘플의 출력 사이에 프로브가 `Debug.Log`로 남긴 줄이 섞여 있다. 머리말로 찾는다.

| 머리말 | 남기는 곳 | 시점 | 내용 |
| --- | --- | --- | --- |
| `[ServerProbe]` | `ServerConnectionProbeSystem` | 5초마다 | 접속 요약: `connections`, `inGame`, `withCommandsArrived`, `withSnapshotAck`, 경과 시간 |
| `[ProbeDriver] started.` | `ProbeDriver` | 시작 | 기록 폴더, 실행 ID, 씬 클라이언트 수, 실행 시간 |
| `[ProbeDriver] client frame cap:` | `ProbeDriver` | 시작 (클라이언트만) | 프레임 상한 적용 결과 |
| `[ProbeDriver] thin client creation started.` | `ProbeDriver` | 클라이언트 World가 게임에 들어간 뒤 | 요청 개수와 생성 빈도 |
| `[ProbeDriver] thin client worlds=N/M` | `ProbeDriver` | 4개마다, 그리고 다 찼을 때 | 만들어진 씬 클라이언트 World 수 |
| `[ProbeDriver] clientServerTickRate…` | `ProbeDriver` | 한 번 (서버만) | `meta.txt`에 덧붙이는 줄과 같은 내용 |
| `[ProbeDriver] system dump world=…` | `SystemOrderDump` | 덤프 때 | World별 줄 수와 파일 경로 |
| `[ProbeDriver] duration …s reached. Quitting.` | `ProbeDriver` | 종료 직전 | 지정 시간이 지나 스스로 끝남 |
| `[ThinProbe][<World>] input entity created…` | `ThinClientProbeInputSystem` | 씬 클라이언트 World마다 한 번 | 연결 번호와 입력 시드 |
| `[ServerWorld] Go in game connection N` 등 | 넷코드 샘플 (프로브 아님) | 연결이 게임에 들어갈 때 | 접속 수를 세는 데 쓴다 |

예시 (`20261111-conn-t48`의 `server.log`):

```
[ServerProbe] connections=50 inGame=50 withCommandsArrived=50 withSnapshotAck=50 t=45.6s
```

## 4. 실행 스크립트가 남기는 파일 (`tools\probe-run.ps1`)

### 4.1 `run.txt`

시각이 붙은 줄로 실행 과정을 남긴다.

- 실행 조건: 실행 ID, 씬, 포트, 클라이언트 프로세스 수, 프로세스당 씬 클라이언트 수, 프로파일, 실행 시간, 기대 연결 수
- git 커밋과 커밋되지 않은 변경 수 (`commit=… dirty=…`)
- 서버·클라이언트 실행 파일 경로와 그 안의 프로브 DLL 수정 시각 (`probeDllWritten=…`)
- 프로세스별 PID와 실행 인자, UDP 포트를 잡은 프로세스, 종료 코드

### 4.2 `cpu.csv`

5초마다 프로세스별로 한 줄을 남긴다.

| 열 | 뜻 |
| --- | --- |
| `time` | PC 시각 (`HH:mm:ss`) |
| `name`, `pid` | 프로세스 이름(`server`, `client1`, …)과 PID |
| `cpu_seconds_total` | 누적 CPU 시간(초) |
| `cpu_pct_of_one_core` | 직전 표본 이후의 평균 CPU 사용률 (코어 1개 기준 %) |
| `working_set_mb` | 메모리 사용량 (MB) |

### 4.3 `envN.json`

프로파일 실행(`-Profile env0` 등)일 때 `tools\netprofiles\`의 설정 파일을 실행 폴더에 복사해 둔다.

## 5. 빌드 로그 (`tools\unity-build.ps1`)

빌드할 때마다 `measurements\_env\build-<종류>-<시각>\`에 남는다.

| 파일 | 내용 |
| --- | --- |
| `build.log` | Unity 배치 빌드의 전체 로그 |
| `run.txt` | 시작·끝 시각, Unity 실행 인자, 종료 코드 |

## 6. 집계

원본을 직접 고치지 않고 아래 스크립트로 읽는다.

| 스크립트 | 읽는 파일 | 내놓는 것 |
| --- | --- | --- |
| `tools\probe-summary.py <실행 폴더>` | `server-ticks.csv`, `server-conns.csv`, `client*\client-conns.csv` | 틱 수와 빈도, `dur_us` 분위수, 연결별 명령·ack 증가량과 추정 RTT, 접속 판정(N/N) |
| `tools\world-table.py <실행 폴더> <출력 파일>` | `systems-ServerWorld.tsv`, `systems-ClientWorld.tsv`, `systems-ThinClientWorld1.tsv` | World 배치표 데이터 (`docs\data\world-placement.tsv`) |

## 7. 알려진 한계

아래는 로그를 읽을 때 알아야 할 것만 추린 것이다. 근거와 나머지 항목은 [02-environment.md](02-environment.md) 6절과 10절(반증 검토 결과, 알려진 한계)에 있다.

- `dur_us`에는 그룹 자체의 진입·종료 비용과, 예약만 되고 구간 안에서 끝나지 않은 Job의 시간이 들어가지 않을 수 있다.
- `batch_size`는 기록만 한다. 계획서가 요구한 "틱 배칭 끄기" 설정은 아직 구현하지 않았다.
- `cmds_arrived`는 도착 여부 판정에만 쓴다. 손실률이나 입력률로 쓰지 않는다.
- 시각 열의 0점이 프로세스마다 다르다 (2.6절).
- `-probeOut` 없이 돌린 대조 실행은 하지 않았다. 프로브 자체가 측정값에 주는 영향은 확인하지 않았다.
