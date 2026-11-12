# ─────────────────────────────────────────────────────────────────────────────
# 측정 실행 한 회차를 돌린다.
# 서버 1개와 클라이언트 프로세스 N개를 띄우고, 끝날 때까지 지켜본 뒤, 기록을 measurements\<RunId>\ 에 모은다.
#
# 사용
#   powershell -File tools\probe-run.ps1 -RunId 20261111-smoke-2 -ClientProcs 1 -Thin 2 -Seconds 40 [-Profile env2]
#
# 규칙
#   - 한 번에 하나만 실행한다 (같은 UDP 포트를 쓴다).
#   - 이 스크립트가 직접 띄운 프로세스만 종료한다.
#   - 서버 연결 수 기대값 = ClientProcs * (Thin + 1).
#     클라이언트 프로세스마다 일반 클라이언트 World 하나가 씬 클라이언트와 함께 접속하기 때문이다.
#
# 진행 순서
#   1. 준비       : 실행 파일·포트·실행 ID 확인, 기록 폴더 생성
#   2. 조건 기록  : 실행 조건, 커밋, 빌드 시각을 run.txt 에 적는다
#   3. 서버 시작  : 서버를 띄우고 포트를 열 때까지 기다린다
#   4. 클라이언트 시작
#   5. 지켜보기   : 모두 끝날 때까지 5초마다 CPU·메모리를 cpu.csv 에 적는다
#   6. 마무리     : 종료 코드를 적고, 제한 시간 뒤에도 남은 프로세스는 종료한다
#
# 이 스크립트가 measurements\<RunId>\ 에 만드는 것
#   run.txt        Note 함수가 한 줄씩 덧붙인다 (실행 조건, 커밋, 실행 파일, 프로세스 시작·종료)
#   cpu.csv        5단계의 표본 루프가 5초마다 덧붙인다 (프로세스별 CPU·메모리)
#   <Profile>.json -Profile 을 줬을 때 tools\netprofiles\ 의 설정을 복사한다
# 이 스크립트가 경로만 정해 주고 게임 프로세스가 만드는 것
#   server.log, clientN.log   -logFile 인자. Unity 플레이어가 쓴다
#   server\, clientN\         -probeOut 인자. 프로브 코드(Assets\ServerProbe)가 쓴다. 파일별 내용은 ProbeCore.cs 머리 주석 참고
# ─────────────────────────────────────────────────────────────────────────────
param(
  [Parameter(Mandatory=$true)]
  [string]$RunId,                          # 실행 ID. measurements\ 아래 폴더 이름이 된다. 이미 있으면 멈춘다
  [string]$Scene = 'CharacterController',  # 띄울 씬 이름
  [int]$Port = 7979,                       # 서버 UDP 포트
  [int]$ClientProcs = 1,                   # 클라이언트 프로세스 수
  [int]$Thin = 0,                          # 클라이언트 프로세스 하나가 만드는 씬 클라이언트 World 수
  [string]$Profile = '',                   # 네트워크 프로파일 이름 (tools\netprofiles\<이름>.json). 비우면 시뮬레이터 설정을 넘기지 않는다
  [int]$Seconds = 40,                      # 클라이언트 실행 시간(초)
  [int]$Fps = 60,                          # 클라이언트 프레임 상한
  [string]$ServerBuild = 'server-release', # builds\ 아래의 서버 빌드 폴더
  [string]$ClientBuild = 'client-dev',     # builds\ 아래의 클라이언트 빌드 폴더
  [switch]$ClientWindow                    # 주면 클라이언트를 창으로 띄운다 (기본은 창 없는 배치 모드)
)
$ErrorActionPreference = 'Stop'

# 시간 설정(초)
$ServerExtraSeconds   = 20  # 서버는 클라이언트보다 이만큼 더 오래 돈다
$ServerStartupWait    = 6   # 서버를 띄운 뒤 클라이언트를 띄우기 전에 기다리는 시간
$ClientStartInterval  = 1   # 클라이언트 프로세스를 띄우는 간격
$CpuSampleInterval    = 5   # cpu.csv 표본 간격
$WatchTimeoutExtra    = 90  # 지켜보기 제한 시간 = Seconds + 이 값. 넘으면 남은 프로세스를 종료한다


# ── 1. 준비 ──────────────────────────────────────────────────────────────────

$root = Split-Path -Parent $PSScriptRoot      # 저장소 루트 (tools 의 위 폴더)
$out  = Join-Path $root "measurements\$RunId" # 이번 실행의 기록 폴더

