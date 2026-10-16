using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

/// <summary>
/// Which nodes a source world's script instructions act on, compared between the shown build and the rebuild after a copy
/// or move in a glTF file (<see cref="SourceObjectEdits.TargetChange"/>).
/// </summary>
public sealed class SourceObjectTargetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void SourceSelectionKeepsReferenceOccurrencesAtomicIncludingFreedGroups()
    {
        SourceInstruction load = new("gamegen/m1.gs", 11, "LoadGameGen", [], []);
        WorldNodeProvenance Map(int node) => new() { ModelFile = "data/m1/models/m1.gltf", ModelNode = node, Database = true, Load = load };
        WorldNodeProvenance Part(string file, int node, WorldNodeProvenance holder) => new()
        { ModelFile = file, ModelNode = node, ReferencedBy = holder, Load = load };
        var turret1 = Map(1); var turret2 = Map(2); var freed = Map(3);
        var gun = Part("data/common/models/turret.gltf", 1, turret1);
        var nested = Part("data/common/models/barrel.gltf", 0, gun);
        var otherGun = Part("data/common/models/turret.gltf", 1, turret2);
        var regionPart = Part("data/m1/models/region.gltf", 0, freed);
        var index = SourceSelectionIndex.Build(new Dictionary<int, WorldNodeProvenance>
        { [4] = turret1, [5] = gun, [6] = nested, [9] = turret2, [10] = otherGun, [12] = regionPart, [20] = Map(4) }, Token);
        Assert.Equal(4, index.Owners[5]); Assert.Equal(4, index.Owners[6]);
        Assert.Equal(9, index.Owners[10]); Assert.NotEqual(index.Identities[4], index.Identities[9]);
        int group = index.Owners[12]; Assert.True(group < -1); Assert.True(index.Boundaries[group].Synthetic);
        Assert.NotNull(index.Boundaries[group].ReadOnlyReason); Assert.Equal(20, index.Owners[20]);
        // Rebuilt slots and synthetic allocation order are not persistent identities.
        var rebuilt = SourceSelectionIndex.Build(new Dictionary<int, WorldNodeProvenance> { [32] = regionPart, [40] = turret2, [41] = otherGun, [50] = turret1, [51] = gun }, Token);
        Assert.Equal(index.Identities[group], rebuilt.Identities[rebuilt.Owners[32]]);
        Assert.Equal(index.Identities[4], rebuilt.Identities[50]);

        // Retail turrets use an authored placement object around the external-file holder. Its name/transform
        // are irrelevant to ownership: topology and the exact source occurrence identify the wrapper.
        var wrapper = Map(10); var secondWrapper = Map(11); var extraParent = Map(12);
        var origins = new Dictionary<int, WorldNodeProvenance> { [3] = wrapper, [4] = turret1, [5] = gun, [6] = nested, [8] = secondWrapper, [9] = turret2, [10] = otherGun, [11] = extraParent };
        var topology = origins.Keys.ToDictionary(i => i, i => Node("same"));
        Link(3, 4); Link(4, 5); Link(5, 6); Link(8, 9); Link(9, 10);
        var wrapped = SourceSelectionIndex.Build(origins, Token, topology);
        Assert.Equal(3, wrapped.Owners[4]); Assert.Equal(3, wrapped.Owners[5]); Assert.Equal(3, wrapped.Owners[6]);
        Assert.Equal(8, wrapped.Owners[10]); Assert.NotEqual(wrapped.Identities[3], wrapped.Identities[8]);
        Assert.Equal("data/common/models/turret.gltf", wrapped.Boundaries[3].Source);
        // A placement shared under several parents or a grouping object with another child must not absorb the reference.
        Link(11, 4); Link(8, 11);
        var separate = SourceSelectionIndex.Build(origins, Token, topology);
        Assert.Equal(4, separate.Owners[5]); Assert.Equal(9, separate.Owners[10]);
        // A script-created single-child group is independently editable: null source-file metadata is not wrapper evidence.
        SourceInstruction scriptLoad = new("gamegen/m1.gs", 30, "LoadGameGen", [], []);
        origins[30] = new() { Created = new("gamegen/m1.gs", 27, "NewObject3D", [], []) };
        origins[31] = new() { Created = scriptLoad, LoadedFile = "data/common/models/turret.gltf" };
        origins[32] = new() { ModelFile = "data/common/models/turret.gltf", ModelNode = 0, Load = scriptLoad };
        topology[30] = Node("holder"); topology[31] = Node("obj"); topology[32] = Node("hull");
        Link(30, 31); Link(31, 32);
        var scripted = SourceSelectionIndex.Build(origins, Token, topology);
        Assert.Equal(30, scripted.Owners[30]); Assert.Equal(31, scripted.Owners[31]); Assert.Equal(31, scripted.Owners[32]);
        // A node ordinal or script line can be reused by a structural source edit.
        // Captured content changes invalidate only dependent glTF identities;
        // any executed script change invalidates the map's placement context.
        Dictionary<string, string> hashes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["gamegen/m1.gs"] = SourceProject.Sha256("old script"u8),
            ["data/m1/models/m1.gltf"] = SourceProject.Sha256("map"u8),
            ["data/common/models/turret.gltf"] = SourceProject.Sha256("healthy, destroyed"u8),
            ["data/common/models/barrel.gltf"] = SourceProject.Sha256("barrel"u8)
        };
        var captured = SourceSelectionIndex.Build(origins, Token, topology, hashes);
        Assert.Equal(captured.Identities[5], SourceSelectionIndex.Build(origins, Token, topology, hashes).Identities[5]);
        hashes["data/common/models/turret.gltf"] = SourceProject.Sha256("destroyed, healthy"u8);
        var reordered = SourceSelectionIndex.Build(origins, Token, topology, hashes);
        Assert.NotEqual(captured.Identities[5], reordered.Identities[5]); Assert.NotEqual(captured.Identities[6], reordered.Identities[6]);
        Assert.Equal(captured.Identities[3], reordered.Identities[3]);
        hashes["gamegen/m1.gs"] = SourceProject.Sha256("replacement instruction at same line"u8);
        Assert.NotEqual(reordered.Identities[30], SourceSelectionIndex.Build(origins, Token, topology, hashes).Identities[30]);

        // The same sourced helper line executes with two macro values. Use the assembler's actual per-execution
        // provenance, including a fresh rebuild, rather than assuming SourceInstruction value equality is identity.
        using SourceWorldFixture fixture = new();
        var model = WorldGltf.Export([Node("hull")], 0xFF, new() { Texture = t => (t.Name + ".png", 0) }).Write("part.bin", Token);
        fixture.Write("data/m1/models/part.gltf", model.Json); fixture.Write("data/m1/models/part.bin", model.Binary);
        fixture.Write("gamegen/helper.gs", "LoadGameGen part.gltf %actor%\nFindNode world\nAddChild %actor%\n");
        const string script = "SetModelDirectory ..\\data\\m1\\models\nNewWorld world\nset actor first\nsource helper.gs\nset actor second\nsource helper.gs\nGameZWriteZBDFile out\n";
        fixture.Write("gamegen/repeated.gs", script);
        var first = Repeated(); var second = Repeated();
        Assert.Equal(2, first.Roots.Length);
        Assert.NotEqual(first.Index.Identities[first.Roots[0]], first.Index.Identities[first.Roots[1]]);
        Assert.Equal(first.Index.Identities[first.Roots[0]], second.Index.Identities[second.Roots[0]]);
        foreach (int root in first.Roots)
        {
            var made = first.Origins[root].Created;
            int hull = first.Origins.Single(p => ReferenceEquals(p.Value.Load, made)).Key;
            Assert.Equal(root, first.Index.Owners[hull]);
        }
        // A removed root must not donate its surviving imported child to the other invocation of the same line.
        int removed = first.Roots[0];
        int orphan = first.Origins.Single(p => ReferenceEquals(p.Value.Load, first.Origins[removed].Created)).Key;
        first.Origins.Remove(removed);
        var missing = SourceSelectionIndex.Build(first.Origins, Token);
        Assert.True(missing.Owners[orphan] < -1); Assert.NotNull(missing.Boundaries[missing.Owners[orphan]].ReadOnlyReason);
        // Equal macro-expanded instructions still select their own occurrences, but cannot claim stable restoration.
        fixture.Write("gamegen/repeated.gs", script.Replace("actor second", "actor first", StringComparison.Ordinal));
        var identical = Repeated(); var identicalAgain = Repeated();
        Assert.NotEqual(identical.Index.Identities[identical.Roots[0]], identical.Index.Identities[identical.Roots[1]]);
        Assert.NotEqual(identical.Index.Identities[identical.Roots[0]], identicalAgain.Index.Identities[identicalAgain.Roots[0]]);
        foreach (int root in identical.Roots)
            Assert.Equal(root, identical.Index.Owners[identical.Origins.Single(p => ReferenceEquals(p.Value.Load, identical.Origins[root].Created)).Key]);

        (SourceSelectionIndex Index, Dictionary<int, WorldNodeProvenance> Origins, int[] Roots) Repeated()
        {
            WorldAssembler assembler = new(new SelectionFiles(new SourceWorkspace(fixture.Project)), Token);
            var world = assembler.Assemble("repeated.gs");
            var nodes = world.Nodes.Select((n, i) => (n, i)).ToDictionary(p => p.i, p => p.n);
            var provenance = nodes.Where(p => assembler.Provenance.ContainsKey(p.Value)).ToDictionary(p => p.Key, p => assembler.Provenance[p.Value]);
            return (SourceSelectionIndex.Build(provenance, Token, nodes), provenance, [.. provenance.Where(p => p.Value.LoadedFile != null).Select(p => p.Key)]);
        }
        void Link(int parent, int child) { topology[parent].Children.Add(topology[child]); topology[child].Parents.Add(topology[parent]); }
    }

    private sealed class SelectionFiles(SourceWorkspace workspace) : IProjectFiles
    {
        public bool Exists(string relative) => workspace.Exists(relative);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            var bytes = workspace.Read(relative, token) ?? throw new FileNotFoundException(relative);
            limits.Validate(bytes); return bytes;
        }
    }

    [Fact]
    public async Task CopiesOfAnInstanceAreOneNodeWhicheverTheBuildReads()
    {
        using SourceWorldFixture fixture = new();
        fixture.WriteSharedDatabase();
        Script(fixture, "# no vehicles", "FindNode gate\r\nSetLandmark on");
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        // Under sgate2, sgate1's copy of the shared node comes after sgate2's, so the build reads that one instead.
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, build, world, world.Nodes.Single(n => n.Name == "sgate1")), world.Nodes.Single(n => n.Name == "sgate2"), Token));
        var (after, moved) = await BuildAsync(fixture, workspace);
        Assert.NotEqual(Origin(build, world, "gate").ModelNode, Origin(after, moved, "gate").ModelNode);
        // It is the same node, marked by the script as before: the move is no retarget.
        Assert.Equal(0x80u, moved.Nodes.Single(n => n.Name == "gate").Flags & 0x80);
        Assert.Equal("1/0", Origin(after, moved, "gate").Instance?.ToString());
        Assert.Null(SourceObjectEdits.TargetChange(build, after));
    }

    [Fact]
    public async Task AnotherInstanceFoundByNameIsAnotherNode()
    {
        using SourceWorldFixture fixture = new();
        // Two shared nodes, each holding a gate. The build makes shared nodes first, in the order the records reach them,
        // so the script marks the second one's gate, the newest.
        WorldMaterial rock = new() { Texture = new("rock"), Flags = 0x1FF };
        WorldNode Shared() { var shared = Node(""); shared.Children.Add(Node("gate", Quad(rock, 2, 1))); return shared; }
        WorldNode first = Shared(), second = Shared();
        var gates = Enumerable.Range(1, 4).Select(i => Node($"sgate{i}")).ToArray();
        for (int i = 0; i < 4; i++) { var shared = i < 2 ? first : second; gates[i].Children.Add(shared); shared.Parents.Add(gates[i]); }
        WriteDatabase(fixture, [Node("ground", Quad(rock, 64, 0)), .. gates]);
        Script(fixture, "# no vehicles", "FindNode gate\r\nSetLandmark on");
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        Assert.Equal("sgate3", Marked(world));
        // Under the ground, sgate3 reaches the second shared node first: the first one's gate is now the newest.
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, build, world, world.Nodes.Single(n => n.Name == "sgate3")), world.Nodes.Single(n => n.Name == "ground"), Token));
        var (after, moved) = await BuildAsync(fixture, workspace);
        Assert.Equal("sgate1", Marked(moved));
        Assert.Contains("gamegen/m1.gs line 14 would act on gate (data/m1/models/m1.gltf instance 1/0, loaded at gamegen/m1.gs line 11) instead of gate (data/m1/models/m1.gltf instance 2/0", SourceObjectEdits.TargetChange(build, after));
        static string Marked(GameZWorld world) => Ancestors(world.Nodes.Single(n => n.Name == "gate" && (n.Flags & 0x80) != 0)).First(a => a.Name.StartsWith("sgate", StringComparison.Ordinal)).Name;
    }

    [Fact]
    public async Task InstructionsOnNodesTheBuildFreesAreCompared()
    {
        using SourceWorldFixture fixture = new();
        // Two groups named g, which the build frees with the database's root; before that, the script marks the newer.
        WorldMaterial rock = new() { Texture = new("rock"), Flags = 0x1FF };
        WorldNode one = Node("g"), two = Node("g");
        one.Children.Add(Node("a", Quad(rock, 2, 1))); two.Children.Add(Node("b", Quad(rock, 2, 2)));
        WriteDatabase(fixture, [Node("ground", Quad(rock, 64, 0)), one, two], n => n == one || n == two);
        Script(fixture, "DeleteTree m1.flt", "FindNode g\r\nSetLandmark on\r\nDeleteTree m1.flt\r\nNewObject3D extra\r\nAddChild a\r\nFindNode world\r\nAddChild extra\r\nAddChild b");
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        Assert.DoesNotContain(world.Nodes, n => n.Name == "g");
        // The preview keeps authored groups after DeleteTree, without borrowing their freed/reused slots.
        var slots = GameZWriter.NodeSlots(world, Token);
        var selection = SourceSelectionIndex.Build(build.Provenance, Token, slots.ToDictionary(p => p.Value, p => p.Key), build.ReadHashes, build.AuthoredHierarchy);
        int worldSlot = slots[world.Nodes.Single(n => n.Class == WorldNodeClass.World)];
        var groups = selection.AuthoredChildren[worldSlot].Where(i => selection.AuthoredNodes[i].Name == "g").ToArray();
        Assert.Equal(2, groups.Length); Assert.NotEqual(selection.Identities[groups[0]], selection.Identities[groups[1]]);
        Assert.All(groups, i => { Assert.True(i < -1); Assert.Null(selection.AuthoredNodes[i].CompiledNode); Assert.True(selection.AuthoredNodes[i].Group); });
        Assert.Equal("a", selection.AuthoredNodes[Assert.Single(selection.AuthoredChildren[groups[0]])].Name);
        Assert.Equal("b", selection.AuthoredNodes[Assert.Single(selection.AuthoredChildren[groups[1]])].Name);
        int aSlot = slots[world.Nodes.Single(n => n.Name == "a")], bSlot = slots[world.Nodes.Single(n => n.Name == "b")];
        int extraSlot = slots[world.Nodes.Single(n => n.Name == "extra")];
        Assert.Contains((worldSlot, aSlot), selection.FlattenedEdges);
        Assert.DoesNotContain((worldSlot, bSlot), selection.FlattenedEdges);
        Assert.Contains(build.AuthoredHierarchy.ScriptEdges, edge => edge.Parent == extraSlot && edge.Child == aSlot);
        Assert.Contains(build.AuthoredHierarchy.ScriptEdges, edge => edge.Parent == worldSlot && edge.Child == bSlot);
        // The groups change places in the file, keeping their node numbers: the line marks the other one.
        var gltf = JsonNode.Parse(workspace.Read("data/m1/models/m1.gltf", Token)!)!;
        var roots = gltf["scenes"]![0]!["nodes"]!.AsArray();
        int a = roots[1]!.GetValue<int>(), b = roots[2]!.GetValue<int>();
        roots[1] = b; roots[2] = a;
        Assert.NotNull(workspace.Apply("Swap the groups", [("data/m1/models/m1.gltf", Encoding.UTF8.GetBytes(gltf.ToJsonString()))], Token));
        var (after, _) = await BuildAsync(fixture, workspace);
        Assert.Contains($"gamegen/m1.gs line 13 would act on g (data/m1/models/m1.gltf node {a}, loaded at gamegen/m1.gs line 11) instead of g (data/m1/models/m1.gltf node {b}", SourceObjectEdits.TargetChange(build, after));
        Assert.Null(SourceObjectEdits.TargetChange(build, build));
    }

    [Fact]
    public async Task RenamingARemovedReferenceHolderKeepsInstructionsOnItsContent()
    {
        using SourceWorldFixture fixture = new();
        fixture.WritePartDatabase();
        Script(fixture, "# no vehicles", "FindNode post\r\nSetLandmark on");
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        // The marked post's identity includes the part reference holding it, which the build removes.
        var marked = build.Provenance.Values.Single(p => p.ModelNodeName == "post" && p.Applied.Count > 0);
        var holder = marked.ReferencedBy!;
        Assert.Contains(build.AuthoredHierarchy.Nodes, n => n.SourceNode == holder.ModelNode && n.File == holder.ModelFile && n.CompiledNode == null);
        Assert.Contains("already have a node named post", Assert.Throws<InvalidDataException>(() =>
            SourceObjectEdits.PlanRenameRemoved(workspace, world, build, holder.ModelFile!, holder.ModelNode, "m1_01.flt", "post", Token)).Message);
        var plan = SourceObjectEdits.PlanRenameRemoved(workspace, world, build, holder.ModelFile!, holder.ModelNode, "m1_01.flt", "part_b", Token);
        Apply(workspace, plan);
        var (after, _) = await BuildAsync(fixture, workspace);
        Assert.Contains(after.AuthoredHierarchy.Nodes, n => n.Name == "part_b" && n.CompiledNode == null);
        // Under its new name the holder is another identity, but the rename tells the comparison it is the same node.
        Assert.NotNull(SourceObjectEdits.TargetChange(build, after));
        Assert.Null(SourceObjectEdits.TargetChange(build, after, plan.Lines));
    }

    [Fact]
    public async Task TheTakeBackMessageIsBounded()
    {
        using SourceWorldFixture fixture = new();
        fixture.WritePartDatabase(secondGround: true);
        // Five lines mark the newest ground, whose glTF node has a very long name (the world's name comes from the extras).
        Script(fixture, "# no vehicles", string.Concat(Enumerable.Repeat("FindNode ground\r\nSetLandmark on\r\n", 5)).TrimEnd());
        var gltf = JsonNode.Parse(File.ReadAllBytes(fixture.Path("data/m1/models/m1.gltf")))!;
        foreach (var node in gltf["nodes"]!.AsArray().Where(n => n!["name"]!.GetValue<string>() == "ground"))
        {
            node!["name"] = new string('x', 5000);
            var extras = node["extras"] as JsonObject ?? (JsonObject)(node["extras"] = new JsonObject());
            var recoil = extras[WorldGltf.Key] as JsonObject ?? (JsonObject)(extras[WorldGltf.Key] = new JsonObject());
            recoil["name"] = "ground";
        }
        fixture.Write("data/m1/models/m1.gltf", Encoding.UTF8.GetBytes(gltf.ToJsonString()));
        SourceWorkspace workspace = new(fixture.Project);
        var (build, world) = await BuildAsync(fixture, workspace);
        var slots = GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken);
        var grounds = world.Nodes.Where(n => n.Name == "ground").ToList();
        var flagged = grounds.Single(g => build.Provenance[slots[g]].Applied.Count > 0);
        Apply(workspace, SourceObjectEdits.PlanReparent(Target(workspace, build, world, grounds.Single(g => !ReferenceEquals(g, flagged))), flagged, Token));
        var (after, _) = await BuildAsync(fixture, workspace);
        string message = SourceObjectEdits.TargetChange(build, after)!;
        // The first three lines, then a count; names are shortened.
        Assert.Contains("gamegen/m1.gs line 14 would act on", message);
        Assert.Contains("; and 2 more instructions:", message);
        Assert.DoesNotContain("line 20 would act on", message);
        Assert.Contains(new string('x', 64) + "…", message);
        Assert.InRange(message.Length, 1, 2048);
    }

    private static async Task<(SourceSceneState Build, GameZWorld World)> BuildAsync(SourceWorldFixture fixture, SourceWorkspace workspace)
    {
        var build = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", workspace.Overlay(), token: Token);
        Assert.NotNull(build.Scene);
        var world = build.World;
        return (build.BuildState, world);
    }
    private static SourceObjectTarget Target(SourceWorkspace workspace, SourceSceneState build, GameZWorld world, WorldNode picked)
    {
        Dictionary<WorldNode, WorldNodeProvenance> provenance = new(ReferenceEqualityComparer.Instance);
        foreach (var (node, slot) in GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken)) if (build.Provenance.TryGetValue(slot, out var origin)) provenance[node] = origin;
        return new(workspace, "m1", world, SourceObjectEdits.ObjectOf(picked, provenance), provenance, build.Executions);
    }
    private static WorldNodeProvenance Origin(SourceSceneState build, GameZWorld world, string name) => build.Provenance[GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken)[world.Nodes.Single(n => n.Name == name)]];
    private static void Apply(SourceWorkspace workspace, SourceEditPlan plan) => Assert.NotNull(workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token));
    private static void Script(SourceWorldFixture fixture, string line, string replacement)
    {
        string script = Encoding.Latin1.GetString(File.ReadAllBytes(fixture.Path("gamegen/m1.gs")));
        Assert.Contains(line, script);
        fixture.Write("gamegen/m1.gs", script.Replace(line, replacement, StringComparison.Ordinal));
    }
    private static IEnumerable<WorldNode> Ancestors(WorldNode node)
    {
        for (var at = node.Parents.FirstOrDefault(); at != null; at = at.Parents.FirstOrDefault()) yield return at;
    }
    private static void WriteDatabase(SourceWorldFixture fixture, IReadOnlyList<WorldNode> roots, Func<WorldNode, bool>? group = null)
    {
        var (json, bin) = WorldGltf.Export(roots, 0xFF, new() { Texture = t => ($"../textures/{t.Name}.png", 0), Group = group ?? (_ => false) }).Write("m1.bin");
        fixture.Write("data/m1/models/m1.bin", bin); fixture.Write("data/m1/models/m1.gltf", json);
    }
    private static WorldNode Node(string name, WorldModel? model = null)
    {
        WorldNode node = new(name, WorldNodeClass.Object3D) { Model = model, Flags = WorldGltf.DefaultCarried };
        node.SetPayloadInt(0, 0x28);
        return node;
    }
    private static WorldModel Quad(WorldMaterial material, float size, float y)
    {
        ModelBuilder builder = new();
        builder.Add(new([new(0, y, 0), new(size, y, 0), new(size, y, -size), new(0, y, -size)], [new(0, 0), new(1, 0), new(1, 1), new(0, 1)], [], [], material));
        return builder.Finish();
    }
}
