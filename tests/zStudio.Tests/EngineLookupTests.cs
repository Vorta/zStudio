using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Lookups by name as ResolveNodeByName, LoadZbd, RebindEntryToNode, CreateFromNamesAtPose and the script interpreter
/// make them (docs/engine-evidence.md#name-lookups-and-node-slot-order).
/// </summary>
public sealed class EngineLookupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static AnimationEntry Entry(int index, string root, params string[] references)
    {
        var entry = new AnimationEntry(new byte[308], index, 0); entry.SetText(0, $"anim{index}"); entry.SetText(32, root); entry.SetText(68, root); entry.SetFloat(164, -1);
        entry.References[1].Add(new(new byte[40]));
        foreach (string name in references) entry.References[1].Add(Named(40, name));
        entry.Sequences.Add(new(new byte[64]) { Name = "motion" });
        return entry;
    }
    private static AnimationRecord Named(int size, string name) { var record = new AnimationRecord(new byte[size]); record.SetText(0, name, 36); return record; }
    private static GameNode Node(int index, string name, int[] parents, int[] children, string type = "object3d") =>
        new(index, name, type, type == "object3d" ? 0 : null, parents, children, new JsonObject { ["flags"] = 4 },
            new JsonObject { ["flags"] = 0, ["opacity"] = 1f, ["scale"] = JsonData.Vector(Vector3.One), ["transform"] = new JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0) });
    private static AnimationPreviewContext Context(GameScene scene, int? loaded, params AnimationEntry[] entries)
    {
        scene.Models.Add(new(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [], []));
        var world = new ZbdDocument("gamez.zbd", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        var package = new AnimationPackage { Prefix = [], Tail = [] };
        package.Entries.AddRange([Entry(0, ""), .. entries]);
        return new() { Package = package, World = world, LoadedNodeCount = loaded };
    }
    private static Vector3 At(AnimationFrame frame, int node) => frame.Nodes.Single(p => p.SourceNode == node).Transform.Translation;

    [Fact]
    public void LoadTimeLookupsSeeOnlyTheWorldFilesNodes()
    {
        // A mission clone (tank_01, made after the animations load) has a part named like the gun template outside the world:
        // LoadZbd found that template, by the world's highest slot; a later rebinding finds the newest node.
        GameScene scene = new();
        scene.Nodes.AddRange([Node(0, "world", [], [1, 4], "world"), Node(1, "tank", [0], [2]), Node(2, "body", [1], []), Node(3, "gun", [], []),
            Node(4, "tank_01", [0], [5]), Node(5, "gun", [4], [])]);
        var tank = Entry(1, "tank", "gun"); var world = Entry(2, "world", "gun");
        var context = Context(scene, 4, tank, world);
        Assert.Equal(3, context.ResolveNode(tank, 1));
        Assert.Equal(3, context.ResolveTrackedNode(tank, "gun", 1));
        // Nor do the root's subtree searches see the clone the mission added under the world.
        Assert.Equal(3, context.ResolveNode(world, 1));
        // A child started at the tank while the mission runs binds it again: the newest gun, not the loaded lookup's cache.
        Assert.Equal(5, context.ResolveInstanceNode(tank, 1, 1, AnimationBinding.Rebound));
        // The mission scene grows (another copy): a rebinding sees it, the loaded lookup does not change.
        scene.Nodes.Add(Node(6, "gun", [], []));
        Assert.Equal(6, context.ResolveInstanceNode(tank, 1, 1, AnimationBinding.Rebound));
        Assert.Equal(6, context.ResolveTrackedNode(tank, "gun", 1, AnimationBinding.Rebound));
        Assert.Equal(3, context.ResolveNode(tank, 1));
    }

    [Fact]
    public void ARootsNameIsLookedUpLikeAnyOther()
    {
        // ResolveNodeByName has no shortcut for the root's name: the attachment's subtree comes first (a dwall part under the
        // wall the entry attaches to), and a node the entry is started at answers only its own name.
        GameScene scene = new();
        scene.Nodes.AddRange([Node(0, "world", [], [1, 3, 5, 7], "world"), Node(1, "wall", [0], [2]), Node(2, "dwall", [1], []), Node(3, "dwall", [0], [4]), Node(4, "healthy", [3], []),
            Node(5, "pipe", [0], [6]), Node(6, "destroyed", [5], []), Node(7, "watexp", [0], [])]);
        var hit = Entry(1, "dwall", "dwall"); hit.SetText(68, "wall");
        var pipe = Entry(2, "pipe", "destroyed");
        var explosion = Entry(3, "watexp", "watexp");
        var context = Context(scene, null, hit, pipe, explosion);
        Assert.Equal(3, context.ResolveRoot(hit));
        Assert.Equal(2, context.ResolveNode(hit, 1));
        Assert.Equal(2, context.ResolveInstanceNode(hit, 1, 3));
        // 1999/1998 m5 start under_water_exp (root watexp.flt) at a pipe's destroyed node: its watexp.flt is the world's.
        Assert.Equal(7, context.ResolveInstanceNode(explosion, 1, 6, AnimationBinding.Rebound));
        var launch = AnimationCatalog.Create(24); launch.SetText(12, "anim3", 20); launch.SetShort(44, 1); launch.Threshold = .1f; pipe.Sequences[0].Events.Add(launch);
        var move = AnimationCatalog.Create(7); move.SetShort(28, 1); move.SetVector(16, new(42, 0, 0)); explosion.Sequences[0].Events.Add(move);
        var frame = new AnimationPlayer(context, 2).EvaluateForTest(.5);
        Assert.Equal(42, At(frame, 7).X); Assert.Equal(0, At(frame, 6).X);
        // The editor's chosen root stands for the root's name.
        context.RootOverrides[3] = 6;
        Assert.Equal(6, context.ResolveNode(explosion, 1));
    }

    [Fact]
    public void TheEntrysOwnLightsAndSoundsComeBeforeTheWholeWorld()
    {
        GameScene scene = new();
        scene.Nodes.AddRange([Node(0, "lamp", [], [1]), Node(1, "bulb", [0], []), Node(2, "snd_hum", [], []), Node(3, "glow", [], [])]);
        var lamp = Entry(1, "lamp", "snd_hum", "glow", "bulb");
        lamp.References[2].AddRange([new(new byte[44]), Named(44, "glow"), Named(44, "bulb")]);
        lamp.References[3].AddRange([new(new byte[44]), Named(44, "snd_hum")]);
        lamp.References[0].AddRange([new(new byte[96]), Named(96, "snd_hum")]);
        var context = Context(scene, null, lamp);
        // The entry's own sound and light nodes are not the world's: nothing in the scene is moved.
        Assert.Equal(-1, context.ResolveNode(lamp, 1));
        Assert.Equal(-1, context.ResolveNode(lamp, 2));
        Assert.Equal(-1, context.ResolveTrackedNode(lamp, "snd_hum", 0));
        // The root's subtree comes first.
        Assert.Equal(1, context.ResolveNode(lamp, 3));
        var show = AnimationCatalog.Create(6); show.SetShort(16, 1); show.SetInt(12, 1); lamp.Sequences[0].Events.Add(show);
        var frame = new AnimationPlayer(context, 1).EvaluateForTest(.1);
        Assert.Contains(frame.Diagnostics, d => d.Contains("own light or sound snd_hum", StringComparison.Ordinal));
    }

    [Fact]
    public void CopiesAndRebindingsLookTheAttachmentUpInsideTheNode()
    {
        // A hit wall attached to a wall outside its root (1999 m4–m13): LoadZbd finds healthy under the wall, but a copy of
        // the root (flag 0x8000) or a child started at the root itself binds it again, inside that node only (the game then
        // disables it: the attachment is not inside).
        GameScene scene = new();
        scene.Nodes.AddRange([Node(0, "world", [], [1, 3, 5], "world"), Node(1, "dwall", [0], [2]), Node(2, "healthy", [1], []), Node(3, "wall", [0], [4]), Node(4, "healthy", [3], []),
            Node(5, "trigger", [0], [])]);
        var hit = Entry(1, "dwall", "healthy"); hit.SetText(68, "wall");
        var copied = Entry(2, "dwall", "healthy"); copied.SetText(68, "wall"); copied.SetInt(148, 0x8000);
        var trigger = Entry(3, "trigger", "dwall", "wall");
        var context = Context(scene, null, hit, copied, trigger);
        Assert.Equal((AnimationBinding.Loaded, 4), (context.Binding(hit, 1), context.ResolveNode(hit, 1)));
        Assert.Equal((AnimationBinding.Copied, 2), (context.Binding(copied, 1), context.ResolveNode(copied, 1)));
        Assert.True(context.RebindDisables(copied, 1, AnimationBinding.Copied));
        var launch = AnimationCatalog.Create(24); launch.SetText(12, "anim1", 20); launch.SetShort(44, 1); launch.Threshold = .1f; trigger.Sequences[0].Events.Add(launch);
        var move = AnimationCatalog.Create(7); move.SetShort(28, 1); move.SetVector(16, new(0, 42, 0)); hit.Sequences[0].Events.Add(move);
        var frame = new AnimationPlayer(context, 3).EvaluateForTest(.5);
        Assert.Equal(42, At(frame, 2).Y); Assert.Equal(0, At(frame, 4).Y);
        Assert.Contains(frame.Diagnostics, d => d.Contains("the game disables this animation bound at dwall", StringComparison.Ordinal));
    }

    private static JsonObject Arr(params JsonNode[] nodes) => new() { ["type"] = "array", ["children"] = new JsonArray(nodes) };
    private static JsonObject Str(string value) => new() { ["type"] = "string", ["value"] = value };
    private static JsonObject Num(float value) => new() { ["type"] = "float", ["value"] = value };
    private static JsonObject Spawn(float x, float y, float z) => Arr(Num(0), Arr(Num(x), Num(y), Num(z)), Num(0));

    [Fact]
    public void AiVehiclesAreLookedUpAmongEveryLiveNode()
    {
        // CreateFromNamesAtPose looks both names up in bucket 6, whatever the class: a light named like a vehicle is what the
        // game places, and a newer light named like the template is what it copies. Neither makes a vehicle.
        GameScene scene = new();
        scene.Nodes.AddRange([Node(0, "world", [], [1], "world"), Node(1, "scou", [0], []), Node(2, "scou_01", [], [], "light"), Node(3, "rumv", [0], []), Node(4, "rumv", [], [], "light"),
            Node(5, "tren", [0], [])]);
        var world = Context(scene, null).World;
        var mission = MissionSceneLoader.Build(world, null, Arr(Str("scou_01"), Spawn(1, 2, 3), Str("rumv_01"), Spawn(4, 5, 6), Str("tren_01"), Spawn(7, 8, 9)),
            Arr(Str("scou"), Arr(), Str("rumv"), Arr(), Str("tren"), Arr()), null, token: Token);
        Assert.Equal(["tren_01"], mission.Actors.Select(a => a.Name));
        Assert.Contains(mission.Diagnostics, d => d.StartsWith("Mission actor scou_01: The game places the light node #2", StringComparison.Ordinal));
        Assert.Contains(mission.Diagnostics, d => d.StartsWith("Mission actor rumv_01: The game copies the light node rumv (#4)", StringComparison.Ordinal));
    }

    [Fact]
    public void ScriptCommandsMatchAsTheRetailInterpreterMatchesThem()
    {
        // Case-sensitive prefixes (strncmp with the command's length), Quit exactly; Quit ends its own script.
        Dictionary<string, string> scripts = new()
        {
            ["gamegen/m1_zbd.gs"] = "Source support\\skipped.gw\nsourced support\\tex_fxm1.gw\nfindnode lower\nFindNodes prefix\nquit\nFindNode afterquit\nQuit\nFindNode never\n",
            ["gamegen/support/tex_fxm1.gw"] = "FindNode ramp\nQuit\nFindNode skipped\n",
            ["gamegen/support/skipped.gw"] = "FindNode wrong\n",
        };
        var findNodes = WorldLookups.FindNodes(p => scripts.TryGetValue(p, out var text) ? Encoding.Latin1.GetBytes(text) : null, "m1", Token);
        Assert.Equal([("gamegen/support/tex_fxm1.gw", "ramp"), ("gamegen/m1_zbd.gs", "prefix"), ("gamegen/m1_zbd.gs", "afterquit")], findNodes);

        // The texture effects the preview runs: lowercase commands do nothing, and only on or true turn looping on.
        GameScene scene = new(); scene.Nodes.AddRange([Node(0, "first", [], []), Node(1, "second", [], [])]);
        var context = Context(scene, null);
        scene.Models[0] = scene.Models[0] with { Polygons = [new(0, 0, [], [], [], []), new(1, 1, [], [], [], [])] };
        scene.Models.Add(new(1, [Vector3.Zero], [], [], [new(1, 0, [], [], [], [])], []));
        scene.Nodes[1] = scene.Nodes[1] with { ModelIndex = 1 };
        context.ReadTextureScript("mission", new Dictionary<string, ScriptContent>
        {
            ["mission"] = new([["findnode", "second"], ["FindNode", "first"], ["CycleTextureSetOn", "1"], ["CycleTextureSetLooping", "1"], ["CycleTextureSetMap", "a"],
                ["FindNode", "second"], ["CycleTextureSetOn", "1"], ["CycleTextureSetLooping", "TRUE"], ["CycleTextureSetMap", "b"], ["cycletexturesetspeed", "99"]], ""),
        }, Token);
        var (first, second) = (context.MaterialCycles[0], context.MaterialCycles[1]);
        Assert.Equal(["a"], first.Textures); Assert.Equal(["b"], second.Textures);
        Assert.Equal((false, true, 15f, 15f), (first.Loop, second.Loop, first.Speed, second.Speed));
    }

    [Fact]
    public void LookupFingerprintsAreBoundedAndMadeOncePerNode()
    {
        // 300 entries bound to one node with 2,000 children: each lookup keeps a fixed-size digest, and the node's structure
        // is described once rather than once per lookup.
        GameZWorld world = new(); WorldNode top = new("world1", WorldNodeClass.World); world.Nodes.Add(top);
        WorldNode Add(WorldNode parent, string name)
        {
            WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried }; node.SetPayloadInt(0, 0x28);
            parent.Children.Add(node); node.Parents.Add(parent); world.Nodes.Add(node); return node;
        }
        var wide = Add(top, "wide");
        for (int i = 0; i < 2000; i++) Add(wide, $"piece{i}");
        var package = new AnimationPackage { Prefix = [], Tail = [] };
        package.Entries.Add(Entry(0, ""));
        for (int i = 1; i <= 300; i++) package.Entries.Add(Entry(i, "wide"));
        WorldLookups.Resolve("m1", world, package, [], Token);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var lookups = WorldLookups.Resolve("m1", world, package, [], Token);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(300, lookups.Count);
        Assert.All(lookups, l => Assert.Equal(64, l.Fingerprint!.Length));
        Assert.True(allocated < 4_000_000, $"Allocated {allocated:N0} bytes");
    }

    [Fact]
    public void ALookupBaselineReadsItsWorldOnce()
    {
        static GameZWorld Walls(params float[] xs)
        {
            GameZWorld world = new(); WorldNode root = new("world1", WorldNodeClass.World); world.Nodes.Add(root);
            foreach (float x in xs)
            {
                WorldNode node = new("wall", WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried }; node.SetPayloadInt(0, 0x20);
                float[] matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1, x, 0, 0];
                for (int i = 0; i < 12; i++) node.SetPayloadFloat(0x30 + i * 4, matrix[i]);
                root.Children.Add(node); node.Parents.Add(root); world.Nodes.Add(node);
            }
            return world;
        }
        (string, string)[] findNodes = [("gamegen/support/tex_fxm1.gw", "wall")];
        var first = Walls(10, 30); byte[] bytes = GameZWriter.Write(first, Token);
        var baseline = new SourceLookupBaseline(bytes, WorldLookups.Resolve("m1", first, null, findNodes, Token));
        // Nothing may differ: the world is not read.
        Assert.Empty(baseline.Changes(() => throw new InvalidOperationException(), WorldLookups.Resolve("m1", Walls(10, 30), null, findNodes, Token), Token));
        // Each edit that adds a newer wall is reported against the same baseline world, read once: the bytes are not read again.
        for (int i = 0; i < 2; i++)
        {
            var edited = Walls(10, 30, 20 + i);
            var change = Assert.Single(baseline.Changes(() => edited, WorldLookups.Resolve("m1", edited, null, findNodes, Token), Token));
            Assert.Equal((2, 3), (change.Before.Slot, change.After.Slot));
            Array.Clear(bytes);
        }
    }
}