$serverBuildDir = Join-Path $root "builds\$ServerBuild"
$clientBuildDir = Join-Path $root "builds\$ClientBuild"
$serverExe = Join-Path $serverBuildDir 'NetcodeSamplesServer.exe'
$clientExe = Join-Path $clientBuildDir 'NetcodeSamplesClient.exe'

# 시작하기 전에 막히는 조건을 먼저 확인한다. 하나라도 걸리면 아무것도 띄우지 않는다.
if (Test-Path $out)
{
  throw "이미 있는 실행 ID: $out"
}
if (-not (Test-Path $serverExe))
{
  throw "서버 빌드 없음: $serverExe"
}
if (-not (Test-Path $clientExe))
{
  throw "클라이언트 빌드 없음: $clientExe"
}
$portUsers = @(Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue)
if ($portUsers.Count -gt 0)
{
  throw "UDP $Port 사용 중: PID $($portUsers.OwningProcess -join ',')"
}

New-Item -ItemType Directory -Path $out | Out-Null

# run.txt 에 시각을 붙여 한 줄 적고, 같은 내용을 화면에도 낸다.
$runFile = Join-Path $out 'run.txt'
function Note($text)
{
  "$(Get-Date -Format 'HH:mm:ss.fff') $text" | Out-File $runFile -Append -Encoding utf8
  Write-Output $text
}

# 프로파일을 줬으면 설정 파일을 기록 폴더에 복사하고, 클라이언트에는 그 사본의 경로를 넘긴다.
$profileArgs = @()
if ($Profile -ne '')
{
  $profileSource = Join-Path $PSScriptRoot "netprofiles\$Profile.json"
  if (-not (Test-Path $profileSource))
  {
    throw "프로파일 없음: $profileSource"
  }
  $profileCopy = Join-Path $out "$Profile.json"
  Copy-Item $profileSource $profileCopy
  $profileArgs = @('--loadNetworkSimulatorJsonFile', $profileCopy)
}


# ── 2. 조건 기록 ─────────────────────────────────────────────────────────────

$expectedConnections = $ClientProcs * ($Thin + 1)
Note "runId=$RunId scene=$Scene port=$Port clientProcs=$ClientProcs thinPerProc=$Thin profile=$Profile seconds=$Seconds fps=$Fps expectedConnections=$expectedConnections"

# 어느 소스로 잰 값인지: 커밋과, 커밋되지 않은 변경의 개수
$commit     = git -C $root rev-parse --short HEAD
$dirtyCount = @(git -C $root status --short).Count
Note "commit=$commit dirty=$dirtyCount"

# 어느 빌드로 잰 값인지: 증분 빌드는 exe를 다시 쓰지 않으므로 프로브 DLL의 시각을 남긴다.
$serverDll = Join-Path $serverBuildDir 'NetcodeSamplesServer_Data\Managed\ServerProbe.dll'
$clientDll = Join-Path $clientBuildDir 'NetcodeSamplesClient_Data\Managed\ServerProbe.dll'
Note "serverExe=$serverExe probeDllWritten=$((Get-Item $serverDll).LastWriteTime.ToString('o'))"
Note "clientExe=$clientExe probeDllWritten=$((Get-Item $clientDll).LastWriteTime.ToString('o'))"


# ── 3. 서버 시작 ─────────────────────────────────────────────────────────────

$procs = @() # 이 스크립트가 띄운 프로세스 목록. 항목: Name(server, client1, ...), P(프로세스 객체)

$serverArgs = @(
  '-batchmode', '-nographics',
  '-scene', $Scene,
  '-port', $Port,
  '-logFile', (Join-Path $out 'server.log'),
  '-probeOut', (Join-Path $out 'server'),
  '-probeRunId', $RunId,
  '-probeDuration', ($Seconds + $ServerExtraSeconds)
)
$server = Start-Process -FilePath $serverExe -ArgumentList $serverArgs -PassThru
$procs += [pscustomobject]@{ Name = 'server'; P = $server }
Note "server pid=$($server.Id) args=$($serverArgs -join ' ')"

# 서버가 포트를 열 시간을 준 뒤, 그 포트를 누가 잡고 있는지 적는다(서버 PID여야 한다).
Start-Sleep -Seconds $ServerStartupWait
$portOwners = @(Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue)
Note "udp$Port owners=$($portOwners.OwningProcess -join ',')"


