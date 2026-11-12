using System.Globalization;
using System.IO;
using System.Text;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace ServerProbe
{
    // ─────────────────────────────────────────────────────────────────────────────
    // 프로브의 진행 담당. 프로세스당 하나이며 -probeOut 인자가 있을 때만 자동으로 만들어진다.
    //
    // 하는 일
    //   시작할 때   : meta.txt 기록, 클라이언트 프레임 상한 적용
    //   매 프레임   : 씬 클라이언트 World 생성 진행, 서버 틱레이트 한 번 기록
    //   지정 시각에 : World별 시스템 순서 덤프(-probeDump, 실제 덤프는 SystemOrderDump), 프로세스 종료(-probeDuration)
    // ─────────────────────────────────────────────────────────────────────────────
    public class ProbeDriver : MonoBehaviour
    {
        const int k_TickRateLogMinFrame = 10; // 서버 틱레이트를 읽기 전에 기다리는 프레임 수
        const int k_ThinCountLogStep = 4;     // 씬 클라이언트 World 개수를 로그에 남기는 간격(개)

        bool m_ThinStarted;       // 씬 클라이언트 생성을 시작했는가
        bool m_Dumped;            // 시스템 순서 덤프를 했는가
        bool m_TickRateLogged;    // 서버 틱레이트를 기록했는가
        bool m_QuitRequested;     // 종료를 요청했는가
        int m_LastThinCount = -1; // 마지막으로 확인한 씬 클라이언트 World 개수

        // ── 생성 ────────────────────────────────────────────────────────────────

        // 첫 씬이 로드된 뒤 Unity가 부른다. 프로브가 켜져 있으면 이 컴포넌트를 가진 오브젝트를 만든다.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Create()
        {
            if (!ProbeArgs.Enabled)
            {
                return;
            }

            var go = new GameObject("ServerProbeDriver");
            DontDestroyOnLoad(go); // 씬이 바뀌어도 남는다
            go.AddComponent<ProbeDriver>();
        }

        // ── 시작할 때 한 번 ─────────────────────────────────────────────────────

        void Start()
        {
            ProbeLog.EnsureOutputFolder();
            WriteMetaFile();
            ApplyClientFrameCap();

            Debug.Log($"[ProbeDriver] started. out='{ProbeArgs.OutDir}' runId='{ProbeArgs.RunId}' thin={ProbeArgs.Thin} duration={ProbeArgs.Duration}");

            // 프로세스가 끝날 때 남은 기록을 마저 쓴다.
            Application.quitting += ProbeLog.Shutdown;
        }

        // meta.txt: 이 실행이 어떤 빌드·설정으로 돌았는지를 "키=값" 줄로 남긴다.
        // 측정값을 나중에 볼 때 조건을 확인하는 근거가 된다.
        static void WriteMetaFile()
        {
            var meta = new StringBuilder();
            meta.AppendLine("runId=" + ProbeArgs.RunId);
            meta.AppendLine("commandLine=" + ProbeArgs.CommandLine);
            meta.AppendLine("unityVersion=" + Application.unityVersion);
            meta.AppendLine("platform=" + Application.platform);
            meta.AppendLine("isDebugBuild=" + Debug.isDebugBuild);
            meta.AppendLine("isBatchMode=" + Application.isBatchMode);

            // 빌드 종류를 가르는 컴파일 정의. 빌드에 그 정의가 있었으면 1, 없었으면 0이 찍힌다.
        #if UNITY_SERVER
            meta.AppendLine("define.UNITY_SERVER=1");
        #else
            meta.AppendLine("define.UNITY_SERVER=0");
        #endif
        #if UNITY_CLIENT
            meta.AppendLine("define.UNITY_CLIENT=1");
        #else
            meta.AppendLine("define.UNITY_CLIENT=0");
        #endif

            // 네트워크 시뮬레이터 설정은 NETCODE_DEBUG가 정의된 빌드에서만 적는다.
        #if NETCODE_DEBUG
            meta.AppendLine("define.NETCODE_DEBUG=1");
            meta.AppendLine("networkSimulator.Enabled=" + NetworkSimulatorSettings.Enabled);
            var sim = NetworkSimulatorSettings.ClientSimulatorParameters;
            meta.AppendLine(
                $"networkSimulator.params=Mode:{sim.Mode} DelayMs:{sim.PacketDelayMs} JitterMs:{sim.PacketJitterMs}"
                + $" DropPercent:{sim.PacketDropPercentage} DropInterval:{sim.PacketDropInterval}"
                + $" DuplicationPercent:{sim.PacketDuplicationPercentage} RandomSeed:{sim.RandomSeed}"
                + $" MaxPacketCount:{sim.MaxPacketCount} MaxPacketSize:{sim.MaxPacketSize}");
        #else
            meta.AppendLine("define.NETCODE_DEBUG=0");
        #endif

            meta.AppendLine("stopwatchFrequency=" + System.Diagnostics.Stopwatch.Frequency);
            meta.AppendLine("startLocalTime=" + System.DateTime.Now.ToString("o", CultureInfo.InvariantCulture));
            meta.AppendLine("processId=" + System.Diagnostics.Process.GetCurrentProcess().Id);

            // meta.txt가 만들어지는 곳: <-probeOut 폴더>\meta.txt (있으면 덮어쓴다)
            File.WriteAllText(MetaFilePath, meta.ToString(), new UTF8Encoding(false));
        }

        static string MetaFilePath => Path.Combine(ProbeArgs.OutDir, "meta.txt"); // meta.txt의 전체 경로

        // 클라이언트 프로세스의 프레임 상한을 -probeFps 값으로 정한다.
        static void ApplyClientFrameCap()
        {
            // 서버 World가 있는 프로세스에서는 넷코드가 프레임률을 직접 다루므로 건드리지 않는다.
            if (ClientServerBootstrap.ServerWorld != null)
            {
                return;
            }
            if (ProbeArgs.Fps <= 0)
            {
                return;
            }

            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = ProbeArgs.Fps;
            Debug.Log($"[ProbeDriver] client frame cap: targetFrameRate={Application.targetFrameRate} vSyncCount={QualitySettings.vSyncCount}");
        }

        // ── 매 프레임 ───────────────────────────────────────────────────────────

        void Update()
        {
            var now = Time.realtimeSinceStartupAsDouble;

            UpdateThinClients();
            LogServerTickRateOnce();

            // 지정 시각이 되면 시스템 순서를 한 번 덤프한다.
            if (!m_Dumped && ProbeArgs.DumpAt > 0 && now >= ProbeArgs.DumpAt)
            {
                m_Dumped = true;
                SystemOrderDump.DumpAllWorlds();
            }

            // 지정 시간이 지나면 남은 기록을 쓰고 끝낸다.
            if (!m_QuitRequested && ProbeArgs.Duration > 0 && now >= ProbeArgs.Duration)
            {
                m_QuitRequested = true;
                Debug.Log($"[ProbeDriver] duration {ProbeArgs.Duration}s reached. Quitting.");
                ProbeLog.Flush();
                Application.Quit(0);
            }
        }

        // 씬 클라이언트 World를 -probeThin 개수만큼 만든다. 만드는 일 자체는 넷코드의
        // AutomaticThinClientWorldsUtility에 맡기고, 여기서는 시작 시점과 개수·빈도만 정한다.
        void UpdateThinClients()
        {
            if (ProbeArgs.Thin <= 0)
            {
                return;
            }

            if (!m_ThinStarted)
            {
                // 실행 중에 만든 World는 참조 World의 씬 요청을 복사해서 서브씬을 받는다
                // (AutomaticThinClientWorldsUtility.DefaultRuntimeThinClientWorldInitialization).
                // 그래서 클라이언트 World에 씬 요청 엔티티가 생기고 게임에 들어간 뒤에 시작한다.
                var client = ClientServerBootstrap.ClientWorld;
                if (client == null || !client.IsCreated)
                {
                    return;
                }

                using var sceneRequests = client.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<RequestSceneLoaded>(),
                    ComponentType.ReadOnly<SceneReference>());
                using var inGameConnections = client.EntityManager.CreateEntityQuery(
                    ComponentType.ReadOnly<NetworkId>(),
                    ComponentType.ReadOnly<NetworkStreamInGame>());
                if (sceneRequests.IsEmpty || inGameConnections.IsEmpty)
                {
                    return;
                }

                m_ThinStarted = true;
                AutomaticThinClientWorldsUtility.ReferenceWorld = client;
                AutomaticThinClientWorldsUtility.NumThinClientsRequested = ProbeArgs.Thin;
                AutomaticThinClientWorldsUtility.CreationFrequency = ProbeArgs.ThinRate;
                Debug.Log(
                    $"[ProbeDriver] thin client creation started. requested={ProbeArgs.Thin} rateHz={ProbeArgs.ThinRate} sceneRequests={sceneRequests.CalculateEntityCount()} t={Time.realtimeSinceStartupAsDouble:0.0}s");
            }

            AutomaticThinClientWorldsUtility.UpdateAutomaticThinClientWorlds();

            // 개수가 바뀌었을 때만, 4개마다 그리고 다 찼을 때 로그를 남긴다.
            var count = AutomaticThinClientWorldsUtility.AutomaticallyManagedWorlds.Count;
            if (count != m_LastThinCount)
            {
                m_LastThinCount = count;
                if (count == ProbeArgs.Thin || count % k_ThinCountLogStep == 0)
                {
                    Debug.Log(
                        $"[ProbeDriver] thin client worlds={count}/{ProbeArgs.Thin} t={Time.realtimeSinceStartupAsDouble:0.0}s");
                }
            }
        }

        // 서버 World의 틱레이트 설정을 로그와 meta.txt에 한 번 남긴다.
        void LogServerTickRateOnce()
        {
            if (m_TickRateLogged)
            {
                return;
            }

            var server = ClientServerBootstrap.ServerWorld;
            if (server == null || !server.IsCreated)
            {
                return;
            }
            if (Time.frameCount < k_TickRateLogMinFrame)
            {
                return;
            }
            m_TickRateLogged = true;

            // 싱글턴이 없으면 기본값 구조체에 ResolveDefaults()를 적용한 값을 적는다.
            // singletonExists로 어느 쪽인지 구분한다.
            using var query = server.EntityManager.CreateEntityQuery(ComponentType.ReadOnly<ClientServerTickRate>());
            var hasSingleton = query.TryGetSingleton<ClientServerTickRate>(out var rate);
            rate.ResolveDefaults();

            var line =
                $"clientServerTickRate.singletonExists={hasSingleton} SimulationTickRate={rate.SimulationTickRate} NetworkTickRate={rate.NetworkTickRate}"
                + $" MaxSimulationStepsPerFrame={rate.MaxSimulationStepsPerFrame} MaxSimulationStepBatchSize={rate.MaxSimulationStepBatchSize}"
                + $" TargetFrameRateMode={rate.TargetFrameRateMode} targetFrameRate={Application.targetFrameRate}";
            Debug.Log("[ProbeDriver] " + line);
            File.AppendAllText(MetaFilePath, line + "\n", new UTF8Encoding(false)); // meta.txt 끝에 한 줄 덧붙인다
        }
    }
}
