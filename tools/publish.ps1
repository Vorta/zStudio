param([string]$Output = 'artifacts/zStudio-win-x64', [string[]]$PreserveSuperseded = @())
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$stage = $build = $stagedArchive = $null
Push-Location $repository
try {
    # Every path we replace or remove must stay inside this repository's artifacts.
    $artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
    $outputPath = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Output, $repository))
    function Assert-ArtifactPath([string]$Path) {
        $fullPath = [IO.Path]::GetFullPath($Path)
        if (-not $fullPath.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Publish paths must be below $artifacts : $fullPath"
        }
        for ($ancestor = $fullPath; $ancestor; $ancestor = Split-Path -Parent $ancestor) {
            if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Publish paths must not use directory links: $ancestor"
            }
        }
    }
    function Assert-ArtifactTree([string]$Path) {
        Assert-ArtifactPath $Path
        if (Test-Path -LiteralPath $Path -PathType Container) {
            foreach ($entry in Get-ChildItem -LiteralPath $Path -Force -Recurse) { Assert-ArtifactPath $entry.FullName }
        }
    }
    # A package folder holds zStudio.exe with its licence and notices beside it, or (the 0.9.6 layout, which the
    # first single-file publish replaces) zStudio.exe with the app in dependencies/.
    function Test-StudioPackage([string]$Path) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path 'zStudio.exe') -PathType Leaf)) { return $false }
        @('LICENSE', 'THIRD-PARTY-NOTICES.md').Where({ -not (Test-Path -LiteralPath (Join-Path $Path $_) -PathType Leaf) }).Count -eq 0 -or
            (Test-Path -LiteralPath (Join-Path $Path 'dependencies/zStudio.Desktop.runtimeconfig.json') -PathType Leaf)
    }
    Assert-ArtifactTree $outputPath
    $preservedPaths = @($PreserveSuperseded | ForEach-Object { $full = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($_, $repository)); Assert-ArtifactPath $full; $full })
    if ((Test-Path -LiteralPath $outputPath) -and -not (Test-StudioPackage $outputPath)) {
        throw "Refusing to replace a folder that is not a Studio publish: $outputPath"
    }
    $stage = Join-Path $artifacts ('.studio-publish-' + [Guid]::NewGuid().ToString('N'))
    $build = $stage + '.bin'
    Assert-ArtifactPath $stage; Assert-ArtifactPath $build
    New-Item -ItemType Directory -Path $stage, $build -Force | Out-Null
    # ReadyToRun images an earlier publish configuration left (composite stubs) must never be bundled.
    Get-ChildItem -LiteralPath src/zStudio.Desktop/obj -Directory -Recurse -Filter R2R -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force
    dotnet publish src/zStudio.Desktop/zStudio.Desktop.csproj -c Release -r win-x64 -p:StudioPackage=true -p:RestoreLockedMode=true -o $build
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    $published = @(Get-ChildItem -LiteralPath $build -Force)
    if ($published.Count -ne 1 -or $published[0].Name -ne 'zStudio.Desktop.exe') {
        throw "The single-file publish must produce only zStudio.Desktop.exe, not: $($published.Name -join ', ')"
    }
    # The bundle keeps its assembly name inside; only the file is renamed.
    Move-Item -LiteralPath $published[0].FullName -Destination (Join-Path $stage 'zStudio.exe')
    Copy-Item -LiteralPath LICENSE, THIRD-PARTY-NOTICES.md -Destination $stage
    & (Join-Path $PSScriptRoot 'package-blender.ps1') -Destination $stage
    if (@(Get-ChildItem -LiteralPath $stage -Force).Count -ne 4) { throw 'The package must hold only zStudio.exe, LICENSE, THIRD-PARTY-NOTICES.md and the Blender extension.' }
    [xml]$properties = Get-Content -LiteralPath 'Directory.Build.props'
    $version = [string]$properties.Project.PropertyGroup.Version
    if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'Version must be a release version.' }
    $parent = Split-Path -Parent $outputPath
    $archive = Join-Path $parent "zStudio-$version-win-x64.zip"
    $stagedArchive = $stage + '.zip'
    $superseded = $outputPath + '.previous-' + [Guid]::NewGuid().ToString('N')
    $supersededArchive = $archive + '.previous-' + [Guid]::NewGuid().ToString('N')
    foreach ($path in @($archive, $stagedArchive, $superseded, $supersededArchive)) { Assert-ArtifactPath $path }
    if (Test-Path -LiteralPath $archive -PathType Container) { throw "Archive path is a directory: $archive" }
    Assert-ArtifactTree $stage
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $stagedArchive
    # A normal verification rejection must leave both current artifacts byte-for-byte intact.
    & (Join-Path $PSScriptRoot 'verify-package.ps1') -Directory $stage -Archive $stagedArchive -ExpectedVersion $version
    Assert-ArtifactTree $outputPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $oldOutputMoved = $oldArchiveMoved = $newOutputInstalled = $newArchiveInstalled = $false
    try {
        # Keep both backups until the entire pair has been installed. Non-overwriting
        # moves also reject a destination created after validation instead of merging it.
        if (Test-Path -LiteralPath $outputPath) { [IO.Directory]::Move($outputPath, $superseded); $oldOutputMoved = $true }
        if (Test-Path -LiteralPath $archive) { [IO.File]::Move($archive, $supersededArchive); $oldArchiveMoved = $true }
        [IO.Directory]::Move($stage, $outputPath); $newOutputInstalled = $true
        [IO.File]::Move($stagedArchive, $archive); $newArchiveInstalled = $true
    } catch {
        $publicationFailure = $_
        $rollbackErrors = [Collections.Generic.List[string]]::new()
        # Try each restoration even if another fails. A failed rollback retains its
        # backup at the reported path; temporary cleanup never removes those backups.
        try { if ($newArchiveInstalled) { [IO.File]::Move($archive, $stagedArchive) } }
        catch { $rollbackErrors.Add("Cannot remove new archive at ${archive}: $_") }
        try { if ($oldArchiveMoved) { [IO.File]::Move($supersededArchive, $archive) } }
        catch { $rollbackErrors.Add("Previous archive retained at ${supersededArchive}: $_") }
        try { if ($newOutputInstalled) { [IO.Directory]::Move($outputPath, $stage) } }
        catch { $rollbackErrors.Add("Cannot remove new package at ${outputPath}: $_") }
        try { if ($oldOutputMoved) { [IO.Directory]::Move($superseded, $outputPath) } }
        catch { $rollbackErrors.Add("Previous package retained at ${superseded}: $_") }
        if ($rollbackErrors.Count) { throw "Publication failed: $publicationFailure`nRollback needs recovery:`n$($rollbackErrors -join "`n")" }
        throw $publicationFailure
    }
    # Delete this and any earlier superseded Studio output, and older ZIPs. A build
    # still running from a superseded folder is reported and removed by the next publish.
    foreach ($stale in @(Get-ChildItem -LiteralPath $parent -Directory -Filter ((Split-Path -Leaf $outputPath) + '.previous-*'))) {
        Assert-ArtifactTree $stale.FullName
        if ($stale.FullName -in $preservedPaths) { Write-Warning "Retaining explicitly protected superseded folder: $($stale.FullName)"; continue }
        # A delete that stopped at a running zStudio.exe leaves only that exe (its companions sort first): still ours.
        $leftover = @(Get-ChildItem -LiteralPath $stale.FullName -Force)
        $partial = $leftover.Count -eq 1 -and $leftover[0].Name -eq 'zStudio.exe' -and
            (Get-Item -LiteralPath $leftover[0].FullName).VersionInfo.ProductName -eq 'zStudio'
        if (-not $partial -and -not (Test-StudioPackage $stale.FullName)) { Write-Warning "Not deleting a folder that is not a Studio publish: $($stale.FullName)"; continue }
        try { Remove-Item -LiteralPath $stale.FullName -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "Superseded build is still in use and will be deleted by the next publish: $($stale.FullName)" }
    }
    foreach ($zip in @(Get-ChildItem -LiteralPath $parent -File -Filter 'zStudio-*-win-x64.zip' | Where-Object { $_.FullName -ne [IO.Path]::GetFullPath($archive) })) {
        Assert-ArtifactPath $zip.FullName; Remove-Item -LiteralPath $zip.FullName -Force
    }
    foreach ($backup in @(Get-ChildItem -LiteralPath $parent -File -Filter 'zStudio-*-win-x64.zip.previous-*')) {
        Assert-ArtifactPath $backup.FullName; Remove-Item -LiteralPath $backup.FullName -Force
    }
    Get-FileHash -LiteralPath $archive -Algorithm SHA256
} finally {
    foreach ($temporary in @($stage, $build, $stagedArchive)) {
        if ($temporary -and (Test-Path -LiteralPath $temporary)) {
            try { Assert-ArtifactTree $temporary; Remove-Item -LiteralPath $temporary -Recurse -Force }
            catch { Write-Warning "Temporary publish output could not be removed at ${temporary}: $_" }
        }
    }
    Pop-Location
}
