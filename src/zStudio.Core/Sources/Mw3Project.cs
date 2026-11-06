using System.Text.Json;
using zStudio.Core.Formats;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

internal static partial class Mw3Project
{
    internal const string Obsolete = "This MW3 project uses obsolete record recipes. Reconstruct the project from the original ZBD files to recover its native data and gamegen sources.";
    internal static bool IsProject(string root, SourceBuilder.Snapshot? snapshot = null, CancellationToken token = default)
    {
        const string obsoleteManifest = "gamegen/mw3.project.json";
        SourceProject.RejectNestedLinks(root, obsoleteManifest);
        if (snapshot?.Exists(obsoleteManifest) ?? SourceRead.FileExists(SourceProject.Resolve(root, obsoleteManifest))) throw new InvalidDataException(Obsolete);
        foreach (string path in SourceProject.Files(root, "data", p => Model(p), snapshot?.Added, token, snapshot?.Inventory))
        {
            token.ThrowIfCancellationRequested();
            SourceProject.RejectNestedLinks(root, path);
            byte[] bytes = snapshot == null ? SourceRead.All(SourceProject.Resolve(root, path), ProjectReadLimits.Model(), token) : snapshot.ReadGameIdentity(path, token);
            try
            {
                if (WorldGltf.ReadSourceIdentity(bytes, token).Game == "mw3") return true;
            }
            catch (JsonException) { /* An unrelated unfinished model does not hide independently buildable outputs. */ }
        }
        return false;
    }