# ── 4. 클라이언트 시작 ───────────────────────────────────────────────────────

for ($i = 1; $i -le $ClientProcs; $i++)
{
  $clientArgs = @()
  if (-not $ClientWindow)
  {
    $clientArgs += @('-batchmode', '-nographics')
  }
  $clientArgs += @(
    '-scene', $Scene,
    '-port', $Port,
    '-logFile', (Join-Path $out "client$i.log"),
    '-probeOut', (Join-Path $out "client$i"),
    '-probeRunId', $RunId,
    '-probeThin', $Thin,
    '-probeSeed', $i,          # 프로세스 번호를 시드로 써서 프로세스마다 입력이 다르게 나온다
    '-probeFps', $Fps,
    '-probeDuration', $Seconds
  )
  $clientArgs += $profileArgs

  $client = Start-Process -FilePath $clientExe -ArgumentList $clientArgs -PassThru
  $procs += [pscustomobject]@{ Name = "client$i"; P = $client }
  Note "client$i pid=$($client.Id) args=$($clientArgs -join ' ')"
  Start-Sleep -Seconds $ClientStartInterval
}


# ── 5. 지켜보기 ──────────────────────────────────────────────────────────────
# 모든 프로세스가 끝나거나 제한 시간이 될 때까지, 5초마다 프로세스별 CPU·메모리를 cpu.csv 에 적는다.
#
# cpu.csv 열
#   time                 PC 시각
#   name, pid            프로세스 이름(server, client1, ...)과 PID
#   cpu_seconds_total    누적 CPU 시간(초)
#   cpu_pct_of_one_core  직전 표본 이후의 평균 CPU 사용률. 코어 1개 기준 %. 첫 표본은 비어 있다
#   working_set_mb       메모리 사용량(MB)

$cpuFile = Join-Path $out 'cpu.csv'
'time,name,pid,cpu_seconds_total,cpu_pct_of_one_core,working_set_mb' | Out-File $cpuFile -Encoding utf8

$previous       = @{}                                 # 프로세스 이름 → 직전 표본 @(누적 CPU 초, 경과 초)
$stopwatch      = [Diagnostics.Stopwatch]::StartNew() # 지켜보기를 시작한 뒤의 경과 시간
$timeoutSeconds = $Seconds + $WatchTimeoutExtra

while ($stopwatch.Elapsed.TotalSeconds -lt $timeoutSeconds)
{
  $aliveCount = 0
  foreach ($entry in $procs)
  {
    $process = $entry.P
    $process.Refresh()
    if ($process.HasExited)
    {
      continue
    }
    $aliveCount++

    $cpuSeconds = $process.TotalProcessorTime.TotalSeconds
    $elapsed    = $stopwatch.Elapsed.TotalSeconds

    # 사용률 = (CPU 시간이 늘어난 양) / (그동안 흐른 시간). 직전 표본이 있어야 계산할 수 있다.
    $cpuPercent = ''
    if ($previous.ContainsKey($entry.Name))
    {
      $previousCpuSeconds = $previous[$entry.Name][0]
      $previousElapsed    = $previous[$entry.Name][1]
      $cpuPercent = [math]::Round(100 * ($cpuSeconds - $previousCpuSeconds) / ($elapsed - $previousElapsed), 1)
    }
    $previous[$entry.Name] = @($cpuSeconds, $elapsed)

    $time     = Get-Date -Format 'HH:mm:ss'
    $cpuTotal = [math]::Round($cpuSeconds, 2)
    $memoryMb = [math]::Round($process.WorkingSet64 / 1MB, 0)
    "$time,$($entry.Name),$($process.Id),$cpuTotal,$cpuPercent,$memoryMb" | Out-File $cpuFile -Append -Encoding utf8
  }

  if ($aliveCount -eq 0)
  {
    break
  }
  Start-Sleep -Seconds $CpuSampleInterval
}


# ── 6. 마무리 ────────────────────────────────────────────────────────────────
# 정상이라면 모든 프로세스가 -probeDuration 으로 스스로 끝나 있다.

foreach ($entry in $procs)
{
  $process = $entry.P
  $process.Refresh()
  if ($process.HasExited)
  {
    Note "$($entry.Name) pid=$($process.Id) exit=$($process.ExitCode)"
  }
  else
  {
    Note "$($entry.Name) pid=$($process.Id) 가 제한 시간 뒤에도 살아 있어 종료함"
    Stop-Process -Id $process.Id -Force
  }
}

Note "done out=$out"
