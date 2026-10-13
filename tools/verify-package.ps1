param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'ExpectedVersion must be a release version.' }
$root = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
$expectedRoot = @('dependencies', 'zStudio.exe')
$actualRoot = @(Get-ChildItem -LiteralPath $root -Force | Select-Object -ExpandProperty Name | Sort-Object)
if (@(Compare-Object $expectedRoot $actualRoot).Count -ne 0) { throw 'Portable root must contain exactly zStudio.exe and dependencies/.' }
foreach ($required in @('dependencies/Recoil.Zbd.Studio.dll', 'dependencies/Recoil.Zbd.Studio.runtimeconfig.json', 'dependencies/hostfxr.dll', 'dependencies/README.md', 'dependencies/LICENSE', 'dependencies/THIRD-PARTY-NOTICES.md', 'dependencies/blender/zstudio_recoil-0.1.0.zip')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $required) -PathType Leaf)) { throw "Missing portable file: $required" }
}
$expectedFileVersion = ($ExpectedVersion -split '-', 2)[0] + '.0'
foreach ($binary in @('zStudio.exe', 'dependencies/Recoil.Zbd.Studio.dll')) {
    $version = (Get-Item -LiteralPath (Join-Path $root $binary)).VersionInfo
    if ($version.FileVersion -ne $expectedFileVersion -or ($version.ProductVersion -split '\+', 2)[0] -ne $ExpectedVersion) { throw "Version mismatch in $binary" }
}
$launcher = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $root 'zStudio.exe')))
if (-not $launcher.Contains('dependencies/Recoil.Zbd.Studio.dll')) { throw 'Launcher lost its relative apphost binding.' }
$extensionSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'blender/zstudio_recoil'
$extensionFiles = @{}
foreach ($file in Get-ChildItem -LiteralPath $extensionSource -File | Where-Object { $_.Extension -in '.py', '.toml', '.md' }) { $extensionFiles[$file.Name] = $file.FullName }
$extensionFiles['LICENSE'] = Join-Path (Split-Path -Parent $PSScriptRoot) 'LICENSE'
$extension = [IO.Compression.ZipFile]::OpenRead((Join-Path $root 'dependencies/blender/zstudio_recoil-0.1.0.zip'))
try {
    if ($extension.Entries.Count -ne $extensionFiles.Count) { throw 'Unexpected Blender extension inventory' }
    foreach ($entry in $extension.Entries) {
        if (-not $extensionFiles.ContainsKey($entry.FullName)) { throw "Unexpected Blender extension entry: $($entry.FullName)" }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($hash -ne (Get-FileHash -LiteralPath $extensionFiles[$entry.FullName] -Algorithm SHA256).Hash) { throw "Stale Blender extension entry: $($entry.FullName)" }
        $extensionFiles.Remove($entry.FullName)
    }
    if ($extensionFiles.Count) { throw 'Missing Blender extension entry' }
} finally { $extension.Dispose() }
$files = @(Get-ChildItem -LiteralPath $root -Recurse -File -Force)
if ($files.Where({ $_.Extension -in @('.zbd', '.zrd', '.bndb', '.pfx', '.p12', '.key') }).Count -gt 0) { throw 'Package contains game data, research databases or key files.' }
$diskNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $files) { [void]$diskNames.Add([IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')) }
$zipNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Archive).Path)
try {
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($name.StartsWith('/') -or $name.Contains(':') -or '..' -in ($name -split '/')) { throw "Unsafe archive entry: $name" }
        if ($name.EndsWith('/')) { continue }
        if (-not $zipNames.Add($name) -or -not $diskNames.Contains($name)) { throw "Duplicate or unexpected archive entry: $name" }
        $stream = $entry.Open()
        try { $zipHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($zipHash -ne (Get-FileHash -LiteralPath (Join-Path $root $name) -Algorithm SHA256).Hash) { throw "Archive content mismatch: $name" }
    }
} finally { $zip.Dispose() }
if (-not $diskNames.SetEquals($zipNames)) { throw 'Archive does not contain all portable files.' }
Write-Host "Verified $($files.Count) package files, version $ExpectedVersion, relative apphost binding and ZIP SHA-256 parity."
