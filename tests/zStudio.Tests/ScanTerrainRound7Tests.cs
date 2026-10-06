using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Every listing of a project's folders (data's mission folders, the folders a save writes into, the recovery and staging
/// folders, the world previews) and of a destination's mission folder counts every entry it visits and gives way to
/// cancellation; terrain conversion and the comparison of an instance's copies work out what a shared mesh or node holds
/// once, however many pieces or copies use it.
/// </summary>
public sealed class ScanTerrainRound7Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static CancellationToken Canceled { get { CancellationTokenSource source = new(); source.Cancel(); return source.Token; } }

    private sealed class Project : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-round7-" + Guid.NewGuid().ToString("N"));
        public Project()
        {
            Directory.CreateDirectory(Full(SourceProject.DataFolder));
            Directory.CreateDirectory(Full(SourceProject.GameGenFolder));
        }
        public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        public void Write(string relative, string text = "")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Full(relative))!);
            File.WriteAllText(Full(relative), text);
        }
        /// <summary>Folders an editor or a backup tool leaves beside the sources.</summary>
        public void Clutter(string folder, int count)
        {
            for (int i = 0; i < count; i++) Directory.CreateDirectory(Full($"{folder}/backup{i}"));
        }
        public void Dispose()
        {
            try { Directory.Delete(Root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void PlanningListsDataForItsMissionsAsAScan()
    {
        using Project project = new();
        // Thirty folders and ten files beside the missions; the other scans of planning find empty or missing folders.
        project.Clutter("data", 30);
        for (int i = 0; i < 10; i++) project.Write($"data/notes{i}.txt");
        Assert.Empty(SourceBuilder.Plan(project.Root, token: Token));
        // Canceled while it lists data, where nothing else would observe the token.
        Assert.ThrowsAny<OperationCanceledException>(() => SourceBuilder.Plan(project.Root, token: Canceled));
        // Every entry counts, a mission folder or not.
        project.Write("data/m1/zrdr/ai.zrd", "GRAVITY ( -9.8 )\n");
        Assert.Equal(["m1"], SourceProject.MissionFolders(project.Root, Token, 41));
        var refused = Assert.Throws<IOException>(() => SourceProject.MissionFolders(project.Root, Token, 40));
        Assert.Contains("data holds more than 40 files and folders", refused.Message);
        Assert.Contains("Move files the build does not use", refused.Message);
        // Missions in number order as the disk spells them; a name with other digits than 0–9 is no mission folder.
        Directory.CreateDirectory(project.Full("data/M10")); Directory.CreateDirectory(project.Full("data/m2")); Directory.CreateDirectory(project.Full("data/m١"));
        Assert.Equal(["m1", "m2", "M10"], SourceProject.MissionFolders(project.Root, Token));
        Assert.Equal(["m1/zrdr.zbd"], SourceBuilder.Plan(project.Root, token: Token).Select(p => p.Path));
    }

    [Fact]
    public void DefinitionsForAModelListDataAsAScan()
    {
        using Project project = new();
        project.Clutter("data", 30);
        Directory.CreateDirectory(project.Full("data/m1"));
        Assert.Empty(SourceWorlds.DefinitionsFor(project.Root, "m1", "tank", null, Token));
        // No other mission to look at: only the listing of data observes the cancellation.
        Assert.ThrowsAny<OperationCanceledException>(() => SourceWorlds.DefinitionsFor(project.Root, "m1", "tank", null, Canceled));
    }

    [Fact]
    public void ASaveCountsTheEntriesOfTheFoldersItWritesInto()
    {
        using Project project = new();
        project.Write("gamegen/m1.gs", "Quit\n");
        for (int i = 0; i < 30; i++) project.Write($"data/m1/zrdr/old{i}.zrd", "OLD ( 1 )\n");
        SourceFileWrite[] writes = [new("data/m1/zrdr/new.zrd", null, Encoding.ASCII.GetBytes("NEW ( 2 )\n"))];
        // The project folder, data, m1 and zrdr are listed: more than 20 entries together.
        var refused = Assert.Throws<IOException>(() => new SourcePublisher(project.Root) { ScanLimit = 20 }.Publish(writes, "new", Token));
        Assert.Contains("The folders this save writes into hold more than 20 files and folders", refused.Message);
        Assert.False(File.Exists(project.Full("data/m1/zrdr/new.zrd")));
        new SourcePublisher(project.Root).Publish(writes, "new", Token);
        Assert.Equal("NEW ( 2 )\n", File.ReadAllText(project.Full("data/m1/zrdr/new.zrd")));
    }

    [Fact]
    public void TheRecoveryFolderIsListedAsAScan()
    {
        using Project project = new();
        project.Write("gamegen/m1.gs", "Quit\n");
        project.Write("data/m1/zrdr/ai.zrd", "GRAVITY ( -9.8 )\n");
        // Folders another program left where zStudio keeps its save journals: no journals, but each is looked at.
        project.Clutter(SourcePublisher.RecoveryFolder, 30);
        Assert.Empty(new SourcePublisher(project.Root).FindInterrupted(Token));
        var refused = Assert.Throws<IOException>(() => new SourcePublisher(project.Root) { ScanLimit = 20 }.FindInterrupted(Token));
        Assert.Contains($"{SourcePublisher.RecoveryFolder} holds more than 20 files and folders", refused.Message);
        Assert.ThrowsAny<OperationCanceledException>(() => new SourcePublisher(project.Root).FindInterrupted(Canceled));
        // A save tidies the folder before it writes anything, and is refused the same way.
        SourceFileWrite[] writes = [new("data/m1/zrdr/ai.zrd", Encoding.ASCII.GetBytes("GRAVITY ( -9.8 )\n"), Encoding.ASCII.GetBytes("GRAVITY ( -4.9 )\n"))];
        var save = Assert.Throws<IOException>(() => new SourcePublisher(project.Root) { ScanLimit = 20 }.Publish(writes, "edit", Token));
        Assert.Contains($"{SourcePublisher.RecoveryFolder} holds more than 20 files and folders", save.Message);
        Assert.Equal("GRAVITY ( -9.8 )\n", File.ReadAllText(project.Full("data/m1/zrdr/ai.zrd")));
        // So is the staging folder (the recovery folder holds 31 entries with the lock file).
        project.Clutter(SourcePublisher.StagingFolder, 40);
        var staging = Assert.Throws<IOException>(() => new SourcePublisher(project.Root) { ScanLimit = 35 }.Publish(writes, "edit", Token));
        Assert.Contains($"{SourcePublisher.StagingFolder} holds more than 35 files and folders", staging.Message);
        new SourcePublisher(project.Root).Publish(writes, "edit", Token);
        Assert.Equal("GRAVITY ( -4.9 )\n", File.ReadAllText(project.Full("data/m1/zrdr/ai.zrd")));
    }

    [Fact]
    public void AbandonedWorldBuildsAreFoundByABoundedListing()
    {
        using Project project = new();
        string previews = SourceWorlds.PreviewRoot(project.Root);
        string old = Path.Combine(previews, "old"), locked = Path.Combine(previews, "locked"), fresh = Path.Combine(previews, "fresh");
        foreach (string folder in new[] { old, locked, fresh }) Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(locked, ".lock"), "");
        File.WriteAllText(Path.Combine(previews, "stray.txt"), "");
        foreach (string folder in new[] { old, locked }) Directory.SetCreationTimeUtc(folder, DateTime.UtcNow.AddMinutes(-5));
        // Only the old folder no session holds; the new one may be a session's that has not locked it yet.
        Assert.Equal([old], SourceWorlds.AbandonedBuilds(previews, Token));
        var refused = Assert.Throws<IOException>(() => SourceWorlds.AbandonedBuilds(previews, 3, Token).ToList());
        Assert.Contains("holds more than 3 files and folders", refused.Message);
        Assert.ThrowsAny<OperationCanceledException>(() => SourceWorlds.AbandonedBuilds(previews, Canceled).ToList());
        Assert.Empty(SourceWorlds.AbandonedBuilds(Path.Combine(previews, "missing"), Token));
    }

    [Fact]
    public void ADestinationsPacksAreListedAsAScan()
    {
        using Project project = new();
        string destination = project.Full("out");
        foreach (string name in new[] { "rtexture8.zbd", "RTexture16.ZBD", "texture4.zbd", "texturemax.zbd", "rtexture2.zbd", "gamez.zbd", "texture.txt" }) project.Write($"out/m1/{name}");
        // A folder named like a pack is no pack.
        Directory.CreateDirectory(project.Full("out/m1/rtexture32.zbd"));
        Assert.Equal(["m1/rtexture16.zbd", "m1/rtexture8.zbd", "m1/texture4.zbd", "m1/texturemax.zbd"], BuildProfiles.ShadowingPacks(destination, "m1", ["rtexture2.zbd"], Token));
        Assert.Equal(4, BuildProfiles.ShadowingPacks(destination, "m1", ["rtexture2.zbd"], 8, Token).Count);
        var refused = Assert.Throws<IOException>(() => BuildProfiles.ShadowingPacks(destination, "m1", [], 7, Token));
        Assert.Contains("holds more than 7 files and folders", refused.Message);
        Assert.ThrowsAny<OperationCanceledException>(() => BuildProfiles.ShadowingPacks(destination, "m1", [], Canceled));
        Assert.Empty(BuildProfiles.ShadowingPacks(destination, "m2", [], Token));
    }

    /// <summary>
    /// A mission database of <paramref name="pieces"/> root nodes that all use one mesh with <paramref name="values"/> as its
    /// model values: a triangle lying flat (it covers ground, so every piece stacks on the others), one standing upright (it
    /// covers none, so all fit one surface), or <paramref name="splinters"/> triangles too small to cover anything.
    /// </summary>
    private static SourceWorkspace Database(Project project, int pieces, JsonObject values, bool flat = false, int splinters = 0)
    {
        GltfMesh mesh = new() { Name = "piece", Extras = new JsonObject { [WorldGltf.Key] = values } };
        GltfPrimitive primitive = new();
        if (splinters > 0)
            for (int i = 0; i < splinters; i++)
            {
                float x = i % 100, z = i / 100;
                primitive.Positions.AddRange([new(x, 0, z), new(x + 0.0001f, 0, z), new(x, 0, z + 0.0001f)]);
                primitive.Indices.AddRange([3 * i, 3 * i + 1, 3 * i + 2]);
            }
        else
        {
            primitive.Positions.AddRange(flat ? [new(0, 0, 0), new(10, 0, 0), new(0, 0, 10)] : [new(0, 0, 0), new(0, 10, 0), new(10, 10, 0)]);
            primitive.Indices.AddRange([0, 1, 2]);
        }
        mesh.Primitives.Add(primitive);
        GltfDocument doc = new();
        for (int i = 0; i < pieces; i++) doc.Roots.Add(new GltfNode { Name = $"piece{i}", Mesh = mesh });
        var (json, bin) = doc.Write("m1.bin");
        Directory.CreateDirectory(project.Full("data/m1/models"));
        File.WriteAllBytes(project.Full("data/m1/models/m1.gltf"), json);
        File.WriteAllBytes(project.Full("data/m1/models/m1.bin"), bin);
        return new(project.Root);
    }
    private const string Database1 = "data/m1/models/m1.gltf";
    private static readonly (HashSet<string> Names, IReadOnlyList<Regex> Patterns) NoReferences = ([], []);
    /// <summary>Plans the conversion, returning what planning allocated on this thread.</summary>
    private static (TerrainConversionPlan Plan, long Allocated) Measured(SourceWorkspace workspace)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        var plan = SourceTerrainConversion.Plan(workspace, Database1, NoReferences, Token);
        return (plan, GC.GetAllocatedBytesForCurrentThread() - before);
    }
    /// <summary>What planning these databases takes at most; each amplification it guards against takes gigabytes.</summary>
    private const long Bound = 64L * 1024 * 1024;

    [Fact]
    public void PiecesSharingAMeshWriteItsValuesOnce()
    {
        using Project project = new();
        // A thousand pieces use one mesh whose model values hold a megabyte: written out once, not once per piece (a gigabyte).
        JsonObject values = new() { ["flags"] = 2, ["note"] = new string('x', 1 << 20) };
        var (plan, allocated) = Measured(Database(project, 1000, values));
        Assert.True(allocated < Bound, $"Planning allocated {allocated:N0} bytes.");
        var surface = Assert.Single(plan.Groups);
        Assert.Equal(1000, surface.Nodes.Count);
        Assert.Equal("zany_01000018_m" + SourceProject.Sha256(Encoding.UTF8.GetBytes(values.ToJsonString()))[..6], surface.Id);
        Assert.True(JsonNode.DeepEquals(values, surface.ModelValues));
    }

    [Fact]
    public void AMeshsModeIsReadOncePerMesh()
    {
        using Project project = new();
        // A facade mode of a megabyte keeps every piece an object; it is read once, not once per piece.
        var (plan, allocated) = Measured(Database(project, 1000, new() { ["mode"] = new string('7', 1 << 20) }));
        Assert.True(allocated < Bound, $"Planning allocated {allocated:N0} bytes.");
        Assert.Empty(plan.Groups);
        Assert.Equal(1000, plan.Kept.Count);
        Assert.All(plan.Kept, k => Assert.Equal("a facade or point model", k.Reason));
    }

    [Fact]
    public void StackedSheetsReadTheirValuesBackOnce()
    {
        using (Project project = new())
        {
            // Two hundred pieces lie on each other: two hundred surfaces with the same two megabytes of values, read back once
            // (their overlaps take about 70 MB to find; reading the values back for each surface takes 400 MB more).
            JsonObject values = new() { ["flags"] = 2, ["note"] = new string('x', 2 << 20) };
            var (plan, allocated) = Measured(Database(project, 200, values, flat: true));
            Assert.True(allocated < 160L * 1024 * 1024, $"Planning allocated {allocated:N0} bytes.");
            Assert.Equal(200, plan.Groups.Count);
            Assert.All(plan.Groups, g => { Assert.Single(g.Nodes); Assert.True(JsonNode.DeepEquals(values, g.ModelValues)); });
            Assert.Equal(Enumerable.Range(0, 200), plan.Groups.Select(g => g.Nodes[0]));
        }
        using (Project project = new())
        {
            // Applied, each stacked surface's mesh carries the values of its own.
            JsonObject values = new() { ["flags"] = 2, ["scroll"] = new JsonArray(0.5, 0.25, 3) };
            var workspace = Database(project, 3, values, flat: true);
            var plan = SourceTerrainConversion.Plan(workspace, Database1, NoReferences, Token);
            Assert.Equal(3, plan.Groups.Count);
            SourceTerrainConversion.Apply(workspace, plan, Token);
            var surfaces = JsonNode.Parse(workspace.Read(plan.Surfaces, Token)!)!;
            Assert.Equal(3, surfaces["meshes"]!.AsArray().Count);
            Assert.All(surfaces["meshes"]!.AsArray(), m => Assert.True(JsonNode.DeepEquals(values, m!["extras"]![WorldGltf.Key])));
        }
    }

    [Fact]
    public void AMeshsAreaIsWorkedOutOnce()
    {
        using Project project = new();
        // A thousand pieces use a mesh of ten thousand splinters: its area is worked out once, not once per piece.
        var (plan, allocated) = Measured(Database(project, 1000, new() { ["flags"] = 2 }, splinters: 10_000));
        Assert.True(allocated < Bound, $"Planning allocated {allocated:N0} bytes.");
        Assert.Equal(1000, Assert.Single(plan.Groups).Nodes.Count);
    }

    /// <summary>An instance's copies (roots marked as one instance) with the given engine values and meshes.</summary>
    private static GltfDocument Copies(int count, Func<int, JsonObject> values, Func<int, GltfMesh?> mesh)
    {
        GltfDocument doc = new();
        for (int i = 0; i < count; i++) doc.Roots.Add(new GltfNode { Name = "copy", Mesh = mesh(i), Extras = new JsonObject { [WorldGltf.Key] = values(i) } });
        return doc;
    }
    private static JsonArray Zeros(int count) => new(Enumerable.Repeat(0, count).Select(z => (JsonNode?)z).ToArray());
    /// <summary>A mesh of <paramref name="parts"/> morphing triangles, its values noting <paramref name="note"/>.</summary>
    private static GltfMesh Mesh(int parts, int note = 0)
    {
        GltfMesh mesh = new() { Name = "m", Extras = new JsonObject { [WorldGltf.Key] = new JsonObject { ["note"] = note } } };
        for (int i = 0; i < parts; i++)
        {
            GltfPrimitive primitive = new();
            primitive.Positions.AddRange([new(i, 0, 0), new(i + 1, 0, 0), new(i, 0, 1)]);
            primitive.Indices.AddRange([0, 1, 2]);
            primitive.Targets.Add([new(0, 1, 0), new(0, 1, 0), new(0, 1, 0)]);
            mesh.Primitives.Add(primitive);
        }
        mesh.Weights.Add(0.5f);
        return mesh;
    }
    private static long Allocation(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void AnInstancesCopiesAreComparedWithoutCopyingTheFirstOnesValues()
    {
        // The first copy states a long zoneFromLoad, which the comparison leaves out; it is not copied for each other copy.
        var doc = Copies(300, i => i == 0 ? new() { ["instance"] = 1, [WorldGltf.ZoneFromLoad] = Zeros(50_000) } : new() { ["instance"] = 1 }, _ => null);
        long allocated = Allocation(() => WorldGltf.CheckInstances(doc, "big.gltf"));
        Assert.True(allocated < 64L * 1024 * 1024, $"Comparing allocated {allocated:N0} bytes.");
        // Values that differ are still found, a missing one as null, as before.
        var differing = Copies(2, i => new() { ["instance"] = 1, ["flags"] = i == 0 ? "0x1" : "0x2" }, _ => null);
        Assert.Contains("differ in the engine values of", Assert.Throws<InvalidDataException>(() => WorldGltf.CheckInstances(differing, "x.gltf")).Message);
        var extra = Copies(2, i => i == 0 ? new() { ["instance"] = 1 } : new() { ["instance"] = 1, ["flags"] = "0x2" }, _ => null);
        Assert.Contains("differ in the engine values of", Assert.Throws<InvalidDataException>(() => WorldGltf.CheckInstances(extra, "x.gltf")).Message);
        var fewer = Copies(2, i => i == 1 ? new() { ["instance"] = 1 } : new() { ["instance"] = 1, ["flags"] = "0x2" }, _ => null);
        Assert.Contains("differ in the engine values of", Assert.Throws<InvalidDataException>(() => WorldGltf.CheckInstances(fewer, "x.gltf")).Message);
        // What copies may write differently is left out, and a value missing from the other compares as null (as DeepEquals does).
        WorldGltf.CheckInstances(Copies(2, i => new() { ["instance"] = 1, ["name"] = "copy", [WorldGltf.ZoneFromLoad] = i == 0 }, _ => null), "x.gltf");
        WorldGltf.CheckInstances(Copies(2, i => i == 0 ? new() { ["instance"] = 1, ["a"] = null } : new() { ["instance"] = 1, ["b"] = null }, _ => null), "x.gltf");
    }

    [Fact]
    public void TwoMeshesOfAnInstancesCopiesAreComparedOnce()
    {
        // The first copy's mesh and the mesh all others share are alike, 20,000 morphing triangles each: compared once, not
        // once for each of 299 copies (their morph targets are paired up for every comparison).
        GltfMesh first = Mesh(20_000), others = Mesh(20_000);
        var doc = Copies(300, _ => new() { ["instance"] = 1 }, i => i == 0 ? first : others);
        long allocated = Allocation(() => WorldGltf.CheckInstances(doc, "big.gltf"));
        Assert.True(allocated < 64L * 1024 * 1024, $"Comparing allocated {allocated:N0} bytes.");
        // Meshes that differ are still found, by their values or their triangles.
        var differing = Copies(3, _ => new() { ["instance"] = 1 }, i => i == 0 ? Mesh(1, note: 1) : Mesh(1, note: 2));
        Assert.Contains("differ in the mesh of", Assert.Throws<InvalidDataException>(() => WorldGltf.CheckInstances(differing, "x.gltf")).Message);
        var shapes = Copies(3, _ => new() { ["instance"] = 1 }, i => Mesh(i == 0 ? 1 : 2));
        Assert.Contains("differ in the mesh of", Assert.Throws<InvalidDataException>(() => WorldGltf.CheckInstances(shapes, "x.gltf")).Message);
    }

    [Fact]
    public void ACopysShapeRefusesANodeReachedTwice()
    {
        // Each node lists the next twice: eighteen levels would be copied 262,144 times over.
        JsonArray nodes = [];
        for (int i = 0; i < 18; i++) nodes.Add(new JsonObject { ["name"] = $"n{i}", ["children"] = new JsonArray(i + 1, i + 1) });
        nodes.Add(new JsonObject { ["name"] = "leaf" });
        // Refused at the first node reached again (the leaf, from its parent's second listing).
        var refused = Assert.Throws<InvalidDataException>(() => SourceObjectEdits.Shape(nodes, 0, 0));
        Assert.Contains("glTF node 18 is reached twice", refused.Message);
        // A tree is shaped as before: its own values with its children's, without their names.
        JsonArray tree = [new JsonObject { ["name"] = "a", ["mesh"] = 0, ["children"] = new JsonArray(1, 2) }, new JsonObject { ["name"] = "b" }, new JsonObject { ["name"] = "c", ["mesh"] = 1 }];
        Assert.Equal("{\"mesh\":0,\"children\":[{\"children\":[]},{\"mesh\":1,\"children\":[]}]}", SourceObjectEdits.Shape(tree, 0, 0).ToJsonString());
    }
}
