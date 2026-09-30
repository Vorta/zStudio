param(
    [Parameter(Mandatory, Position=0)]
    [ValidateSet('check','arm','status','read','acknowledge','stop','resume','listen')][string]$Action,
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$Pr,
    [string]$Head, [string]$Codex, [string]$Gh, [string]$Thread,
    [string]$Notice, [string]$Snapshot,
    [switch]$ReleaseOnApproval, [switch]$NotifyTest, [switch]$Claude
)
$ErrorActionPreference = 'Stop'
$workspace = Split-Path -Parent $PSScriptRoot
$project = Join-Path $PSScriptRoot 'zStudio.PrWatch/zStudio.PrWatch.csproj'
# Keep stdout for the watcher itself: a Claude Code Monitor turns every stdout line into a notification.
$build = dotnet build $project -c Release --nologo --verbosity quiet 2>&1
if ($LASTEXITCODE -ne 0) { [Console]::Error.WriteLine(($build | Out-String)); throw 'PR watcher build failed.' }
$watchArgs = @($Action, '--workspace', $workspace, '--pr', [string]$Pr)
foreach ($pair in @(@('--head',$Head), @('--codex',$Codex), @('--gh',$Gh), @('--thread',$Thread), @('--notice',$Notice), @('--snapshot',$Snapshot))) {
    if ($pair[1]) { $watchArgs += $pair }
}
if ($ReleaseOnApproval) { $watchArgs += '--release-on-approval' }
if ($NotifyTest) { $watchArgs += '--notify-test' }
if ($Claude) { $watchArgs += '--claude' }
$dll = Join-Path $PSScriptRoot 'zStudio.PrWatch/bin/Release/net10.0/Recoil.Zbd.PrWatch.dll'
if ($Action -eq 'listen') {
    # Listen from a private content-addressed copy so solution builds remain possible while it waits.
    $runtimeArgs = @('runtime', '--workspace', $workspace, '--pr', [string]$Pr)
    if ($Claude) { $runtimeArgs += '--claude' }
    $dll = dotnet $dll @runtimeArgs | Select-Object -Last 1
    if ($LASTEXITCODE -ne 0 -or -not $dll) { throw 'Could not prepare the PR watcher runtime copy.' }
}
dotnet $dll @watchArgs
if ($LASTEXITCODE -ne 0) { throw "PR watcher $Action failed. See the diagnostic above." }
