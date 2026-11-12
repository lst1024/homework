using System.Globalization;
using Samples.HelloNetcode;
using Unity.Entities;
using Unity.NetCode;

namespace ServerProbe
{
    // ─────────────────────────────────────────────────────────────────────────────
    // 연결 상태를 1초에 한 번 기록한다.
    //
    //   ServerConnectionProbeSystem : 서버 World          → server-conns.csv
    //   ClientConnectionProbeSystem : 클라이언트·씬 클라이언트 World → client-conns.csv
    //
    // 둘 다 InitializationSystemGroup에서 돈다. 서버 틱 측정 구간(SimulationSystemGroup) 밖이므로
    // 이 기록에 드는 시간은 server-ticks.csv의 dur_us에 들어가지 않는다.
    //
    // 값은 모두 연결 엔티티에 있는 컴포넌트(NetworkSnapshotAck 등)를 읽기만 한다.
    // ─────────────────────────────────────────────────────────────────────────────

    // 서버 World의 연결별 상태를 1초에 한 번 기록한다.
    //   입력을 받고 있는지     : cmds_arrived가 늘어나는지로 본다.
    //   스냅샷을 보내고 있는지 : 상대가 받았다고 알린 마지막 스냅샷 틱(last_snapshot_acked_by_remote)이 전진하는지로 본다.
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct ServerConnectionProbeSystem : ISystem
    {
        const double k_SampleIntervalSeconds = 1.0;  // 연결별 표본을 남기는 주기(초)
        const double k_SummaryIntervalSeconds = 5.0; // Unity 로그에 요약 한 줄을 남기는 주기(초)

        const string k_File = "server-conns.csv";

        // 열 설명 (연결 하나가 한 줄)
        //   t_us                           기록 시각 (마이크로초, ProbeLog의 0점 기준)
        //   network_id                     연결 번호 (NetworkId)
        //   in_game                        게임에 들어갔으면 1 (NetworkStreamInGame)
        //   est_rtt_ms, dev_rtt_ms         넷코드의 추정 RTT와 그 편차 (ms)
        //   last_snapshot_acked_by_remote  상대가 받았다고 알린 마지막 스냅샷 틱 (없으면 0)
        //   cmds_arrived                   도착한 명령 수. 도착 여부 판정에만 쓰고 손실률·입력률로 쓰지 않는다
        //   cmd_packets_arrived            도착한 명령 패킷 수
        //   cmds_too_late                  늦게 도착한 명령 수
        //   server_command_age_q8          NetworkSnapshotAck.ServerCommandAge 원본 값
        //   num_loaded_prefabs             NetworkSnapshotAck.NumLoadedPrefabs
        //   has_target                     입력을 받는 캐릭터 엔티티가 지정됐으면 1 (CommandTarget)
        //   move_x, move_y                 그 캐릭터에 지금 들어 있는 이동 입력
        const string k_Header = "t_us,network_id,in_game,est_rtt_ms,dev_rtt_ms,last_snapshot_acked_by_remote,cmds_arrived,cmd_packets_arrived,cmds_too_late,server_command_age_q8,num_loaded_prefabs,has_target,move_x,move_y";

        double m_NextSampleTime;  // 다음 표본을 남길 시각
        double m_NextSummaryTime; // 다음 요약 줄을 남길 시각

        public void OnCreate(ref SystemState state)
        {
            if (!ProbeArgs.Enabled)
            {
                state.Enabled = false;
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            // 1초가 지나지 않았으면 아무것도 하지 않는다.
            var now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now < m_NextSampleTime)
            {
                return;
            }
            m_NextSampleTime = now + k_SampleIntervalSeconds;

            var invariant = CultureInfo.InvariantCulture;
            var timeText = ProbeLog.NowUs.ToString(invariant); // 이번 표본의 모든 줄이 같은 시각을 쓴다
            var inGameLookup = SystemAPI.GetComponentLookup<NetworkStreamInGame>(true);
            var inputLookup = SystemAPI.GetComponentLookup<CharacterControllerPlayerInput>(true);

            // 요약 줄에 쓸 개수
            int connectionCount = 0;      // 연결 수
            int inGameCount = 0;          // 게임에 들어간 연결 수
            int withCommandsCount = 0;    // 명령이 한 번이라도 도착한 연결 수
            int withSnapshotAckCount = 0; // 스냅샷 ack가 한 번이라도 온 연결 수

            foreach (var (networkId, snapshotAckRef, commandTarget, connectionEntity) in
                     SystemAPI.Query<RefRO<NetworkId>, RefRO<NetworkSnapshotAck>, RefRO<CommandTarget>>().WithEntityAccess())
            {
                var snapshotAck = snapshotAckRef.ValueRO;
                var stats = snapshotAck.CommandArrivalStatistics;

                connectionCount++;

                var inGame = inGameLookup.HasComponent(connectionEntity);
                if (inGame)
                {
                    inGameCount++;
                }

                if (stats.NumCommandsArrived > 0)
                {
                    withCommandsCount++;
                }

                var ackedTick = snapshotAck.LastReceivedSnapshotByRemote;
                if (ackedTick.IsValid)
                {
                    withSnapshotAckCount++;
                }

                // 이 연결의 입력이 향하는 캐릭터 엔티티와, 거기 들어 있는 이동 입력
                var targetEntity = commandTarget.ValueRO.targetEntity;
                var hasTarget = targetEntity != Entity.Null && inputLookup.HasComponent(targetEntity);
                float moveX = 0, moveY = 0;
                if (hasTarget)
                {
                    var input = inputLookup[targetEntity];
                    moveX = input.Movement.x;
                    moveY = input.Movement.y;
                }

                ProbeLog.Append(k_File, k_Header, string.Concat(
                    timeText, ",",                                                           // t_us
                    networkId.ValueRO.Value.ToString(invariant), ",",                        // network_id
                    inGame ? "1" : "0", ",",                                                 // in_game
                    snapshotAck.EstimatedRTT.ToString("0.0", invariant), ",",                // est_rtt_ms
                    snapshotAck.DeviationRTT.ToString("0.0", invariant), ",",                // dev_rtt_ms
                    (ackedTick.IsValid ? ackedTick.TickIndexForValidTick : 0u).ToString(invariant), ",", // last_snapshot_acked_by_remote
                    stats.NumCommandsArrived.ToString(invariant), ",",                       // cmds_arrived
                    stats.NumCommandPacketsArrived.ToString(invariant), ",",                 // cmd_packets_arrived
                    stats.NumArrivedTooLate.ToString(invariant), ",",                        // cmds_too_late
                    snapshotAck.ServerCommandAge.ToString(invariant), ",",                   // server_command_age_q8
                    snapshotAck.NumLoadedPrefabs.ToString(invariant), ",",                   // num_loaded_prefabs
                    hasTarget ? "1" : "0", ",",                                              // has_target
                    moveX.ToString("0.##", invariant), ",",                                  // move_x
                    moveY.ToString("0.##", invariant)));                                     // move_y
            }

            // 5초에 한 번은 Unity 로그(server.log)에도 요약을 남긴다. CSV를 열지 않고도 접속 상태를 볼 수 있다.
            if (now >= m_NextSummaryTime)
            {
                m_NextSummaryTime = now + k_SummaryIntervalSeconds;
                UnityEngine.Debug.Log(
                    $"[ServerProbe] connections={connectionCount} inGame={inGameCount} withCommandsArrived={withCommandsCount} withSnapshotAck={withSnapshotAckCount} t={now:0.0}s");
            }
        }
    }

