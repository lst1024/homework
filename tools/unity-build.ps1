# 샘플 프로젝트를 배치 모드로 빌드한다. 한 번에 하나만 실행한다.
# 사용: powershell -File tools\unity-build.ps1 -Kind ServerRelease|ServerDev|ClientDev
param([Parameter(Mandatory=$true)][ValidateSet('ServerRelease','ServerDev','ClientDev')][string]$Kind)
$ErrorActionPreference = 'Stop'
$root  = Split-Path -Parent $PSScriptRoot
$proj  = Join-Path $root 'Unity_Netcode\NetcodeSamples'
$unity = 'C:\Program Files\Unity\Hub\Editor\6000.5.1f1\Editor\Unity.exe'
if (Test-Path (Join-Path $proj 'Temp\UnityLockfile')) { throw '프로젝트가 열려 있음 (Temp\UnityLockfile)' }
$sub = if ($Kind -like 'Server*') { 'Server' } else { 'Player' }
$out = Join-Path $root ('measurements\_env\build-' + $Kind + '-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $out | Out-Null
$log = Join-Path $out 'build.log'
$argList = @('-batchmode','-quit','-projectPath',"`"$proj`"",'-buildTarget','Win64','-standaloneBuildSubtarget',$sub,
             '-executeMethod',"Research.Build.ResearchBuild.Build$Kind",'-researchOut',"`"$(Join-Path $root 'builds')`"",'-logFile',"`"$log`"")
"start=$(Get-Date -Format o)" | Out-File (Join-Path $out 'run.txt') -Encoding utf8
"args=$($argList -join ' ')" | Out-File (Join-Path $out 'run.txt') -Append -Encoding utf8
$p = Start-Process -FilePath $unity -ArgumentList $argList -Wait -PassThru -NoNewWindow
"end=$(Get-Date -Format o)" | Out-File (Join-Path $out 'run.txt') -Append -Encoding utf8
"exit=$($p.ExitCode)" | Out-File (Join-Path $out 'run.txt') -Append -Encoding utf8
"exit=$($p.ExitCode)"; "out=$out"
