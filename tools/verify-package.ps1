param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Archive,
    [Parameter(Mandatory)][string]$ExpectedVersion
)
$ErrorActionPreference = 'Stop'
if ($ExpectedVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') { throw 'ExpectedVersion must be a release version.' }
$root = (Resolve-Path -LiteralPath $Directory).Path.TrimEnd([IO.Path]::DirectorySeparatorChar)
$extensionName = "zStudio-Blender-extension-$ExpectedVersion.zip"
# Only the executable and the files the owner keeps beside it: no runtime, guides, symbols or folders.
$expectedRoot = @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'zStudio.exe', $extensionName) | Sort-Object
$actualRoot = @(Get-ChildItem -LiteralPath $root -Force | Select-Object -ExpandProperty Name | Sort-Object)
if (@(Compare-Object $expectedRoot $actualRoot -CaseSensitive).Count -ne 0) { throw "Package root must contain exactly $($expectedRoot -join ', ') (found $($actualRoot -join ', '))." }
$executable = Join-Path $root 'zStudio.exe'

# The .NET single-file bundle manifest (format 6): header offset beside the host's bundle signature.
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
public sealed class StudioBundleEntry { public long Offset, Size, CompressedSize; public int Type; public string Path; }
public sealed class StudioBundle
{
    public uint Major, Minor; public string Id; public ulong Flags; public List<StudioBundleEntry> Entries = new List<StudioBundleEntry>();
    static readonly byte[] Signature = { 0x8b, 0x12, 0x02, 0xb9, 0x6a, 0x61, 0x20, 0x38, 0x72, 0x7b, 0x93, 0x02, 0x14, 0xd7, 0xa0, 0x32,
                                         0x13, 0xf5, 0xb9, 0xe6, 0xef, 0xae, 0x33, 0x18, 0xee, 0x3b, 0x2d, 0xce, 0x24, 0xb3, 0x6a, 0xae };
    public static StudioBundle Read(string path)
    {
        byte[] all = File.ReadAllBytes(path);
        int at = all.AsSpan().IndexOf(Signature);
        if (at < 8) throw new InvalidDataException("zStudio.exe is not a .NET single-file bundle (no bundle signature).");
        long header = BitConverter.ToInt64(all, at - 8);
        if (header <= 0 || header >= all.Length) throw new InvalidDataException("zStudio.exe is an apphost without a bundle.");
        using var reader = new BinaryReader(new MemoryStream(all), Encoding.UTF8);
        reader.BaseStream.Position = header;
        var bundle = new StudioBundle { Major = reader.ReadUInt32(), Minor = reader.ReadUInt32() };
        if (bundle.Major < 6) throw new InvalidDataException("Unsupported bundle format " + bundle.Major + "." + bundle.Minor + ".");
        int count = reader.ReadInt32();
        if (count <= 0 || count > 100000) throw new InvalidDataException("Implausible bundle entry count " + count + ".");
        bundle.Id = reader.ReadString();
        reader.ReadInt64(); reader.ReadInt64(); reader.ReadInt64(); reader.ReadInt64(); // deps.json and runtimeconfig.json locations
        bundle.Flags = reader.ReadUInt64();
        for (int i = 0; i < count; i++)
        {
            var entry = new StudioBundleEntry { Offset = reader.ReadInt64(), Size = reader.ReadInt64(), CompressedSize = reader.ReadInt64(), Type = reader.ReadByte(), Path = reader.ReadString() };
            long stored = entry.CompressedSize == 0 ? entry.Size : entry.CompressedSize;
            if (entry.Offset <= 0 || stored < 0 || entry.Offset + stored > header) throw new InvalidDataException("Bundle entry outside the bundle: " + entry.Path);
            bundle.Entries.Add(entry);
        }
        return bundle;
    }
    public static byte[] Extract(string path, StudioBundleEntry entry)
    {
        using var file = File.OpenRead(path);
        file.Position = entry.Offset;
        if (entry.CompressedSize == 0) { var plain = new byte[entry.Size]; file.ReadExactly(plain); return plain; }
        var packed = new byte[entry.CompressedSize]; file.ReadExactly(packed);
        using var inflate = new DeflateStream(new MemoryStream(packed), CompressionMode.Decompress);
        var output = new MemoryStream(); inflate.CopyTo(output);
        if (output.Length != entry.Size) throw new InvalidDataException("Bundle entry size mismatch: " + entry.Path);
        return output.ToArray();
    }
}
'@
$bundle = [StudioBundle]::Read($executable)
# Header flag 1 (.NET Core 3 compatibility mode) extracts every file; only native libraries may extract.
if ($bundle.Flags -band 1) { throw 'The bundle extracts all of its files (IncludeAllContentForSelfExtract).' }
$entries = @{}
foreach ($entry in $bundle.Entries) {
    if ($entries.ContainsKey($entry.Path)) { throw "Duplicate bundle entry: $($entry.Path)" }
    $entries[$entry.Path] = $entry
}
# Entry types: 1 assembly, 2 native library, 3 deps.json, 4 runtimeconfig.json.
foreach ($required in @(@('zStudio.Desktop.dll', 1), @('zStudio.Desktop.deps.json', 3), @('zStudio.Desktop.runtimeconfig.json', 4))) {
    if (-not $entries.ContainsKey($required[0]) -or $entries[$required[0]].Type -ne $required[1]) { throw "The bundle lacks $($required[0])." }
}
if ($entries['zStudio.Desktop.dll'].CompressedSize -le 0) { throw 'The bundle is not compressed.' }
$natives = @($bundle.Entries | Where-Object Type -eq 2 | ForEach-Object Path | Sort-Object)
$expectedNatives = @('D3DCompiler_47_cor3.dll', 'PenImc_cor3.dll', 'PresentationNative_cor3.dll', 'vcruntime140_cor3.dll', 'wpfgfx_cor3.dll')
if (@(Compare-Object $expectedNatives $natives).Count -ne 0) { throw "Unexpected native libraries in the bundle: $($natives -join ', ')" }
# Per-assembly ReadyToRun: a composite image (the SDK default) is larger and starts slower.
if ($bundle.Entries.Where({ $_.Path -like '*.r2r.dll' }).Count -gt 0) { throw 'The bundle holds a composite ReadyToRun image.' }
if ($bundle.Entries.Where({ [IO.Path]::GetExtension($_.Path) -in @('.zbd', '.zrd', '.bndb', '.pfx', '.p12', '.key', '.pdb') }).Count -gt 0) {
    throw 'The bundle contains game data, research databases, key files or loose symbols.'
}