    // 클라이언트 World와 씬 클라이언트 World의 연결 상태를 1초에 한 번 기록한다.
    //   이 World가 스냅샷을 받고 있는지 : last_snapshot_received_by_local이 전진하는지로 본다.
    [WorldSystemFilter(WorldSystemFilterFlags.ClientSimulation | WorldSystemFilterFlags.ThinClientSimulation)]
    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial struct ClientConnectionProbeSystem : ISystem
    {
        const double k_SampleIntervalSeconds = 1.0; // 표본을 남기는 주기(초)

        const string k_File = "client-conns.csv";

        // 열 설명 (한 프로세스의 여러 World가 같은 파일에 쓴다)
        //   t_us                             기록 시각 (마이크로초, ProbeLog의 0점 기준)
        //   world                            World 이름 (ClientWorld, ThinClientWorld1, ...)
        //   network_id                       연결 번호 (NetworkId)
        //   in_game                          게임에 들어갔으면 1
        //   est_rtt_ms, dev_rtt_ms           넷코드의 추정 RTT와 그 편차 (ms)
        //   last_snapshot_received_by_local  이 World가 받은 마지막 스냅샷 틱 (없으면 0)
        //   server_command_age_q8            NetworkSnapshotAck.ServerCommandAge 원본 값
        //   has_target                       입력 엔티티가 지정됐으면 1 (CommandTarget)
        const string k_Header = "t_us,world,network_id,in_game,est_rtt_ms,dev_rtt_ms,last_snapshot_received_by_local,server_command_age_q8,has_target";

        double m_NextSampleTime; // 다음 표본을 남길 시각

        public void OnCreate(ref SystemState state)
        {
            if (!ProbeArgs.Enabled)
            {
                state.Enabled = false;
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            // 1초가 지나지 않았으면 아무것도 하지 않는다.
            var now = UnityEngine.Time.realtimeSinceStartupAsDouble;
            if (now < m_NextSampleTime)
            {
                return;
            }
            m_NextSampleTime = now + k_SampleIntervalSeconds;

            var invariant = CultureInfo.InvariantCulture;
            var timeText = ProbeLog.NowUs.ToString(invariant); // 이번 표본의 모든 줄이 같은 시각을 쓴다
            var worldName = state.WorldUnmanaged.Name.ToString();
            var inGameLookup = SystemAPI.GetComponentLookup<NetworkStreamInGame>(true);

            foreach (var (networkId, snapshotAckRef, commandTarget, connectionEntity) in
                     SystemAPI.Query<RefRO<NetworkId>, RefRO<NetworkSnapshotAck>, RefRO<CommandTarget>>().WithEntityAccess())
            {
                var snapshotAck = snapshotAckRef.ValueRO;
                var receivedTick = snapshotAck.LastReceivedSnapshotByLocal;

                ProbeLog.Append(k_File, k_Header, string.Concat(
                    timeText, ",",                                                           // t_us
                    worldName, ",",                                                          // world
                    networkId.ValueRO.Value.ToString(invariant), ",",                        // network_id
                    inGameLookup.HasComponent(connectionEntity) ? "1" : "0", ",",            // in_game
                    snapshotAck.EstimatedRTT.ToString("0.0", invariant), ",",                // est_rtt_ms
                    snapshotAck.DeviationRTT.ToString("0.0", invariant), ",",                // dev_rtt_ms
                    (receivedTick.IsValid ? receivedTick.TickIndexForValidTick : 0u).ToString(invariant), ",", // last_snapshot_received_by_local
                    snapshotAck.ServerCommandAge.ToString(invariant), ",",                   // server_command_age_q8
                    commandTarget.ValueRO.targetEntity != Entity.Null ? "1" : "0"));         // has_target
            }
        }
    }
}
