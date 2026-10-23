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
$extensionRelative = "dependencies/blender/zstudio_recoil-$ExpectedVersion.zip"
foreach ($required in @('dependencies/zStudio.Desktop.dll', 'dependencies/zStudio.Desktop.runtimeconfig.json', 'dependencies/hostfxr.dll', 'dependencies/README.md', 'dependencies/LICENSE', 'dependencies/THIRD-PARTY-NOTICES.md', $extensionRelative)) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $required) -PathType Leaf)) { throw "Missing portable file: $required" }
}
# Every shipped package has its notice row (same version) and every notice row a shipped package; every licence link resolves.
if (-not (Test-Path -LiteralPath (Join-Path $root 'dependencies/licenses') -PathType Container)) { throw 'Missing portable folder: dependencies/licenses' }
$notices = Get-Content -LiteralPath (Join-Path $root 'dependencies/THIRD-PARTY-NOTICES.md') -Raw
foreach ($link in [regex]::Matches($notices, '\]\((licenses/[^)\s]+)\)')) {
    if (-not (Test-Path -LiteralPath (Join-Path $root ('dependencies/' + $link.Groups[1].Value)) -PathType Leaf)) { throw "Notice links a missing licence: $($link.Groups[1].Value)" }
}
$noticeRows = @{}
foreach ($row in [regex]::Matches($notices, '(?m)^\| ([^|]+?) \| ([^|]+?) \| \[')) { $noticeRows[$row.Groups[1].Value] = $row.Groups[2].Value }
$packages = @((Get-Content -LiteralPath (Join-Path $root 'dependencies/zStudio.Desktop.deps.json') -Raw | ConvertFrom-Json).libraries.PSObject.Properties |
    Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Name })
foreach ($package in $packages) {
    $name, $packageVersion = $package -split '/', 2
    if ($noticeRows[$name] -ne $packageVersion) { throw "Shipped package without its notice row: $package" }
}
foreach ($name in $noticeRows.Keys) {
    if (-not ($packages -contains "$name/$($noticeRows[$name])")) { throw "Notice row for a package that is not shipped: $name $($noticeRows[$name])" }
}
$repository = Split-Path -Parent $PSScriptRoot
$guides = @('dependencies/CHANGELOG.md', 'dependencies/CONTRIBUTING.md', 'dependencies/SECURITY.md', 'dependencies/blender/zstudio_recoil/README.md')
$guides += @(Get-ChildItem -LiteralPath (Join-Path $repository 'docs') -File -Filter '*.md') + @(Get-ChildItem -LiteralPath (Join-Path $repository 'docs/images') -File) |
    ForEach-Object { 'dependencies/' + [IO.Path]::GetRelativePath($repository, $_.FullName).Replace('\', '/') }
foreach ($guide in $guides) {
    if (-not (Test-Path -LiteralPath (Join-Path $root $guide) -PathType Leaf)) { throw "Missing user guide: $guide" }
}
foreach ($excluded in @('dependencies/docs/agents', 'dependencies/docs/mcp-capabilities.json')) {
    if (Test-Path -LiteralPath (Join-Path $root $excluded)) { throw "Agent-only documentation is packaged: $excluded" }
}
$expectedFileVersion = ($ExpectedVersion -split '-', 2)[0] + '.0'
foreach ($binary in @('zStudio.exe', 'dependencies/zStudio.Desktop.dll')) {
    $version = (Get-Item -LiteralPath (Join-Path $root $binary)).VersionInfo
    if ($version.FileVersion -ne $expectedFileVersion -or ($version.ProductVersion -split '\+', 2)[0] -ne $ExpectedVersion) { throw "Version mismatch in $binary" }
}
$launcher = [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes((Join-Path $root 'zStudio.exe')))
if (-not $launcher.Contains('dependencies/zStudio.Desktop.dll')) { throw 'Launcher lost its relative apphost binding.' }
$extensionSource = Join-Path (Split-Path -Parent $PSScriptRoot) 'blender/zstudio_recoil'
$manifest = Get-Content -LiteralPath (Join-Path $extensionSource 'blender_manifest.toml') -Raw
if ($manifest -notmatch '(?m)^version\s*=\s*"([^"]+)"\s*$' -or $Matches[1] -ne $ExpectedVersion) { throw 'Blender extension version does not match zStudio.' }
$extensionFiles = @{}
foreach ($file in Get-ChildItem -LiteralPath $extensionSource -File | Where-Object { $_.Extension -in '.py', '.toml', '.md' }) { $extensionFiles[$file.Name] = $file.FullName }
$extensionFiles['LICENSE'] = Join-Path (Split-Path -Parent $PSScriptRoot) 'LICENSE'
$extension = [IO.Compression.ZipFile]::OpenRead((Join-Path $root $extensionRelative))
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
