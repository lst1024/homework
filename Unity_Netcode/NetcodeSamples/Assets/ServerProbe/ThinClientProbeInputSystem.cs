using Samples.HelloNetcode;
using Unity.Entities;
using Unity.Mathematics;
using Unity.NetCode;

namespace ServerProbe
{
    // ─────────────────────────────────────────────────────────────────────────────
    // 씬 클라이언트 World가 서버에 입력을 보내게 한다.
    // 로그를 남기는 시스템이 아니라 부하(입력)를 만드는 쪽이다.
    //
    // 하는 일
    //   (1) 입력을 담을 엔티티를 만들어 연결에 붙인다.
    //   (2) 갱신될 때마다 그 엔티티에 입력을 써 넣는다.
    //
    // 따로 만든 이유
    //   샘플의 ThinClientInputSystem(06_ThinClients)은 EnableThinClients 컴포넌트를 요구하는데
    //   CharacterController 서브씬에는 그 컴포넌트가 없어 돌지 않는다. 그래서 같은 방식의 시스템을 따로 둔다.
    //
    // 입력은 (시드, World 번호, 서버 틱)만으로 정해진다. 프레임률에 좌우되지 않는다.
    // ─────────────────────────────────────────────────────────────────────────────
    [UpdateInGroup(typeof(HelloNetcodeInputSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.ThinClientSimulation)]
    public partial class ThinClientProbeInputSystem : SystemBase
    {
        const uint k_DirectionChangeTicks = 60u; // 이동 방향을 바꾸는 주기(서버 틱)
        const uint k_JumpIntervalTicks = 100u;   // 점프하는 주기(서버 틱)
        const uint k_SeedPerProcess = 1000u;     // 프로세스 시드(-probeSeed)에 곱하는 값. 예: 시드 2, World 12 → 2012

        uint m_Seed;           // 이 World의 입력 시드
        uint m_LastJumpWindow; // 마지막으로 본 점프 구간 번호(서버 틱 ÷ 점프 주기). 구간이 바뀔 때 한 번만 점프한다

        protected override void OnCreate()
        {
            if (!ProbeArgs.Enabled)
            {
                Enabled = false;
                return;
            }

            // 아래 세 컴포넌트가 모두 있을 때만 갱신된다.
            RequireForUpdate<EnableCharacterController>();
            RequireForUpdate<NetworkId>();
            RequireForUpdate<NetworkStreamInGame>();

            m_Seed = ProbeArgs.Seed * k_SeedPerProcess + TrailingNumber(World.Name);
        }

        // 이름 끝에 붙은 숫자를 읽는다. "ThinClientWorld12" → 12. 숫자가 없으면 0.
        static uint TrailingNumber(string name)
        {
            uint number = 0;
            uint placeValue = 1; // 1의 자리부터 거꾸로 읽는다
            for (int i = name.Length - 1; i >= 0 && char.IsDigit(name[i]); i--)
            {
                number += (uint)(name[i] - '0') * placeValue;
                placeValue *= 10;
            }
            return number;
        }

        protected override void OnUpdate()
        {
            // (1) 입력 엔티티가 아직 없으면 만든다.
            if (SystemAPI.TryGetSingleton<CommandTarget>(out var commandTarget) && commandTarget.targetEntity == Entity.Null)
            {
                CreateInputEntity();
            }

            var networkTime = SystemAPI.GetSingleton<NetworkTime>();
            if (!networkTime.ServerTick.IsValid)
            {
                return;
            }
            var tick = networkTime.ServerTick.TickIndexForValidTick;

            // (2) 이번 틱의 입력을 정한다.
            //     방향: 60틱마다 (시드, 구간 번호)의 해시로 네 방향 중 하나를 고른다.
            //     점프: 100틱 구간이 바뀌는 순간에 한 번.
            var direction = math.hash(new uint2(m_Seed, tick / k_DirectionChangeTicks)) % 4u;
            var jumpWindow = tick / k_JumpIntervalTicks;
            var jump = jumpWindow != m_LastJumpWindow;
            m_LastJumpWindow = jumpWindow;

            foreach (var input in SystemAPI.Query<RefRW<CharacterControllerPlayerInput>>())
            {
                input.ValueRW = default; // 이전 입력을 지운다
                switch (direction)
                {
                    case 0: input.ValueRW.Movement.x = -1; break;  // x-
                    case 1: input.ValueRW.Movement.x = 1; break;   // x+
                    case 2: input.ValueRW.Movement.y = 1; break;   // y+
                    default: input.ValueRW.Movement.y = -1; break; // y-
                }
                if (jump)
                {
                    input.ValueRW.Jump.Set();
                }
            }
        }

        void CreateInputEntity()
        {
            // 씬 클라이언트는 ghost를 만들지 않으므로 입력 컴포넌트와 그 버퍼를 가진 엔티티를 직접 만들고
            // CommandTarget을 직접 지정한다. 서버 쪽 CommandTarget은 샘플의 SpawnPlayerSystem이 지정한다.
            var inputEntity = EntityManager.CreateEntity();
            EntityManager.AddComponent<CharacterControllerPlayerInput>(inputEntity);

            var connectionId = SystemAPI.GetSingleton<NetworkId>().Value;
            EntityManager.AddComponentData(inputEntity, new GhostOwner { NetworkId = connectionId });
            EntityManager.AddComponent<InputBufferData<CharacterControllerPlayerInput>>(inputEntity);

            SystemAPI.SetSingleton(new CommandTarget { targetEntity = inputEntity });
            UnityEngine.Debug.Log($"[ThinProbe][{World.Name}] input entity created for connection {connectionId}, seed {m_Seed}");
        }
    }
}
