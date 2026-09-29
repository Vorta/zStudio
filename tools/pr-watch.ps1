param(
    [Parameter(Mandatory, Position=0)]
    [ValidateSet('check','arm','status','read','acknowledge','stop','resume')][string]$Action,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$Pr,
    [string]$Head, [string]$Codex, [string]$Gh, [string]$Thread,
    [string]$Notice, [string]$Snapshot,
    [switch]$ReleaseOnApproval, [switch]$NotifyTest
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'zStudio.PrWatch/zStudio.PrWatch.csproj'
dotnet build $project -c Release --nologo --verbosity quiet
if ($LASTEXITCODE -ne 0) { throw 'PR watcher build failed.' }
$watchArgs = @($Action, '--workspace', $workspace, '--pr', [string]$Pr)
foreach ($pair in @(@('--head',$Head), @('--codex',$Codex), @('--gh',$Gh), @('--thread',$Thread), @('--notice',$Notice), @('--snapshot',$Snapshot))) {
    if ($pair[1]) { $watchArgs += $pair }
}
if ($ReleaseOnApproval) { $watchArgs += '--release-on-approval' }
if ($NotifyTest) { $watchArgs += '--notify-test' }
dotnet (Join-Path $PSScriptRoot 'zStudio.PrWatch/bin/Release/net10.0/Recoil.Zbd.PrWatch.dll') @watchArgs
if ($LASTEXITCODE -ne 0) { throw "PR watcher $Action failed. See the diagnostic above." }
