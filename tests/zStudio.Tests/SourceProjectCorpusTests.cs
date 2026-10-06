using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceProjectCorpusTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ReconstructedSourcesExportGameEquivalentFiles()
    {
        string? corpus = Environment.GetEnvironmentVariable("ZSTUDIO_CORPUS"); if (string.IsNullOrEmpty(corpus)) return;
        string work = Path.Combine(Path.GetTempPath(), "zstudio-source-corpus-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(work, "project"), exported = Path.Combine(work, "zbd"), again = Path.Combine(work, "again");
        try
        {
            var report = await SourceExtractor.ExtractAsync(corpus, project, token: Token);
            // Animation definitions shipped in another version than the animations were compiled from are rebuilt from
            // them; nothing else is noted. Definitions that name a sequence twice compile as shipped (the last name).
            const string Rebuilt = " is not the version the shipped animations were compiled from; it was rebuilt from anim.zbd.";
            // A mission database is noted when its rebuilt world does not give every node its shipped slot (the original
            // tool attached some records elsewhere, or its files are not all known); these missions' databases are exact.
            const string Database = ": the mission database ";
            Assert.All(report.Notes, n => Assert.True(n.EndsWith(Rebuilt, StringComparison.Ordinal) || n.Contains(Database, StringComparison.Ordinal), n));
            var inexact = report.Notes.Where(n => n.Contains(Database, StringComparison.Ordinal)).Select(n => n[..n.IndexOf(':')]).ToHashSet();
            string[] exact = ["m1", "m2", "m3", "m4", "m5", "m6", "m7", "m8", "m9", "m10", "m11", "m12", "m13"];
            foreach (string mission in exact)
                if (Directory.Exists(Path.Combine(corpus, mission))) Assert.DoesNotContain(mission, inexact);
            var rebuilt = report.Notes.Where(n => n.EndsWith(Rebuilt, StringComparison.Ordinal)).Select(n => Path.GetFileName(n[..n.IndexOf(':')])).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.DoesNotContain("sbarm.zad", rebuilt); Assert.DoesNotContain("deathmulti.zad", rebuilt);
            Assert.Equal(1, report.Families["scripts"]); Assert.Equal(3, report.Families["sounds"]);
            Assert.Equal(Directory.GetFiles(corpus, "gamez.zbd", SearchOption.AllDirectories).Length, report.Families["worlds"]);
            Assert.Equal(Directory.GetFiles(corpus, "anim.zbd", SearchOption.AllDirectories).Length, report.Families["animations"]);
            Assert.Equal(Directory.GetFiles(corpus, "zrdr.zbd", SearchOption.AllDirectories).Length, report.Families["resources"]);
            Assert.All(report.NotReconstructed, f => Assert.DoesNotContain("zrdr.zbd", f));
            // The original layout, and nothing zStudio-specific.
            Assert.Equal(["data", "gamegen"], Directory.GetFileSystemEntries(project).Select(Path.GetFileName).Order().ToArray());
            Assert.True(File.Exists(Path.Combine(project, "data", "m1", "zrdr", "envmodels", "frcgate.zad")));
            Assert.True(File.Exists(Path.Combine(project, "gamegen", "support", "common.gw")));
            // No empty folders: one the scripts search but no shipped file came from (the multiplayer missions' vehicle
            // folders, data/effects) is not created.
            Assert.DoesNotContain(Directory.GetDirectories(project, "*", SearchOption.AllDirectories), d => !Directory.EnumerateFileSystemEntries(d).Any());
            Assert.False(Directory.Exists(Path.Combine(project, "data", "m7", "models", "bft")));
            // Vehicles the missions load identically are one file; effects are kept together in data/common/effects.
            Assert.True(File.Exists(Path.Combine(project, "data", "common", "models", "ntank.gltf")));
            Assert.False(File.Exists(Path.Combine(project, "data", "m1", "models", "bft", "ntank.gltf")));
            Assert.True(File.Exists(Path.Combine(project, "data", "m1", "models", "bft", "bft_m1.gltf")));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(project, "data", "common", "effects", "models"), "*.gltf"));
            Assert.NotEmpty(Directory.GetFiles(Path.Combine(project, "data", "common", "effects", "textures"), "*.png"));
            Assert.False(Directory.Exists(Path.Combine(project, "data", "effects")));
            // Animation definitions are .zad files, never zReader resources; pickup.zrd keeps only its pickup data.
            Assert.True(File.Exists(Path.Combine(project, "data", "m1", "zrdr", "anim.zad")));
            Assert.True(File.Exists(Path.Combine(project, "data", "common", "zrdr", "pickup.zad")));
            Assert.All(Directory.GetFiles(Path.Combine(project, "data"), "*.zrd", SearchOption.AllDirectories), f =>
                Assert.False(Recoil.Zbd.Core.Animation.AnimationDefinitionSet.HoldsDefinitions(ZrdText.LooksLikeText(File.ReadAllBytes(f)) ? ZrdText.Parse(File.ReadAllBytes(f), Token) : ZrdDecoder.Read(File.ReadAllBytes(f), Token)), f));

            var export = await SourceBuilder.ExportAsync(project, exported, token: Token);
            Assert.Equal(0, export.Failed);
            Assert.Equal(SourceBuilder.Plan(project, token: TestContext.Current.CancellationToken).Count, export.Built);

            foreach (string archive in Directory.GetFiles(corpus, "zrdr.zbd", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(corpus, archive);
                var built = Members(Path.Combine(exported, relative));
                // The shipped resources without the animation definitions, which stay in the project.
                Dictionary<string, byte[]> expected = new(StringComparer.OrdinalIgnoreCase);
                foreach (var (name, bytes) in Members(archive))
                    if (ZrdDecoder.TryRead(bytes, Token) is { } tree && Recoil.Zbd.Core.Animation.AnimationDefinitionSet.HoldsDefinitions(tree))
                    { if (Recoil.Zbd.Core.Animation.AnimationDefinitionSet.Split(tree).Others is { } others) expected[name] = ZrdWriter.Write(others, Token); }
                    else expected[name] = bytes;
                Assert.Equal(expected.Keys.Order(StringComparer.OrdinalIgnoreCase), built.Keys.Order(StringComparer.OrdinalIgnoreCase));
                Assert.All(expected, m => Assert.Equal(m.Value, built[m.Key]));
            }
            // Animations rebuilt from the definitions and keyframe scripts match every shipped entry.
            foreach (string animations in Directory.GetFiles(corpus, "anim.zbd", SearchOption.AllDirectories))
            {
                var shipped = Recoil.Zbd.Core.Animation.AnimationPackage.Read(File.ReadAllBytes(animations), Token);
                var built = Recoil.Zbd.Core.Animation.AnimationPackage.Read(File.ReadAllBytes(Path.Combine(exported, Path.GetRelativePath(corpus, animations))), Token);
                Assert.Equal(shipped.Entries.Select(e => e.Name), built.Entries.Select(e => e.Name));
                Assert.All(Enumerable.Range(1, shipped.Entries.Count - 1), i => Assert.Null(Recoil.Zbd.Core.Animation.AnimationComparer.Difference(shipped.Entries[i], built.Entries[i])));
            }
            // Worlds rebuilt by their scripts have the shipped nodes, placements, flags, cells, models and textures; only the
            // grouping of coplanar triangles into polygons may differ, which draws the same surfaces.
            foreach (string world in Directory.GetFiles(corpus, "gamez.zbd", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(corpus, world);
                var shipped = World(world); var exportedWorld = World(Path.Combine(exported, relative));
                var differences = WorldComparer.Compare(shipped, exportedWorld);
                Assert.All(differences, d => Assert.Equal("model.polygons", d.Field));
                // The lookups by name the game makes when the mission loads find the shipped nodes: its texture-effect
                // scripts' FindNode and the animations' roots (the highest slot of a name first).
                string mission = Path.GetDirectoryName(relative)!;
                // An exact database's world holds every node in its shipped slot, under the same parents, and its models are
                // shared by the same nodes (copies of one cache share its models).
                if (exact.Contains(mission)) { Assert.Empty(SlotDifferences(shipped, exportedWorld).Take(5)); Assert.Empty(ModelDifferences(shipped, exportedWorld).Take(5)); }
                Assert.Empty(Lookups(shipped, exportedWorld, Path.Combine(project, SourceProject.GameGenFolder), mission, Path.Combine(Path.GetDirectoryName(world)!, "anim.zbd")));
            }
            // The scripts build as shipped, naming the project's glTF models and PNG textures instead of the original files.
            var scripts = Scripts(Path.Combine(corpus, "interp.zbd"), projectNames: true); var builtScripts = Scripts(Path.Combine(exported, "interp.zbd"));
            Assert.Equal(scripts.Keys.Order(StringComparer.OrdinalIgnoreCase), builtScripts.Keys.Order(StringComparer.OrdinalIgnoreCase));
            Assert.All(scripts, s => Assert.Equal(s.Value, builtScripts[s.Key]));
            foreach (string script in Directory.GetFiles(Path.Combine(project, SourceProject.GameGenFolder), "*.g?", SearchOption.AllDirectories))
                foreach (var tokens in GameGenScriptText.Tokenize(File.ReadAllText(script, System.Text.Encoding.Latin1)))
                {
                    if (tokens[0] == "LoadGameGen" && tokens.Count > 1 && !tokens[1].Contains('%')) Assert.EndsWith(".gltf", tokens[1], StringComparison.OrdinalIgnoreCase);
                    if (tokens[0].StartsWith("set", StringComparison.Ordinal) && tokens.Count > 2 && tokens[1] == "dbName") Assert.EndsWith(".gltf", tokens[2], StringComparison.OrdinalIgnoreCase);
                    Assert.DoesNotContain(tokens, t => t.EndsWith(".tif", StringComparison.OrdinalIgnoreCase));
                }
            foreach (string bank in new[] { "soundsh.zbd", "soundsm.zbd", "soundsl.zbd" })
            {
                var shipped = Members(Path.Combine(corpus, bank)); var built = Members(Path.Combine(exported, bank));
                Assert.Equal(shipped.Keys.Order(StringComparer.OrdinalIgnoreCase), built.Keys.Order(StringComparer.OrdinalIgnoreCase));
                foreach (var (name, bytes) in shipped)
                {
                    var expected = WaveDecoder.Read(bytes, Token); var actual = WaveDecoder.Read(built[name], Token);
                    Assert.Equal((expected.SampleRate, expected.BitsPerSample, expected.Channels), (actual.SampleRate, actual.BitsPerSample, actual.Channels));
                    Assert.InRange(actual.DataLength / actual.BlockAlign - expected.DataLength / expected.BlockAlign, -2, 2);
                    Assert.Equal(expected.Cues.Count, actual.Cues.Count);
                    // The best-quality bank is the source; its samples already satisfy their declarations.
                    if (bank == "soundsh.zbd") Assert.Equal(bytes, built[name]);
                }
            }

            // Every keyframe script is the SI Animation Script the shipped keyframes came from.
            var scriptsWritten = Directory.GetFiles(Path.Combine(project, "data"), "*.zan", SearchOption.AllDirectories);
            Assert.NotEmpty(scriptsWritten);
            Assert.All(scriptsWritten, s => Assert.True(Recoil.Zbd.Core.Animation.SiAnimationScript.Recognize(File.ReadAllBytes(s)), s));
            // The shipped files are stamped, so their scripts carry the DKit messages of their exporter.
            Assert.All(scriptsWritten, s => Assert.Contains("\r\nWarning, file version ", File.ReadAllText(s, System.Text.Encoding.Latin1)));

            // A project is unpacked once, from the original files; its exports carry no definitions and are refused.
            var refused = await Assert.ThrowsAsync<InvalidDataException>(() => SourceExtractor.ExtractAsync(exported, again, token: Token));
            Assert.Equal(SourceExtractor.NotOriginal, refused.Message);
            Assert.False(Directory.Exists(again));

            // A vehicle only another mission loads (the 1999 light tank of m2–m6) added to m1: the export of m1 holds its
            // nodes, every texture its materials use in each pack, and the animations other missions list for it.
            var m1 = World(Path.Combine(exported, "m1", "gamez.zbd"));
            HashSet<string> present = new(m1.Nodes.Select(n => n.Name), StringComparer.Ordinal);
            var models = SourceWorlds.Models(project, TestContext.Current.CancellationToken);
            var vehicle = models.FirstOrDefault(m => m.Path == "data/m2/models/bft/ltank.gltf") ??
                models.First(m => m.Folder.EndsWith("/models/bft", StringComparison.Ordinal) && !m.Folder.StartsWith("data/m1/", StringComparison.Ordinal) && !present.Contains(m.Name));
            Assert.DoesNotContain(vehicle.Name, present);
            var definitions = SourceWorlds.DefinitionsFor(project, "m1", vehicle.Name, token: Token);
            SourceWorkspace edits = new(project);
            SourceWorlds.AddModel(edits, "m1", new(new(vehicle.Path, vehicle.Name), definitions.Select(d => d.Path).ToArray()), Token);
            edits.Save(Token);
            string added = Path.Combine(work, "added");
            var withVehicle = await SourceBuilder.ExportAsync(project, added, ["m1/gamez.zbd", "m1/anim.zbd", "m1/rtexture16.zbd", "m1/texture2.zbd"], token: Token);
            Assert.Equal(0, withVehicle.Failed);
            var vehicleWorld = World(Path.Combine(added, "m1", "gamez.zbd"));
            var root = vehicleWorld.Nodes.Single(n => n.Name == vehicle.Name);
            Assert.Empty(root.Parents);
            HashSet<string> textures = new(StringComparer.OrdinalIgnoreCase);
            Collect(root, 0);
            Assert.NotEmpty(textures);
            foreach (string pack in new[] { "rtexture16.zbd", "texture2.zbd" })
            {
                var names = FormatRegistry.Default.OpenBytes(pack, File.ReadAllBytes(Path.Combine(added, "m1", pack)), token: Token).Assets.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Assert.All(textures, t => Assert.Contains(t, names));
            }
            var package = Recoil.Zbd.Core.Animation.AnimationPackage.Read(File.ReadAllBytes(Path.Combine(added, "m1", "anim.zbd")), Token);
            if (definitions.Count > 0) Assert.Contains(package.Entries, e => e.RootName == vehicle.Name);
            void Collect(WorldNode node, int depth)
            {
                Assert.True(depth < 512);
                foreach (var polygon in node.Model?.Polygons ?? []) if (polygon.Material?.Texture is { } texture) textures.Add(texture.Name);
                foreach (var child in node.Children) Collect(child, depth + 1);
            }
        }
        finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
    }

    private static Dictionary<string, byte[]> Members(string path)
    {
        var doc = FormatRegistry.Default.OpenBytes(path, File.ReadAllBytes(path), token: Token);
        Dictionary<string, byte[]> members = new(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in doc.Assets) members.TryAdd(asset.Name, doc.Slice(asset.Offset, asset.Length).ToArray());
        return members;
    }
    /// <summary>
    /// The names a mission's texture-effect scripts (FindNode) and animation roots look up whose node in the rebuilt world
    /// is not the shipped node's counterpart. Roots: consecutive entries with one name take the following same-named
    /// nodes, else the highest slot (docs/engine-evidence.md).
    /// </summary>
    /// <summary>Models used by other nodes (by slot) in one world than in the other, and a different number of models.</summary>
    private static IEnumerable<string> ModelDifferences(GameZWorld shipped, GameZWorld built)
    {
        static HashSet<string> Users(GameZWorld w)
        {
            var slots = GameZWriter.NodeSlots(w);
            return [.. w.Nodes.Where(n => n.Model != null).GroupBy(n => n.Model!, ReferenceEqualityComparer.Instance).Select(g => string.Join(",", g.Select(n => slots[n]).Order()))];
        }
        if (shipped.Models.Count != built.Models.Count) yield return $"{shipped.Models.Count} models shipped, {built.Models.Count} built";
        var a = Users(shipped); var b = Users(built);
        foreach (var users in a.Except(b)) yield return $"the model of slots {users} is not one model built";
        foreach (var users in b.Except(a)) yield return $"slots {users} share a model built but not shipped";
    }

    /// <summary>Slots that do not hold the same node in both worlds: its name, its parents' slots and its children's slots.</summary>
    private static IEnumerable<string> SlotDifferences(GameZWorld shipped, GameZWorld built)
    {
        var a = GameZWriter.NodeSlots(shipped); var b = GameZWriter.NodeSlots(built);
        var bySlot = b.ToDictionary(p => p.Value, p => p.Key);
        string Key(WorldNode n, IReadOnlyDictionary<WorldNode, int> s) => n.Name + "|" + string.Join(",", n.Parents.Select(p => s[p]).Order()) + "|" + string.Join(",", n.Children.Select(c => s[c]));
        if (a.Count != b.Count) yield return $"{a.Count} nodes shipped, {b.Count} built";
        foreach (var (node, slot) in a.OrderBy(p => p.Value))
            if (!bySlot.TryGetValue(slot, out var other) || Key(node, a) != Key(other, b)) yield return $"slot {slot}: {Key(node, a)} shipped, {(other == null ? "nothing" : Key(other, b))} built";
    }

    private static List<string> Lookups(GameZWorld shipped, GameZWorld rebuilt, string gameGen, string mission, string animation)
    {
        Dictionary<WorldNode, WorldNode> counterpart = new(ReferenceEqualityComparer.Instance);
        void Walk(WorldComparisonNode n) { if (n.Expected is { } x && n.Actual is { } y) counterpart.TryAdd(x, y); foreach (var c in n.Children) Walk(c); }
        foreach (var root in WorldComparer.CompareTree(shipped, rebuilt, token: Token).Roots) Walk(root);
        Dictionary<string, List<WorldNode>> ByName(GameZWorld w) { var slots = GameZWriter.NodeSlots(w); return w.Nodes.GroupBy(n => n.Name).ToDictionary(g => g.Key, g => g.OrderByDescending(n => slots[n]).ToList()); }
        var a = ByName(shipped); var b = ByName(rebuilt);
        List<string> wrong = [];
        void Check(string what, WorldNode? x, WorldNode? y) { if (x == null ? y != null : !ReferenceEquals(counterpart.GetValueOrDefault(x), y)) wrong.Add($"{mission}: {what}"); }
        foreach (string script in new[] { $"{mission}_zbd.gs", "support/tex_fx.gw", $"support/tex_fx{mission}.gw" }.Select(f => Path.Combine(gameGen, f)).Where(File.Exists))
            foreach (var tokens in GameGenScriptText.Tokenize(File.ReadAllText(script, System.Text.Encoding.Latin1)))
                if (tokens.Count > 1 && tokens[0].Equals("FindNode", StringComparison.OrdinalIgnoreCase) && !tokens[1].StartsWith('%'))
                    Check($"FindNode {tokens[1]}", a.GetValueOrDefault(tokens[1])?.FirstOrDefault(), b.GetValueOrDefault(tokens[1])?.FirstOrDefault());
        if (File.Exists(animation))
        {
            var entries = AnimationPackage.Read(File.ReadAllBytes(animation), Token).Entries;
            string? previous = null; int position = 0;
            for (int i = 1; i < entries.Count; i++)
            {
                string name = entries[i].RootName; position = name == previous ? position + 1 : 0; previous = name;
                var x = a.GetValueOrDefault(name) ?? []; var y = b.GetValueOrDefault(name) ?? [];
                WorldNode? rootX = x.Count == 0 ? null : x[position % x.Count], rootY = y.Count == 0 ? null : y[position % y.Count];
                Check($"animation {entries[i].Name} root {name}", rootX, rootY);
                // Names inside the animation: the attach node's subtree, the root's (preorder, children in order), the entry's own
                // lights and sounds, then the whole world. Child order decides between copies, as m2's call_chopper shows.
                HashSet<string> own = [.. entries[i].References[2].Skip(1).Concat(entries[i].References[3].Skip(1)).Select(r => r.Text(0, 36))];
                WorldNode? Resolve(Dictionary<string, List<WorldNode>> byName, WorldNode? callback, WorldNode? root, string n) =>
                    First(callback, n) ?? First(root, n) ?? (own.Contains(n) ? null : byName.GetValueOrDefault(n)?.FirstOrDefault());
                WorldNode? attachX = Resolve(a, null, rootX, entries[i].AttachName), attachY = Resolve(b, null, rootY, entries[i].AttachName);
                foreach (var reference in entries[i].References[0].Skip(1).Concat(entries[i].References[1].Skip(1)).Select(r => r.Text(0, 36)).Where(n => n.Length > 0).Distinct())
                {
                    WorldNode? foundX = Resolve(a, attachX, rootX, reference), foundY = Resolve(b, attachY, rootY, reference);
                    if (foundX == null ? foundY != null : counterpart.GetValueOrDefault(foundX) is not { } c || !(ReferenceEquals(c, foundY) || foundY != null && WorldComparer.Interchangeable(c, foundY, 0)))
                        wrong.Add($"{mission}: animation {entries[i].Name} name {reference}");
                }
            }
        }
        return wrong;
    }
    /// <summary>The engine's search of a subtree by name (zEffectAnim::FindNodeRecursiveByName): preorder, children first to last.</summary>
    private static WorldNode? First(WorldNode? node, string name)
    {
        if (node == null) return null;
        if (node.Name == name) return node;
        foreach (var child in node.Children) if (First(child, name) is { } found) return found;
        return null;
    }
    private static GameZWorld World(string path) => GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes(path, File.ReadAllBytes(path), token: Token), Token);
    private static Dictionary<string, string> Scripts(string path, bool projectNames = false)
    {
        var doc = FormatRegistry.Default.OpenBytes(path, File.ReadAllBytes(path), token: Token);
        var entries = doc.Scripts!.Entries.ToDictionary(e => e.Name, e => (IReadOnlyList<IReadOnlyList<string>>)[.. e.Instructions.Select(i => (IReadOnlyList<string>)i.Tokens)], StringComparer.OrdinalIgnoreCase);
        var macros = GameGenScriptText.ModelMacros(entries.Values);
        return entries.ToDictionary(e => e.Key, e => string.Join("\n", (projectNames ? GameGenScriptText.ProjectFileNames(e.Value, macros) : e.Value).Select(t => string.Join("\u0001", t))), StringComparer.OrdinalIgnoreCase);
    }
}
