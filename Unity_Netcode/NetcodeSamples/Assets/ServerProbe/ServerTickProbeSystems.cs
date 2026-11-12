using System.Diagnostics;
using Unity.Entities;
using Unity.NetCode;

namespace ServerProbe
{
    // ─────────────────────────────────────────────────────────────────────────────
    // 서버지표1(서버 틱 처리 시간)을 잰다.
    //
    // 방법: 서버 World의 SimulationSystemGroup 안에 시스템을 두 개 넣는다.
    //
    //   SimulationSystemGroup
    //     ├─ ServerTickBeginProbeSystem   ← 맨 앞. 시작 시각을 찍는다
    //     ├─ ... (넷코드와 샘플의 시스템들) ...
    //     ├─ GhostSendSystem
    //     └─ ServerTickEndProbeSystem     ← 맨 뒤. 끝 시각을 찍고 한 줄을 기록한다
    //
    // 두 시각의 차이가 server-ticks.csv의 dur_us 다.
    // 실제로 어디에 놓였는지는 실행마다 systems-ServerWorld.tsv 덤프로 확인한다.
    //
    // 이 값에 들어가지 않는 것:
    //   - 그룹 자체의 진입·종료 비용(레이트 매니저). 두 시스템의 바깥이다.
    //   - 예약만 되고 이 구간에서 끝나지 않은 Job의 시간. Job을 강제로 끝내지 않는다.
    // ─────────────────────────────────────────────────────────────────────────────

    // 시작 시스템이 찍은 값을 끝 시스템에 넘기는 자리. 서버 World는 프로세스에 하나라서 정적으로 둔다.
    static class TickProbeState
    {
        public static long BeginTimestamp; // 이번 틱의 시작 시각(Stopwatch 값)
        public static int Frame = -1;      // 시작 시각을 찍은 Unity 프레임 번호
        public static int IndexInFrame;    // 같은 프레임 안에서 몇 번째 틱인지(0부터)
        public static bool HasBegin;       // 시작 시각이 찍혔고 아직 끝 시스템이 쓰지 않았으면 true
    }

    // 틱의 시작 시각을 찍는다. 서버 World의 SimulationSystemGroup 맨 앞에서 돈다.
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderFirst = true)]
    [UpdateBefore(typeof(BeginSimulationEntityCommandBufferSystem))]
    public partial struct ServerTickBeginProbeSystem : ISystem
    {
        public void OnCreate(ref SystemState state)
        {
            if (!ProbeArgs.Enabled)
            {
                state.Enabled = false;
            }
        }

        public void OnUpdate(ref SystemState state)
        {
            // 프레임이 바뀌면 순번을 0으로, 같은 프레임에서 다시 불리면 1씩 올린다.
            var frame = UnityEngine.Time.frameCount;
            if (frame != TickProbeState.Frame)
            {
                TickProbeState.Frame = frame;
                TickProbeState.IndexInFrame = 0;
            }
            else
            {
                TickProbeState.IndexInFrame++;
            }
            TickProbeState.HasBegin = true;

            // 시각은 반드시 마지막에 찍는다. 위의 정리 작업이 측정 구간에 들어가지 않게 하기 위해서다.
            TickProbeState.BeginTimestamp = Stopwatch.GetTimestamp();
        }
    }

    // 틱의 끝 시각을 찍고 server-ticks.csv에 한 줄을 쓴다. GhostSendSystem 뒤, 그룹 맨 뒤에서 돈다.
    [WorldSystemFilter(WorldSystemFilterFlags.ServerSimulation)]
    [UpdateInGroup(typeof(SimulationSystemGroup), OrderLast = true)]
    [UpdateAfter(typeof(GhostSendSystem))]
    public partial struct ServerTickEndProbeSystem : ISystem
    {
        const string k_File = "server-ticks.csv"; // <서버의 -probeOut 폴더>\server-ticks.csv. 파일은 ProbeLog.Flush가 만든다

        // 열 설명 (시간 단위는 마이크로초)
        //   frame          Unity 프레임 번호
        //   idx_in_frame   그 프레임 안에서 몇 번째 틱인지 (0부터)
        //   server_tick    넷코드 서버 틱 번호
        //   batch_size     NetworkTime.SimulationStepBatchSize 값
        //   begin_us       틱 시작 시각 (ProbeLog의 0점 기준)
        //   dur_us         틱 처리 시간 = 끝 시각 - 시작 시각
        //   connections    연결 수 (NetworkId가 있는 엔티티)
        //   in_game        그중 게임에 들어간 연결 수 (NetworkStreamInGame이 있는 엔티티)
        const string k_Header = "frame,idx_in_frame,server_tick,batch_size,begin_us,dur_us,connections,in_game";

        EntityQuery m_Connections; // NetworkId가 있는 엔티티
        EntityQuery m_InGame;      // NetworkId와 NetworkStreamInGame이 함께 있는 엔티티

        public void OnCreate(ref SystemState state)
        {
            if (!ProbeArgs.Enabled)
            {
                state.Enabled = false;
            }

            m_Connections = state.GetEntityQuery(ComponentType.ReadOnly<NetworkId>());
            m_InGame = state.GetEntityQuery(ComponentType.ReadOnly<NetworkId>(), ComponentType.ReadOnly<NetworkStreamInGame>());
        }

        public void OnUpdate(ref SystemState state)
        {
            // 시각은 반드시 맨 먼저 찍는다. 아래의 기록 작업이 측정 구간에 들어가지 않게 하기 위해서다.
            var end = Stopwatch.GetTimestamp();

            // 짝이 되는 시작 시각이 없으면 기록하지 않는다.
            if (!TickProbeState.HasBegin)
            {
                return;
            }
            TickProbeState.HasBegin = false;

            // ── 여기부터는 측정 구간 밖이다 ──
            var begin = TickProbeState.BeginTimestamp;
            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            var serverTick = networkTime.ServerTick.IsValid ? networkTime.ServerTick.TickIndexForValidTick : 0u;

            var row = new CsvRow();
            row.Add(TickProbeState.Frame);                           // frame
            row.Add(TickProbeState.IndexInFrame);                    // idx_in_frame
            row.Add(serverTick);                                     // server_tick
            row.Add(networkTime.SimulationStepBatchSize);            // batch_size
            row.Add(ProbeLog.ToUs(begin - ProbeLog.StartTimestamp)); // begin_us
            row.Add(ProbeLog.ToUs(end - begin));                     // dur_us
            row.Add(m_Connections.CalculateEntityCount());           // connections
            row.Add(m_InGame.CalculateEntityCount());                // in_game
            ProbeLog.Append(k_File, k_Header, row.ToString());
        }
    }
}
