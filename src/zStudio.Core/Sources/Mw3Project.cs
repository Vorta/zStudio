using System.Text.Json.Nodes;
using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

internal static partial class Mw3Project
{
    internal static bool IsProject(string root)
    {
        SourceProject.RejectNestedLinks(root, Mw3SourceRecipe.Manifest);
        return SourceRead.FileExists(SourceProject.Resolve(root, Mw3SourceRecipe.Manifest));
    }
    internal static IReadOnlyList<SourceOutputPlan> Plan(string root, SourceBuilder.Snapshot snapshot, CancellationToken token)
    {
        var manifest = Mw3SourceRecipe.Parse(snapshot.Read(Mw3SourceRecipe.Manifest, token, SourceProject.MaximumSourceTextBytes));
        Mw3SourceRecipe.RequireFormat(manifest);
        if (manifest["game"]?.GetValue<string>() != "mw3") throw new InvalidDataException("The MW3 project build definition must name game mw3.");
        List<SourceOutputPlan> plans = []; HashSet<string> outputs = new(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var output in manifest["outputs"]!.AsArray().OfType<JsonObject>())
        {
            token.ThrowIfCancellationRequested(); string path = output["path"]!.GetValue<string>();
            _ = SourceProject.Resolve(root, path);
            if (!outputs.Add(path) || outputs.Count > 256) throw new InvalidDataException("Duplicate or excessive MW3 output paths.");
            string source = output["source"]!.GetValue<string>(); List<string> inputs = [source, Mw3SourceRecipe.Manifest];
            Visit(source, 0);
            plans.Add(new(path, "mw3-" + output["family"]!.GetValue<string>(), inputs));
            void Visit(string recipePath, int depth)
            {
                if (depth > 4) throw new InvalidDataException("MW3 recipes are nested too deeply.");
                SourceProject.RequireSource(recipePath); SourceProject.RejectNestedLinks(root, recipePath);
                var recipe = Mw3SourceRecipe.Parse(snapshot.Read(recipePath, token, SourceProject.MaximumSourceTextBytes)); Mw3SourceRecipe.RequireFormat(recipe);
                foreach (var part in recipe["parts"]?.AsArray() ?? []) Add(part!.GetValue<string>());
                foreach (var input in recipe["inputs"]?.AsArray() ?? []) Add(input!.GetValue<string>());
                foreach (var entry in recipe["entries"]?.AsArray().OfType<JsonObject>() ?? [])
                {
                    string member = entry["source"]!.GetValue<string>(); Add(member);
                    if (entry["kind"]?.GetValue<string>() == "recipe") Visit(member, depth + 1);
                }
            }
            void Add(string input)
            {
                token.ThrowIfCancellationRequested(); SourceProject.RequireSource(input); _ = SourceProject.Resolve(root, input); SourceProject.RejectNestedLinks(root, input);
                if (++total > SourceBuilder.MaximumPlanInputs) throw new InvalidDataException("MW3 plan has too many source references."); inputs.Add(input);
            }
        }
        return plans;
    }
    internal static SourceBuilder.Built Build(SourceOutputPlan plan, SourceBuilder.Snapshot snapshot, CancellationToken token)
    {
        byte[] Read(string path)
        {
            SourceProject.RequireSource(path); // Snapshot.Files also refuses links and preserves frozen input identities.
            return snapshot.Files().Read(path, token, Worlds.ProjectReadLimits.Bytes(FormatRegistry.MaximumDocumentBytes));
        }
        BoundedDiagnostics warnings = new();
        byte[] bytes = BuildRecipe(plan.Inputs[0], Read, token, warning: message => warnings.Add($"{new BoundedDiagnostics.PreparedMessage(message)}"));
        return new(bytes, 1, warnings.Snapshot());
    }
}

public static partial class SourceExtractor
{
    private static async Task<SourceReconstructionReport> ExtractMw3Async(string root, List<Input> all, Writes writes, long budget,
        IProgress<SourceProgress>? progress, CancellationToken token)
    {
        var files = all.Where(f => f.Relative.EndsWith(".zbd", StringComparison.OrdinalIgnoreCase) && f.Relative.Count(c => c == '/') <= 1).ToList();
        RequireCheckedInputCapacity(files, token); Dictionary<string, string> digests = new(StringComparer.Ordinal);
        foreach (var file in files) digests.Add(file.Path, (await ProbeInputAsync(file, token)).Digest);
        JsonArray outputs = []; Dictionary<string, int> families = []; int written = 0;
        try
        {
            writes.CreateDirectory(root);
            for (int i = 0; i < files.Count; i++)
            {
                token.ThrowIfCancellationRequested(); var file = files[i]; progress?.Report(new(i, files.Count, file.Relative));
                byte[] bytes = await ReadInputAsync(file, digests[file.Path], token);
                var probe = FormatRegistry.Probe(bytes.AsSpan(0, Math.Min(36, bytes.Length)), bytes.AsSpan(Math.Max(0, bytes.Length - 8)), bytes.Length);
                RequireReconstructionCapacity(file.Relative, 0, probe.Family == FormatFamily.GameZ ? WorldDecodeReservation(bytes.Length) : 16L * bytes.Length + 65536, budget);
                var doc = await Task.Run(() => FormatRegistry.Default.OpenBytes(file.Path, bytes, token: token), token);
                if (doc.Diagnostics.FirstOrDefault(d => d.Severity == "Error") is { } error) throw new InvalidDataException($"{file.Relative}: {error.Message}");
                if (doc.Probe is { Family: FormatFamily.GameZ, Version: not 27 } or { Family: FormatFamily.Animation, Version: not 39 })
                    throw new InvalidDataException("A source reconstruction cannot mix MW3 and RECOIL files.");
                string folder = "data/" + file.Relative[..^4] + ".sources", recipePath = "gamegen/" + file.Relative + ".json";
                void Write(string path, byte[] content)
                {
                    SourceProject.CheckSavablePath(root, path); string absolute = SourceProject.Resolve(root, path);
                    writes.CreateDirectory(Path.GetDirectoryName(absolute)!);
                    writes.CreateAsync(absolute, content, null, token).GetAwaiter().GetResult(); written++;
                    if (written > SourceProject.MaximumFiles) throw new InvalidDataException("MW3 reconstruction exceeds the source file limit.");
                }
                var recipe = await Task.Run(() => Mw3Project.Extract(doc, folder, Write, token), token);
                Write(recipePath, Mw3SourceRecipe.Text(recipe));
                string family = doc.Probe.Family.ToString().ToLowerInvariant(); families[family] = families.GetValueOrDefault(family) + 1;
                outputs.Add(new JsonObject { ["path"] = file.Relative, ["source"] = recipePath, ["family"] = family });
            }
            var manifest = new JsonObject { ["format"] = 1, ["game"] = "mw3", ["target"] = "mw3-1.2", ["outputs"] = outputs };
            string destination = SourceProject.Resolve(root, Mw3SourceRecipe.Manifest); writes.CreateDirectory(Path.GetDirectoryName(destination)!);
            await writes.CreateAsync(destination, Mw3SourceRecipe.Text(manifest), null, token); written++;
            foreach (var file in files) _ = await ReadInputAsync(file, digests[file.Path], token);
            return new(root, written, families, all.Where(f => !files.Contains(f)).Select(f => f.Relative).ToArray(), []) { Game = GameVariant.MechWarrior3 };
        }
        catch
        {
            var (changed, failed) = writes.Remove();
            if (changed.Count > 0 || failed.Count > 0) throw new IOException("MW3 reconstruction stopped; files changed externally or held open remain in the destination.");
            throw;
        }
    }
}
