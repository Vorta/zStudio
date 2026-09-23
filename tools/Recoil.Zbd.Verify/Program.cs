using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;

if (args.Length == 0) { Console.Error.WriteLine("Usage: Recoil.Zbd.Verify <dataset-root> [...]"); return 2; }
int errors = 0, fileCount = 0, textures = 0; Stopwatch timer = Stopwatch.StartNew(); List<object> results = [];
foreach (string root in args)
{
    foreach (string path in Directory.EnumerateFiles(root, "*.zbd", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
    {
        try
        {
            string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            var doc = await FormatRegistry.Default.OpenAsync(path); int decoded = 0, failed = 0; List<string> messages = [];
            foreach (var asset in doc.Assets)
            {
                try
                {
                    if (asset.Kind == AssetKind.Texture) { TextureDecoder.Decode(doc, asset); decoded++; }
                    if (asset.Kind == AssetKind.Zrd) ZrdDecoder.Decode(doc.Slice(asset.Offset, asset.Length));
                    if (asset.Kind == AssetKind.Sound) { var wave = WaveDecoder.Read(doc.Slice(asset.Offset, asset.Length)); WaveDecoder.Peaks(doc.Slice(asset.Offset, asset.Length), wave); }
                }
                catch (Exception ex) { failed++; messages.Add($"{asset.Id}: {ex.Message}"); }
            }
            string after = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
            if (before != after) { failed++; messages.Add("SOURCE CHANGED"); }
            errors += failed + doc.Diagnostics.Count(d => d.Severity == "Error"); fileCount++; textures += decoded;
            results.Add(new { path, family = doc.Probe.Family.ToString(), assets = doc.Assets.GroupBy(a => a.Kind).ToDictionary(g => g.Key.ToString(), g => g.Count()), diagnostics = doc.Diagnostics, messages, sha256 = before });
            Console.Error.WriteLine($"{doc.Probe.Family,-12} {Path.GetRelativePath(root, path),-24} {doc.Assets.Count,6} assets, {failed} failures, {doc.Diagnostics.Count} diagnostics");
        }
        catch (Exception ex) { errors++; Console.Error.WriteLine($"FAIL {path}: {ex}"); }
    }
}
Console.WriteLine(JsonSerializer.Serialize(new { files = fileCount, decodedTextures = textures, errors, seconds = timer.Elapsed.TotalSeconds, results }, JsonData.Options));
return errors == 0 ? 0 : 1;