    internal static IReadOnlyList<SourceOutputPlan> Plan(string root, SourceBuilder.Snapshot snapshot, CancellationToken token,
        IReadOnlyCollection<string>? added = null)
    {
        var files = SourceProject.Files(root, "data", _ => true, added, token, snapshot.Inventory);
        var scripts = SourceProject.Files(root, "gamegen", n => n.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".gw", StringComparison.OrdinalIgnoreCase), added, token, snapshot.Inventory);
        List<SourceOutputPlan> plans = [];
        var models = files.Where(p => Model(p) || p.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)).ToArray();
        var motions = files.Where(p => p.StartsWith(Mw3LibrarySources.MotionFolder + "/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".zan", StringComparison.OrdinalIgnoreCase)).ToArray();
        var animationScripts = files.Where(p => p.EndsWith(".zan", StringComparison.OrdinalIgnoreCase) && !p.StartsWith(Mw3LibrarySources.MotionFolder + "/", StringComparison.OrdinalIgnoreCase)).ToArray();
        var resources = files.Where(p => p.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase) && p.Contains("/zrdr/", StringComparison.OrdinalIgnoreCase));
        foreach (var group in resources.GroupBy(ResourceOutput, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal))
            plans.Add(new(group.Key, "archive", group.Order(StringComparer.Ordinal).ToArray()));
        if (scripts.Count > 0) plans.Add(new("interp.zbd", "scripts", scripts));
        var sounds = files.Where(p => p.StartsWith(SourceBuilder.SoundsFolder + "/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sounds.Length > 0) plans.Add(new("soundsH.zbd", "sounds", sounds));
        var mechModels = models.Where(p => p.StartsWith("data/common/mechs/models/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("data/lab/models/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (mechModels.Length > 0) plans.Add(new("mechlib.zbd", "mw3-mechs", mechModels));
        if (motions.Length > 0) plans.Add(new("motion.zbd", "mw3-motion", motions));
        var pngs = files.Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToArray();
        var images = pngs.Where(p => p.Contains("/images/", StringComparison.OrdinalIgnoreCase) || p.Contains("/fonts/", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (images.Length > 0) plans.Add(new("rimage.zbd", "mw3-textures", images) { Pack = NativePack("rimage.zbd", true) });
        var mechTextures = pngs.Where(p => p.StartsWith("data/common/mechs/textures/", StringComparison.OrdinalIgnoreCase) || p.StartsWith("data/lab/textures/", StringComparison.OrdinalIgnoreCase)).ToList();
        if (mechModels.Length > 0)
            try { mechTextures.AddRange(snapshot.NativeLibrary(mechModels, token).TextureFiles.Values); }
            catch (Exception ex) when (SourceBuilder.IsBuildFailure(ex) && ex is not InventoryCapacityException) { }
        if (mechTextures.Count > 0) plans.Add(new("rmechtex.zbd", "mw3-textures", mechTextures.Concat(mechModels).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()) { Pack = NativePack("rmechtex.zbd") });
        foreach (string mission in files.Where(p => p.EndsWith("/zrdr/anim.zad", StringComparison.OrdinalIgnoreCase)).Select(p => p.Split('/')[1]).Where(m => m != "common").Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            string entry = SourceBuilder.WorldScript(mission);
            if (scripts.Contains(entry, StringComparer.OrdinalIgnoreCase)) plans.Add(new($"{mission}/gamez.zbd", "world", new[] { entry }.Concat(models).ToArray()));
            plans.Add(new($"{mission}/anim.zbd", "animations", new[] { SourceBuilder.AnimationRoot(mission) }.Concat(animationScripts).ToArray()));
            string[] folders = ["data/common/mechs/textures", $"data/{mission}/textures", TextureSources.EffectsTextures, TextureSources.CommonTextures];
            HashSet<string>? worldTextures = new(StringComparer.OrdinalIgnoreCase);
            if (scripts.Contains(entry, StringComparer.OrdinalIgnoreCase))
                try { worldTextures = snapshot.World(mission, token).World.Textures.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase); }
                catch (Exception ex) when (SourceBuilder.IsBuildFailure(ex) && ex is not InventoryCapacityException) { worldTextures = null; }
            HashSet<string> names = new(StringComparer.OrdinalIgnoreCase); List<string> textures = [];
            foreach (string folder in folders)
                foreach (string png in pngs.Where(p => Path.GetDirectoryName(p)!.Replace('\\', '/').Equals(folder, StringComparison.OrdinalIgnoreCase)))
                    if ((!folder.Equals("data/common/mechs/textures", StringComparison.OrdinalIgnoreCase) || worldTextures == null || worldTextures.Contains(Path.GetFileNameWithoutExtension(png))) && names.Add(Path.GetFileNameWithoutExtension(png))) textures.Add(png);
            if (worldTextures != null && scripts.Contains(entry, StringComparer.OrdinalIgnoreCase))
                foreach (string path in snapshot.World(mission, token).TextureFiles.Values)
                    if (!textures.Contains(path, StringComparer.OrdinalIgnoreCase)) textures.Add(path);
            if (textures.Count > 0) plans.Add(new($"{mission}/rtexture.zbd", "mw3-textures", textures) { Pack = NativePack("rtexture.zbd") });
        }
        if (plans.Sum(p => (long)p.Inputs.Count) > SourceBuilder.MaximumPlanInputs) throw new InvalidDataException("The MW3 plan exceeds the source input limit.");
        return plans;
    }
    private static TexturePackVariant NativePack(string name, bool images = false) => new(name, images ? TexturePackKind.Interface : TexturePackKind.Hardware, null, TextureSources.MaximumSide);
    internal static bool Model(string path) => path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase);
    private static string ResourceOutput(string path)
    {
        string[] parts = path.Split('/');
        if (parts[1].Equals("common", StringComparison.OrdinalIgnoreCase)) return "reader.zbd";
        int ai = Array.FindIndex(parts, p => p.Equals("aipath", StringComparison.OrdinalIgnoreCase));
        return ai >= 0 && ai + 2 < parts.Length ? $"{parts[1]}/reader{parts[ai + 1]}.zbd" : $"{parts[1]}/reader.zbd";
    }
    internal static SourceBuilder.Built Build(SourceOutputPlan plan, SourceBuilder.Snapshot snapshot, CancellationToken token)
    {
        if (plan.Family == "mw3-mechs") { var library = snapshot.NativeLibrary(plan.Inputs, token); return new(library.Bytes, library.Members, library.Warnings); }
        if (plan.Family == "mw3-motion")
        {
            List<ArchiveSources.Entry> entries = [];
            Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in plan.Inputs.Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                string name = Path.GetFileNameWithoutExtension(path);
                if (names.TryGetValue(name, out var previous)) throw new InvalidDataException($"{path} and {previous} have the same motion member name {name}; use distinct names.");
                names.Add(name, path);
                entries.Add(new(name, SourceBuilder.SourceField(path), Mw3LibrarySources.BuildMotion(snapshot.Read(path, token, SourceProject.MaximumSourceTextBytes), token)));
            }
            return new(ArchiveSources.Write(entries), entries.Count, []);
        }
        if (plan.Family == "mw3-textures")
        {
            string mission = plan.Path.Split('/')[0];
            var images = plan.Inputs.Where(p => p.EndsWith(".png", StringComparison.OrdinalIgnoreCase)).ToArray();
            IReadOnlyList<(string Input, string Name)> resolved = images.Select(p => (p, SourceBuilder.TextureName(p))).ToArray();
            IReadOnlyDictionary<string, int> addressing = new Dictionary<string, int>();
            if (plan.Path.Contains('/') && snapshot.HasWorld(mission))
            { resolved = SourceBuilder.TextureInputs(images, mission, snapshot, token); addressing = snapshot.World(mission, token).TextureAddressing; }
            else if (plan.Path.Equals("rmechtex.zbd", StringComparison.OrdinalIgnoreCase) && snapshot.NativeLibraryResult is { } library)
            {
                var selected = library.TextureFiles;
                resolved = resolved.Where(p => !selected.ContainsKey(p.Name)).Concat(selected.Select(p => (p.Value, p.Key))).ToArray();
                addressing = library.TextureAddressing;
            }
            List<PackSource> sources = []; Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, name) in resolved)
            {
                token.ThrowIfCancellationRequested();
                if (names.TryGetValue(name, out var previous)) throw new InvalidDataException($"{previous} and {path}: two texture sources have the same library name {name}.");
                names.Add(name, path);
                var size = snapshot.PngSize(path, token) ?? throw new InvalidDataException($"{path}: invalid PNG header.");
                sources.Add(new(name, TextureSources.SortKey(path), size.Width, size.Height, t => SourceBuilder.DecodeTexture(path, snapshot.Read(path, t), t), addressing.GetValueOrDefault(name)));
            }
            var built = TexturePackBuilder.BuildFromSources(sources, new(plan.Path, TexturePackKind.Interface, null, TextureSources.MaximumSide) { Mw3Pixels = true }, token);
            return new(built.Bytes, sources.Count, built.Warnings);
        }
        throw new InvalidDataException($"Unknown native MW3 output family {plan.Family}.");
    }
}
