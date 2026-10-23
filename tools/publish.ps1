param([string]$Output = 'artifacts/zStudio-win-x64', [string[]]$PreserveSuperseded = @())
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
Push-Location $repository
try {
    # Every path we replace or remove must stay inside this repository's artifacts.
    $artifacts = [IO.Path]::GetFullPath((Join-Path $repository 'artifacts'))
    $outputPath = [IO.Path]::GetFullPath($Output, $repository).TrimEnd([IO.Path]::DirectorySeparatorChar)
    function Assert-ArtifactPath([string]$Path) {
        $fullPath = [IO.Path]::GetFullPath($Path)
        if (-not $fullPath.StartsWith($artifacts + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Publish paths must be below $artifacts : $fullPath"
        }
        for ($ancestor = $fullPath; $ancestor.Length -ge $artifacts.Length; $ancestor = Split-Path -Parent $ancestor) {
            if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw "Publish paths must not use directory links: $ancestor"
            }
        }
    }
    # A package folder holds zStudio.exe with its licence and notices beside it, or (the 0.9.6 layout, which the
    # first single-file publish replaces) zStudio.exe with the app in dependencies/.
    function Test-StudioPackage([string]$Path) {
        if (-not (Test-Path -LiteralPath (Join-Path $Path 'zStudio.exe') -PathType Leaf)) { return $false }
        @('LICENSE', 'THIRD-PARTY-NOTICES.md').Where({ -not (Test-Path -LiteralPath (Join-Path $Path $_) -PathType Leaf) }).Count -eq 0 -or
            (Test-Path -LiteralPath (Join-Path $Path 'dependencies/zStudio.Desktop.runtimeconfig.json') -PathType Leaf)
    }
    Assert-ArtifactPath $outputPath
    $preservedPaths = @($PreserveSuperseded | ForEach-Object { $full = [IO.Path]::GetFullPath($_, $repository).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar); Assert-ArtifactPath $full; $full })
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
    if (Test-Path -LiteralPath $outputPath) {
        # Previous builds are not kept. Move the old output aside first so a file in use
        # cannot leave a half-deleted current build; it is deleted once replaced.
        $superseded = $outputPath + '.previous-' + [Guid]::NewGuid().ToString('N')
        Assert-ArtifactPath $superseded
        Move-Item -LiteralPath $outputPath -Destination $superseded
    }
    $parent = Split-Path -Parent $outputPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    Move-Item -LiteralPath $stage -Destination $outputPath
    [xml]$properties = Get-Content -LiteralPath 'Directory.Build.props'
    $version = $properties.Project.PropertyGroup.Version
    $archive = Join-Path $parent "zStudio-$version-win-x64.zip"
    Compress-Archive -Path (Join-Path $outputPath '*') -DestinationPath $archive -Force
    & (Join-Path $PSScriptRoot 'verify-package.ps1') -Directory $outputPath -Archive $archive -ExpectedVersion $version
    # Delete this and any earlier superseded Studio output, and older ZIPs. A build
    # still running from a superseded folder is reported and removed by the next publish.
    foreach ($stale in @(Get-ChildItem -LiteralPath $parent -Directory -Filter ((Split-Path -Leaf $outputPath) + '.previous-*'))) {
        Assert-ArtifactPath $stale.FullName
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
    Get-FileHash -LiteralPath $archive -Algorithm SHA256
} finally {
    foreach ($temporary in @($stage, $build)) {
        if ($temporary -and (Test-Path -LiteralPath $temporary)) { Assert-ArtifactPath $temporary; Remove-Item -LiteralPath $temporary -Recurse -Force }
    }
    Pop-Location
}
