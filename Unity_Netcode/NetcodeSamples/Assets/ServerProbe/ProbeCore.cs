using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace ServerProbe
{
    // ─────────────────────────────────────────────────────────────────────────────
    // 직무과제 관찰용 코드(로그 시스템)의 공통 부분.
    //
    //   ProbeArgs : 실행 인자를 읽어 둔다. 프로브를 켤지 말지도 여기서 정해진다.
    //   ProbeLog  : CSV 한 줄을 받아 파일에 쓴다. 실제 파일 쓰기는 별도 스레드가 한다.
    //               (CSV 한 줄을 만드는 도우미는 CsvRow.cs 에 있다)
    //
    // 넷코드 패키지와 기존 샘플 파일은 건드리지 않는다.
    // 실행 인자에 -probeOut 이 없으면 이 폴더의 모든 시스템이 꺼진 채로 남는다.
    //
    // ── 기록 파일 지도 ───────────────────────────────────────────────────────────
    // 실행 한 번의 기록은 measurements\<실행 ID>\ 폴더 하나에 모인다.
    // 남기는 주체는 셋이다: 실행 스크립트(tools\probe-run.ps1), Unity 플레이어, 프로브(이 폴더의 코드).
    //
    //   measurements\<실행 ID>\
    //   ├─ run.txt                      실행 스크립트
    //   ├─ cpu.csv                        실행 스크립트
    //   ├─ envN.json                      실행 스크립트 (프로파일 실행일 때만, 사용한 설정의 사본)
    //   ├─ server.log                     Unity (서버 프로세스)
    //   ├─ client1.log, client2.log ...   Unity (클라이언트 프로세스마다 하나)
    //   ├─ server\                        프로브 (서버 프로세스)
    //   │   ├─ meta.txt
    //   │   ├─ server-ticks.csv
    //   │   ├─ server-conns.csv
    //   │   └─ systems-ServerWorld.tsv
    //   └─ client1\ ...                   프로브 (클라이언트 프로세스마다 하나)
    //       ├─ meta.txt
    //       ├─ client-conns.csv
    //       ├─ systems-ClientWorld.tsv
    //       └─ systems-ThinClientWorld1.tsv
    //
    // 파일별 설명 (내용 / 왜 필요한지 / 만드는 곳과 시점)
    //
    //   run.txt
    //     내용   : 실행 조건, git 커밋, 실행 파일과 프로브 DLL 시각, 프로세스별 PID·실행 인자·종료 코드
    //     왜 필요 : 이 측정값이 어느 커밋·어느 빌드·어떤 조건에서 나왔는지 나중에 증명하려고.
    //              프로세스가 정상 종료했는지(종료 코드)도 여기서 본다
    //     만드는 곳 : tools\probe-run.ps1 의 Note 함수. 일이 있을 때마다 한 줄씩 덧붙인다
    //   cpu.csv
    //     내용   : 프로세스별 누적 CPU 시간, CPU 사용률, 메모리
    //     왜 필요 : 서버와 클라이언트가 한 PC에서 함께 돈다. 틱 시간이 이상할 때
    //              PC 자원을 누가 얼마나 쓰고 있었는지 프로세스 밖에서 본 값으로 확인하려고
    //     만드는 곳 : tools\probe-run.ps1 의 표본 루프. 5초마다 프로세스당 한 줄
    //   envN.json
    //     내용   : 그 실행에 쓴 네트워크 시뮬레이터 설정
    //     왜 필요 : tools\netprofiles\ 의 원본이 나중에 바뀌어도, 그 실행이 실제로 쓴 설정을 기록과 함께 남기려고
    //     만드는 곳 : tools\probe-run.ps1. -Profile 을 줬을 때 tools\netprofiles\ 에서 복사한다
    //   server.log, clientN.log
    //     내용   : Unity 플레이어의 일반 로그. 프로브가 Debug.Log 로 남긴 줄도 여기에 섞인다
    //              ([ServerProbe], [ProbeDriver], [ThinProbe] 머리말로 찾는다)
    //     왜 필요 : 엔진·넷코드·샘플이 낸 오류와 예외는 여기에만 남는다. CSV가 이상하거나 비어 있을 때 원인을 찾는 곳이다.
    //              "Go in game connection N" 줄로 접속 수를 CSV와 별개로 한 번 더 셀 수 있다
    //     만드는 곳 : Unity 플레이어. 경로는 probe-run.ps1 이 -logFile 인자로 넘긴다
    //   server\, clientN\
    //     내용   : 그 프로세스의 프로브 기록 폴더. 경로는 probe-run.ps1 이 -probeOut 인자로 넘긴다
    //     왜 필요 : 프로세스마다 같은 이름의 파일(meta.txt 등)을 쓰므로 폴더를 나눠야 서로 덮어쓰지 않는다
    //     만드는 곳 : ProbeLog.EnsureOutputFolder
    //   meta.txt
    //     내용   : 실행 ID, 실행 인자, Unity 버전, 빌드 정의, 시뮬레이터 설정, 시작 시각. 서버는 틱레이트 설정도
    //     왜 필요 : 측정값을 읽기 전에 조건부터 확인하려고. 릴리스 빌드인지, 시뮬레이터가 실제로 어떤 값으로 켜졌는지,
    //              틱레이트가 몇인지는 프로세스 안에서만 알 수 있다. startLocalTime은 프로세스 사이의 시각을 맞출 때 쓴다
    //     만드는 곳 : ProbeDriver.WriteMetaFile (시작 때 한 번). 서버는 LogServerTickRateOnce 가 한 줄 덧붙인다
    //   server-ticks.csv
    //     내용   : 서버 틱마다 한 줄. 틱 번호, 처리 시간(dur_us), 연결 수
    //     왜 필요 : 서버지표1(서버 틱 처리 시간)의 원자료다. 평균만이 아니라 분위수(p50, p99)와 튀는 틱을 보려면
    //              틱마다의 값이 있어야 한다. 연결 수를 같이 적어 "연결이 몇일 때의 값인지"를 가를 수 있게 한다
    //     만드는 곳 : ServerTickEndProbeSystem.OnUpdate → ProbeLog
    //   server-conns.csv
    //     내용   : 1초마다 연결당 한 줄. 추정 RTT, 명령 도착 수, 스냅샷 ack 틱, 이동 입력
    //     왜 필요 : 연결 수만으로는 부하가 실제로 걸렸는지 알 수 없다. 연결마다 입력이 들어오고 스냅샷이 나가는지
    //              확인하는 근거다. 네트워크 프로파일이 의도한 지연으로 적용됐는지도 추정 RTT로 대조한다
    //     만드는 곳 : ServerConnectionProbeSystem.AppendConnectionRow → ProbeLog
    //   client-conns.csv
    //     내용   : 1초마다 World당 한 줄. 추정 RTT, 받은 스냅샷 틱
    //     왜 필요 : 서버가 본 것을 클라이언트 쪽에서 한 번 더 확인하려고. 씬 클라이언트 World 하나하나가
    //              실제로 스냅샷을 받고 있는지는 클라이언트 프로세스 안에서만 볼 수 있다
    //     만드는 곳 : ClientConnectionProbeSystem.OnUpdate → ProbeLog
    //   systems-<World 이름>.tsv
    //     내용   : 그 World의 시스템 목록과 순서 (시스템 순서 덤프)
    //     왜 필요 : 두 가지. (1) 틱 시간 측정 구간이 의도한 자리(그룹 맨 앞 ~ GhostSendSystem 뒤)에 놓였는지
    //              실행마다 확인한다. (2) World 배치표(서버·클라이언트·씬 클라이언트에 어떤 시스템이 있는지)의 원자료다
    //     만드는 곳 : SystemOrderDump.DumpWorld (-probeDump 시각에 한 번, 기본 8초)
    //
    // "→ ProbeLog" 로 적은 CSV는 시스템이 ProbeLog.Append 로 줄을 넘기고,
    // 실제 파일은 ProbeLog.Flush 가 처음 쓸 때 만들어 0.5초마다 이어 쓴다.
    // meta.txt 와 .tsv 는 해당 코드가 File.WriteAllText / AppendAllText 로 바로 쓴다.
    // ─────────────────────────────────────────────────────────────────────────────

    // 실행 인자. 샘플과 같은 "대시 하나 + 이름 + 값" 형식이다. 예: -probeOut D:\run1 -probeThin 24
    // 값을 읽어 두기만 한다. 파일이나 폴더는 건드리지 않는다(기록 폴더는 ProbeLog.EnsureOutputFolder가 만든다).
    public static class ProbeArgs
    {
        public static readonly bool Enabled;       // 프로브를 켤지 여부. -probeOut 인자가 있으면 true
        public static readonly string OutDir;      // -probeOut <폴더>     기록 폴더(절대 경로로 바꿔 둔다)
        public static readonly string RunId;       // -probeRunId <이름>   실행 ID. meta.txt에 적는다
        public static readonly float Duration;     // -probeDuration <초>  이 시간이 지나면 스스로 끝난다. 0이면 끝내지 않는다
        public static readonly float DumpAt;       // -probeDump <초>      이 시각에 World별 시스템 순서를 남긴다. 기본 8, 0이면 끈다
        public static readonly int Thin;           // -probeThin <개수>    씬 클라이언트 World 개수. 클라이언트 프로세스 전용
        public static readonly float ThinRate;     // -probeThinRate <Hz>  씬 클라이언트 World를 1초에 몇 개씩 만들지. 기본 4
        public static readonly int Fps;            // -probeFps <값>       클라이언트 프레임 상한. 기본 60. 서버 World가 있으면 적용 안 함
        public static readonly uint Seed;          // -probeSeed <값>      씬 클라이언트 입력의 시드. 기본 1
        public static readonly string CommandLine; // 프로세스가 받은 실행 인자 전체. meta.txt에 그대로 적는다

        static ProbeArgs()
        {
            var args = Environment.GetCommandLineArgs();
            CommandLine = string.Join(" ", args);

            OutDir = FindValue(args, "probeOut");
            Enabled = !string.IsNullOrEmpty(OutDir);

            RunId = FindValue(args, "probeRunId") ?? "";
            Duration = FindNumber(args, "probeDuration", 0f);
            DumpAt = FindNumber(args, "probeDump", 8f);
            Thin = (int)FindNumber(args, "probeThin", 0f);
            ThinRate = FindNumber(args, "probeThinRate", 4f);
            Fps = (int)FindNumber(args, "probeFps", 60f);
            Seed = (uint)FindNumber(args, "probeSeed", 1f);

            if (Enabled)
            {
                OutDir = Path.GetFullPath(OutDir);
            }
        }

        // "-key 값" 쌍에서 값을 찾는다. 대소문자는 가리지 않는다. 없으면 null.
        static string FindValue(string[] args, string key)
        {
            var flag = "-" + key;
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }
            return null;
        }

        // 값을 숫자로 읽는다. 인자가 없거나 숫자가 아니면 fallback을 쓴다.
        static float FindNumber(string[] args, string key, float fallback)
        {
            var text = FindValue(args, key);
            if (text == null)
            {
                return fallback;
            }

            // 소수점은 PC의 지역 설정과 무관하게 항상 '.' 으로 읽는다.
            var parsed = float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
            return parsed ? number : fallback;
        }
    }

    // CSV 기록기. Append는 메모리에 쌓기만 하고, 별도 스레드가 0.5초마다 파일에 쓴다.
    // 그래서 서버 틱 측정 구간 안에서는 파일 쓰기가 일어나지 않는다.
    public static class ProbeLog
    {
        const int k_FlushIntervalMs = 500; // 쌓아 둔 줄을 파일에 쓰는 주기(밀리초)

        // 파일 하나에 대응한다.
        sealed class Channel
        {
            public StringBuilder Pending = new StringBuilder(1 << 16); // 아직 파일에 쓰지 않은 글
            public StreamWriter Writer;                                // 처음 쓸 때 연다
        }

        static readonly object s_Lock = new object();
        static readonly Dictionary<string, Channel> s_Channels = new Dictionary<string, Channel>(); // 파일 이름 → 채널
        static Thread s_Thread;       // 파일에 쓰는 스레드
        static volatile bool s_Stop;  // true가 되면 쓰기 스레드가 멈춘다

        // ── 시각 ────────────────────────────────────────────────────────────────
        // 모든 CSV의 시각 열(t_us, begin_us)은 StartTimestamp부터 잰 마이크로초다.
        // StartTimestamp는 "프로세스 시작"이 아니라 "이 클래스가 처음 쓰인 시점"이다.
        // 프로세스마다 기준이 다르므로 프로세스 사이의 시각 대조에는 쓸 수 없다
        // (그때는 meta.txt의 startLocalTime과 run.txt를 쓴다).

        public static readonly long StartTimestamp = Stopwatch.GetTimestamp(); // 시각 열의 0점

        // 0점부터 지금까지의 경과 시간(마이크로초).
        public static long NowUs => (Stopwatch.GetTimestamp() - StartTimestamp) * 1000000L / Stopwatch.Frequency;

        // Stopwatch 틱 수(차이값)를 마이크로초로 바꾼다.
        public static long ToUs(long stopwatchTicks) => stopwatchTicks * 1000000L / Stopwatch.Frequency;

        // ── 기록 ────────────────────────────────────────────────────────────────

        // 기록 폴더가 없으면 만든다. 파일을 처음 쓰기 전에 부른다. 여러 번 불러도 된다.
        public static void EnsureOutputFolder()
        {
            if (!ProbeArgs.Enabled)
            {
                return;
            }

            Directory.CreateDirectory(ProbeArgs.OutDir);
        }

        // 파일에 한 줄을 더한다(실제 쓰기는 나중에). 그 파일에 처음 쓰는 것이면 머리 줄을 먼저 넣는다.
        //   file   : 기록 폴더 안의 파일 이름. 예: "server-ticks.csv"
        //   header : CSV 머리 줄. 필요 없으면 null
        //   line   : 기록할 한 줄(줄바꿈 제외)
        public static void Append(string file, string header, string line)
        {
            if (!ProbeArgs.Enabled)
            {
                return;
            }

            lock (s_Lock)
            {
                if (!s_Channels.TryGetValue(file, out var channel))
                {
                    channel = new Channel();
                    s_Channels.Add(file, channel);
                    if (header != null)
                    {
                        channel.Pending.Append(header).Append('\n');
                    }
                    EnsureWriterThread();
                }
                channel.Pending.Append(line).Append('\n');
            }
        }

        static void EnsureWriterThread()
        {
            if (s_Thread != null)
            {
                return;
            }

            // 백그라운드 스레드라서 프로세스 종료를 막지 않는다.
            s_Thread = new Thread(WriterLoop) { IsBackground = true, Name = "ProbeLogWriter" };
            s_Thread.Start();
        }

        static void WriterLoop()
        {
            while (!s_Stop)
            {
                Thread.Sleep(k_FlushIntervalMs);
                Flush();
            }
        }

        // 쌓아 둔 줄을 지금 파일에 쓴다. 종료 직전에도 직접 부른다.
        public static void Flush()
        {
            if (!ProbeArgs.Enabled)
            {
                return;
            }

            // 1) 잠금 안에서는 쌓인 글을 꺼내기만 한다. Append를 오래 막지 않기 위해서다.
            var work = new List<KeyValuePair<Channel, string>>();
            lock (s_Lock)
            {
                foreach (var entry in s_Channels)
                {
                    var fileName = entry.Key;
                    var channel = entry.Value;
                    if (channel.Pending.Length == 0)
                    {
                        continue;
                    }

                    if (channel.Writer == null)
                    {
                        // CSV 파일이 실제로 만들어지는 곳: <-probeOut 폴더>\<file>
                        // (server-ticks.csv, server-conns.csv, client-conns.csv)
                        EnsureOutputFolder();
                        var path = Path.Combine(ProbeArgs.OutDir, fileName);
                        channel.Writer = new StreamWriter(path, false, new UTF8Encoding(false)); // 덮어쓰기, BOM 없음
                    }
                    work.Add(new KeyValuePair<Channel, string>(channel, channel.Pending.ToString()));
                    channel.Pending.Clear();
                }
            }

            // 2) 디스크 쓰기는 잠금 밖에서 한다.
            foreach (var item in work)
            {
                var channel = item.Key;
                var text = item.Value;
                lock (channel)
                {
                    channel.Writer.Write(text);
                    channel.Writer.Flush();
                }
            }
        }

        // 쓰기 스레드를 멈추고 남은 줄을 마저 쓴다. 프로세스가 끝날 때 부른다.
        public static void Shutdown()
        {
            s_Stop = true;
            Flush();
        }
    }
}
