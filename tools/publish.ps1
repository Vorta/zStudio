param([string]$Output = 'artifacts/zStudio-win-x64')
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
    Assert-ArtifactPath $outputPath
    if (Test-Path -LiteralPath $outputPath) {
        $existingApp = (Test-Path -LiteralPath (Join-Path $outputPath 'Recoil.Zbd.Studio.runtimeconfig.json')) -or
                       (Test-Path -LiteralPath (Join-Path $outputPath 'dependencies/Recoil.Zbd.Studio.runtimeconfig.json'))
        if (-not $existingApp) { throw "Refusing to replace a folder that is not a Studio publish: $outputPath" }
    }
    $stage = Join-Path $artifacts ('.studio-publish-' + [Guid]::NewGuid().ToString('N'))
    Assert-ArtifactPath $stage
    $dependencies = Join-Path $stage 'dependencies'
    New-Item -ItemType Directory -Path $dependencies -Force | Out-Null
    dotnet publish src/zStudio.Desktop/zStudio.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PortableLayout=true -p:RestoreLockedMode=true -o $dependencies
    if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
    Copy-Item -LiteralPath README.md, LICENSE, THIRD-PARTY-NOTICES.md, CHANGELOG.md, CONTRIBUTING.md, SECURITY.md -Destination $dependencies
    Copy-Item -LiteralPath licenses -Destination $dependencies -Recurse
    Copy-Item -LiteralPath docs -Destination $dependencies -Recurse
    $topLevel = @(Get-ChildItem -LiteralPath $stage)
    if ($topLevel.Count -ne 2 -or -not (Test-Path -LiteralPath (Join-Path $stage 'zStudio.exe'))) {
        throw 'Portable output must contain only zStudio.exe and dependencies.'
    }
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
    # Delete this and any earlier superseded Studio output, and older portable ZIPs. A build
    # still running from a superseded folder is reported and removed by the next publish.
    foreach ($stale in @(Get-ChildItem -LiteralPath $parent -Directory -Filter ((Split-Path -Leaf $outputPath) + '.previous-*'))) {
        Assert-ArtifactPath $stale.FullName
        $studio = (Test-Path -LiteralPath (Join-Path $stale.FullName 'Recoil.Zbd.Studio.runtimeconfig.json')) -or
                  (Test-Path -LiteralPath (Join-Path $stale.FullName 'dependencies/Recoil.Zbd.Studio.runtimeconfig.json'))
        if (-not $studio) { Write-Warning "Not deleting a folder that is not a Studio publish: $($stale.FullName)"; continue }
        try { Remove-Item -LiteralPath $stale.FullName -Recurse -Force -ErrorAction Stop }
        catch { Write-Warning "Superseded build is still in use and will be deleted by the next publish: $($stale.FullName)" }
    }
    foreach ($zip in @(Get-ChildItem -LiteralPath $parent -File -Filter 'zStudio-*-win-x64.zip' | Where-Object { $_.FullName -ne [IO.Path]::GetFullPath($archive) })) {
        Assert-ArtifactPath $zip.FullName; Remove-Item -LiteralPath $zip.FullName -Force
    }
    Get-FileHash -LiteralPath $archive -Algorithm SHA256
} finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) { Assert-ArtifactPath $stage; Remove-Item -LiteralPath $stage -Recurse -Force }
    Pop-Location
}
