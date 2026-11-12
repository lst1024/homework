using System.Collections.Generic;
using System.IO;
using System.Text;
using Unity.Collections;
using Unity.Entities;
using Unity.NetCode;
using UnityEngine;

namespace ServerProbe
{
    // ─────────────────────────────────────────────────────────────────────────────
    // World별 시스템 순서 덤프. ProbeDriver가 -probeDump 시각에 한 번 부른다.
    //
    // 파일 위치: 그 프로세스의 -probeOut 폴더
    //   서버 프로세스       → measurements\<실행 ID>\server\systems-ServerWorld.tsv
    //   클라이언트 프로세스 → measurements\<실행 ID>\clientN\systems-ClientWorld.tsv, systems-ThinClientWorld1.tsv
    //
    // World마다 systems-<World 이름>.tsv 를 만든다. 한 줄이 시스템(또는 그룹) 하나이고,
    // 그룹 안의 줄 순서는 그 그룹이 돌려준 시스템 순서(GetAllSystems)다.
    // 프로브 시스템이 의도한 자리에 놓였는지도 이 파일로 확인한다.
    //
    // 열
    //   world, flags   World 이름과 종류 플래그
    //   depth          그룹 중첩 깊이 (최상위 그룹이 0)
    //   order          부모 그룹 안에서의 순번
    //   system         시스템 타입 이름
    //   kind           group / managed / unmanaged
    //   enabled        켜져 있으면 1
    //   parent         부모 그룹 이름 (최상위 그룹은 "-")
    // ─────────────────────────────────────────────────────────────────────────────
    public static class SystemOrderDump
    {
        // 모든 World를 덤프한다. 씬 클라이언트 World는 첫 번째 것만 남긴다.
        public static void DumpAllWorlds()
        {
            bool thinDumped = false; // 씬 클라이언트 World를 이미 하나 덤프했는가
            foreach (var world in World.All)
            {
                if (world.IsThinClient())
                {
                    if (thinDumped)
                    {
                        continue;
                    }
                    thinDumped = true;
                }

                try
                {
                    DumpWorld(world);
                }
                catch (System.Exception e)
                {
                    // 덤프가 실패해도 실행은 계속한다.
                    Debug.LogError($"[ProbeDriver] system dump failed for '{world.Name}': {e}");
                }
            }
        }

        static void DumpWorld(World world)
        {
            // 핸들 → 관리형 시스템 객체. 여기 없는 핸들은 비관리형(ISystem)으로 본다.
            var managedSystems = new Dictionary<SystemHandle, ComponentSystemBase>();
            foreach (var system in world.Systems)
            {
                managedSystems[system.SystemHandle] = system;
            }

            var table = new StringBuilder();
            table.Append("world\tflags\tdepth\torder\tsystem\tkind\tenabled\tparent\n");

            // 최상위 그룹 세 개를 차례로 내려가며 적는다.
            int rowCount = 0; // 적은 줄 수
            WriteGroup(world, world.GetExistingSystemManaged<InitializationSystemGroup>(), 0, managedSystems, table, ref rowCount);
            WriteGroup(world, world.GetExistingSystemManaged<SimulationSystemGroup>(), 0, managedSystems, table, ref rowCount);
            WriteGroup(world, world.GetExistingSystemManaged<PresentationSystemGroup>(), 0, managedSystems, table, ref rowCount);

            // 덤프 파일이 만들어지는 곳: <-probeOut 폴더>\systems-<World 이름>.tsv (있으면 덮어쓴다)
            // 예: measurements\<실행 ID>\server\systems-ServerWorld.tsv
            var file = Path.Combine(ProbeArgs.OutDir, "systems-" + world.Name + ".tsv");
            File.WriteAllText(file, table.ToString(), new UTF8Encoding(false));
            Debug.Log($"[ProbeDriver] system dump world='{world.Name}' flags={world.Flags} rows={rowCount} file='{file}'");
        }

        // 그룹 하나의 시스템들을 순서대로 적고, 하위 그룹은 재귀로 내려간다.
        static void WriteGroup(World world, ComponentSystemGroup group, int depth,
            Dictionary<SystemHandle, ComponentSystemBase> managedSystems, StringBuilder table, ref int rowCount)
        {
            if (group == null)
            {
                return;
            }
            var groupName = group.GetType().FullName;

            // 최상위 그룹은 자기 자신의 줄을 먼저 적는다(부모가 없으므로 parent는 "-").
            if (depth == 0)
            {
                table.Append(world.Name).Append('\t')
                    .Append(world.Flags).Append("\t0\t0\t")
                    .Append(groupName).Append("\tgroup\t")
                    .Append(group.Enabled ? 1 : 0).Append("\t-\n");
                rowCount++;
            }

            var systems = group.GetAllSystems(Allocator.Temp);
            for (int order = 0; order < systems.Length; order++)
            {
                var handle = systems[order];

                // 시스템의 이름과 종류를 알아낸다.
                string name;
                string kind;
                ComponentSystemGroup childGroup = null;
                if (managedSystems.TryGetValue(handle, out var managed))
                {
                    name = managed.GetType().FullName;
                    childGroup = managed as ComponentSystemGroup;
                    kind = childGroup != null ? "group" : "managed";
                }
                else
                {
                    var typeIndex = world.Unmanaged.GetSystemTypeIndex(handle);
                    name = TypeManager.GetSystemName(typeIndex).ToString();
                    kind = "unmanaged";
                }
                var enabled = world.Unmanaged.ResolveSystemStateRef(handle).Enabled;

                table.Append(world.Name).Append('\t')
                    .Append(world.Flags).Append('\t')
                    .Append(depth + 1).Append('\t')
                    .Append(order).Append('\t')
                    .Append(name).Append('\t')
                    .Append(kind).Append('\t')
                    .Append(enabled ? 1 : 0).Append('\t')
                    .Append(groupName).Append('\n');
                rowCount++;

                if (childGroup != null)
                {
                    WriteGroup(world, childGroup, depth + 1, managedSystems, table, ref rowCount);
                }
            }
        }
    }
}
