using System.Security.Cryptography;
using System.Text.Json;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;

if (args.Length < 2) { Console.Error.WriteLine("Usage: ExportCheck <output-directory> <dataset-root> [...]"); return 2; }
string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(output); List<object> samples = []; int errors = 0, complete = 0;
foreach (string root in args.Skip(1))
{
    using AssetResolver resolver = new(root); ExportService exporter = new(resolver); HashSet<FormatFamily> checkedFamilies = [];
    foreach (string path in Directory.EnumerateFiles(root, "*.zbd", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
    {
        var doc = await FormatRegistry.Default.OpenAsync(path); List<AssetRecord> selected = [];
        if (doc.Probe.Family == FormatFamily.TexturePack && doc.Assets.Count > 0) selected.AddRange(new[] { doc.Assets[0], doc.Assets[doc.Assets.Count / 2], doc.Assets[^1] }.Distinct());
        else if (checkedFamilies.Add(doc.Probe.Family) || doc.Assets.Any(a => a.Kind == AssetKind.Sound) && checkedFamilies.Add(FormatFamily.Wave))
        {
            foreach (var kind in doc.Assets.GroupBy(a => a.Kind)) selected.Add(kind.First());
        }
        if (selected.Count == 0) continue;
        string before = Convert.ToHexString(SHA256.HashData(doc.Bytes.Span));
        var result = await exporter.ExportAsync(doc, selected, output, false);
        errors += result.Errors.Count; complete += result.Completed;
        if (before != Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path)))) throw new InvalidDataException("Source changed: " + path);
        foreach (var a in selected.Where(a => a.Kind == AssetKind.Texture)) samples.Add(new { source = Path.GetFullPath(path), index = a.Index, png = Directory.EnumerateFiles(Path.Combine(result.Directory, "Texture"), $"{a.Index:D5}_*.png").Single() });
        Console.WriteLine($"{path}: {result.Completed} exports, {result.Errors.Count} errors");
    }
}
await File.WriteAllTextAsync(Path.Combine(output, "samples.json"), JsonSerializer.Serialize(samples, JsonData.Options));
Console.WriteLine($"{complete} exported assets; {errors} errors; {samples.Count} texture reference samples"); return errors == 0 ? 0 : 1;
