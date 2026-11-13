using System;
using System.IO;
using System.Linq;
using Unity.Entities.Build;
using Unity.NetCode.Hybrid;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Research.Build
{
    // 직무과제 분석용 빌드 진입점. 넷코드 패키지와 기존 샘플 파일은 건드리지 않는다.
    //
    // 스크립트 빌드(BuildPipeline.BuildPlayer)는 빌드 창 전용 핸들러
    // (EntitySceneBuildPlayerProcessor.HandleGetBuild)를 거치지 않으므로,
    // 그 핸들러가 넣는 extraScriptingDefines와 추가 BuildOptions를 여기서 직접 넣는다.
    //
    // 서버 빌드는 에디터를 "-buildTarget Win64 -standaloneBuildSubtarget Server"로 띄운 상태에서만 실행한다.
    // DotsGlobalSettings.GetPlayerType()이 에디터의 UNITY_SERVER 정의로 서버 여부를 판단하기 때문이다.
    public static class ResearchBuild
    {
        const string k_Tag = "[ResearchBuild]";

        public static void BuildServerRelease() => Run(server: true, development: false, exeName: "NetcodeSamplesServer.exe", defaultDir: "server-release");
        public static void BuildServerDev() => Run(server: true, development: true, exeName: "NetcodeSamplesServer.exe", defaultDir: "server-dev");
        public static void BuildClientDev() => Run(server: false, development: true, exeName: "NetcodeSamplesClient.exe", defaultDir: "client-dev");

        static void Run(bool server, bool development, string exeName, string defaultDir)
        {
            int exitCode = 1;
            try
            {
                exitCode = DoBuild(server, development, exeName, defaultDir) ? 0 : 1;
            }
            catch (Exception e)
            {
                Debug.LogError($"{k_Tag} EXCEPTION {e}");
            }
            if (Application.isBatchMode)
                EditorApplication.Exit(exitCode);
        }

        static bool DoBuild(bool server, bool development, string exeName, string defaultDir)
        {
            var global = DotsGlobalSettings.Instance;
            var editorIsServer = global.GetPlayerType() == DotsGlobalSettings.PlayerType.Server;
            var subtarget = EditorUserBuildSettings.standaloneBuildSubtarget;
            Debug.Log($"{k_Tag} request server={server} development={development} activeTarget={EditorUserBuildSettings.activeBuildTarget} subtarget={subtarget} editorPlayerType={global.GetPlayerType()}");

            if (server != editorIsServer)
            {
                Debug.LogError($"{k_Tag} FAILED editor player type mismatch. Start the editor with -standaloneBuildSubtarget {(server ? "Server" : "Player")}.");
                return false;
            }

            var outRoot = GetArg("researchOut");
            if (string.IsNullOrEmpty(outRoot))
                outRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", "..", "builds"));
            var outDir = Path.Combine(outRoot, defaultDir);
            Directory.CreateDirectory(outDir);

            // 제공자의 GetExtraScriptingDefines()가 이 값을 보고 NETCODE_DEBUG를 넣는다.
            EditorUserBuildSettings.development = development;

            var clientSettings = NetCodeClientSettings.instance;
            var oldClientTarget = clientSettings.ClientTarget;
            try
            {
                string[] defines;
                BuildOptions extra;
                if (server)
                {
                    var provider = global.ServerProvider;
                    if (provider == null)
                    {
                        Debug.LogError($"{k_Tag} FAILED no server settings provider.");
                        return false;
                    }
                    defines = provider.GetExtraScriptingDefines();
                    extra = provider.GetExtraBuildOptions();
                }
                else
                {
                    // 클라이언트 전용 빌드. 디스크의 설정 파일은 바꾸지 않고 메모리 값만 바꾼다(Save 호출 없음).
                    clientSettings.ClientTarget = NetCodeClientTarget.Client;
                    var provider = global.ClientProvider;
                    defines = provider.GetExtraScriptingDefines();
                    extra = provider.GetExtraBuildOptions();
                    if (!defines.Contains("UNITY_CLIENT"))
                    {
                        Debug.LogError($"{k_Tag} FAILED client provider '{provider.GetType().FullName}' did not return UNITY_CLIENT. defines={string.Join(";", defines)}");
                        return false;
                    }
                }

                var scenes = EditorBuildSettings.scenes.Where(s => s.enabled).Select(s => s.path).ToArray();
                var options = new BuildPlayerOptions
                {
                    scenes = scenes,
                    locationPathName = Path.Combine(outDir, exeName),
                    target = BuildTarget.StandaloneWindows64,
                    subtarget = (int)(server ? StandaloneBuildSubtarget.Server : StandaloneBuildSubtarget.Player),
                    options = extra | (development ? BuildOptions.Development : BuildOptions.None),
                    extraScriptingDefines = defines,
                };
                var backend = PlayerSettings.GetScriptingBackend(server ? NamedBuildTarget.Server : NamedBuildTarget.Standalone);
                Debug.Log($"{k_Tag} options scenes={scenes.Length} out={options.locationPathName} buildOptions={options.options} defines={string.Join(";", defines)} scriptingBackend={backend} clientTarget(memory)={clientSettings.ClientTarget}");

                var report = BuildPipeline.BuildPlayer(options);
                var s = report.summary;
                Debug.Log($"{k_Tag} RESULT result={s.result} errors={s.totalErrors} warnings={s.totalWarnings} sizeBytes={s.totalSize} seconds={s.totalTime.TotalSeconds:0} platform={s.platform} output={s.outputPath}");
                return s.result == BuildResult.Succeeded;
            }
            finally
            {
                clientSettings.ClientTarget = oldClientTarget;
            }
        }

        static string GetArg(string key)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i].Equals("-" + key, StringComparison.OrdinalIgnoreCase))
                    return args[i + 1];
            return null;
        }
    }
}
