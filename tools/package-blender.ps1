param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$source = Join-Path (Split-Path -Parent $PSScriptRoot) 'blender/zstudio_recoil'
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$zipPath = Join-Path $Destination 'zstudio_recoil-0.1.0.zip'
$temporary = $zipPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
$zip = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem -LiteralPath $source -File | Where-Object { $_.Extension -in '.py', '.toml', '.md' }) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $file.Name, [IO.Compression.CompressionLevel]::Optimal)
    }
    [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, (Join-Path (Split-Path -Parent $PSScriptRoot) 'LICENSE'), 'LICENSE', [IO.Compression.CompressionLevel]::Optimal)
} finally { $zip.Dispose() }
[IO.File]::Move($temporary, $zipPath, $true)
# Match source-project.md's relative guide link in the portable documentation.
$guide = Join-Path $Destination 'zstudio_recoil'
New-Item -ItemType Directory -Path $guide -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $source 'README.md') -Destination (Join-Path $guide 'README.md')
