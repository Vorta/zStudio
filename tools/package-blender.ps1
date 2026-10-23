param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repository 'blender/zstudio_recoil'
[xml]$properties = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props')
$version = [string]$properties.Project.PropertyGroup.Version
$manifest = Get-Content -LiteralPath (Join-Path $source 'blender_manifest.toml') -Raw
if ($manifest -notmatch '(?m)^version\s*=\s*"([^"]+)"\s*$' -or $Matches[1] -ne $version) {
    throw 'Blender extension and zStudio versions must match.'
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$zipPath = Join-Path $Destination "zstudio_recoil-$version.zip"
$temporary = $zipPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in '.py', '.toml', '.md' }) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $file.Name, [IO.Compression.CompressionLevel]::Optimal)
    }
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path (Split-Path -Parent $PSScriptRoot) 'LICENSE'), 'LICENSE', [IO.Compression.CompressionLevel]::Optimal)
} finally { $zip.Dispose() }
[IO.File]::Move($temporary, $zipPath, $true)