# Every shipped package has its notice row (same version) and every notice row a shipped package; every licence link is a section of the notices.
$notices = Get-Content -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md') -Raw
$anchors = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($heading in [regex]::Matches($notices, '(?m)^#{1,6} (.+?)\s*$')) {
    # GitHub's heading anchors: lower case, punctuation other than hyphens and underscores dropped, spaces to hyphens.
    $anchor = ($heading.Groups[1].Value.ToLowerInvariant() -replace '[^\p{L}\p{N}_\- ]', '') -replace ' ', '-'
    [void]$anchors.Add($anchor)
}
$noticeRows = @{}
foreach ($row in [regex]::Matches($notices, '(?m)^\| ([^|]+?) \| ([^|]+?) \| \[[^\]]+\]\(([^)]*)\) \|\s*$')) {
    $link = $row.Groups[3].Value
    if (-not $link.StartsWith('#') -or -not $anchors.Contains($link.Substring(1))) { throw "Notice row links no licence section of the notices: $($row.Groups[1].Value) $link" }
    $noticeRows[$row.Groups[1].Value] = $row.Groups[2].Value
}
if ($noticeRows.Count -eq 0) { throw 'THIRD-PARTY-NOTICES.md lists no packages.' }
$deps = [Text.Encoding]::UTF8.GetString([StudioBundle]::Extract($executable, $entries['zStudio.Desktop.deps.json']))
$packages = @(($deps | ConvertFrom-Json).libraries.PSObject.Properties | Where-Object { $_.Value.type -eq 'package' } | ForEach-Object { $_.Name })
foreach ($package in $packages) {
    $name, $packageVersion = $package -split '/', 2
    if ($noticeRows[$name] -ne $packageVersion) { throw "Shipped package without its notice row: $package" }
}
foreach ($name in $noticeRows.Keys) {
    if (-not ($packages -contains "$name/$($noticeRows[$name])")) { throw "Notice row for a package that is not shipped: $name $($noticeRows[$name])" }
}

$expectedFileVersion = ($ExpectedVersion -split '-', 2)[0] + '.0'
$version = (Get-Item -LiteralPath $executable).VersionInfo
if ($version.FileVersion -ne $expectedFileVersion -or ($version.ProductVersion -split '\+', 2)[0] -ne $ExpectedVersion) { throw "Version mismatch in zStudio.exe: $($version.FileVersion) $($version.ProductVersion)" }

$repository = Split-Path -Parent $PSScriptRoot
if ((Get-FileHash -LiteralPath (Join-Path $root 'LICENSE')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $repository 'LICENSE')).Hash) { throw 'Stale LICENSE.' }
if ((Get-FileHash -LiteralPath (Join-Path $root 'THIRD-PARTY-NOTICES.md')).Hash -ne (Get-FileHash -LiteralPath (Join-Path $repository 'THIRD-PARTY-NOTICES.md')).Hash) { throw 'Stale THIRD-PARTY-NOTICES.md.' }
$extensionSource = Join-Path $repository 'blender/zstudio_recoil'
$manifest = Get-Content -LiteralPath (Join-Path $extensionSource 'blender_manifest.toml') -Raw
if ($manifest -notmatch '(?m)^version\s*=\s*"([^"]+)"\s*$' -or $Matches[1] -ne $ExpectedVersion) { throw 'Blender extension version does not match zStudio.' }
$extensionFiles = @{}
foreach ($file in Get-ChildItem -LiteralPath $extensionSource -File | Where-Object { $_.Extension -in '.py', '.toml', '.md' }) { $extensionFiles[$file.Name] = $file.FullName }
$extensionFiles['LICENSE'] = Join-Path $repository 'LICENSE'
$extension = [IO.Compression.ZipFile]::OpenRead((Join-Path $root $extensionName))
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

$diskNames = [Collections.Generic.HashSet[string]]::new([string[]]$actualRoot, [StringComparer]::Ordinal)
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
if (-not $diskNames.SetEquals($zipNames)) { throw 'Archive does not contain all package files.' }
Write-Host ("Verified the single-file package {0}: zStudio.exe {1:N0} bytes, bundle {2} with {3} entries, {4} notice rows, Blender extension and ZIP SHA-256 parity." -f $ExpectedVersion, (Get-Item -LiteralPath $executable).Length, $bundle.Id, $bundle.Entries.Count, $noticeRows.Count)
