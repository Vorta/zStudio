using System.Numerics;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>Lookups by name as the engine makes them: the highest slot first, and the animation roots' chain.</summary>
public sealed class NameLookupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static AnimationEntry Entry(int index, string root, byte state = 0, params string[] references)
    {
        var entry = new AnimationEntry(new byte[308], index, 0); entry.SetText(0, $"anim{index}"); entry.SetText(32, root);
        entry.Bytes[NameLookups.StateOffset] = state;
        entry.References[1].Add(new(new byte[40]));
        foreach (string name in references) { var record = new AnimationRecord(new byte[40]); record.SetText(0, name, 36); entry.References[1].Add(record); }
        return entry;
    }

    [Fact]
    public void RootsFollowTheBindingLoopsChain()
    {
        // Entry 0 and entries in state 5 are skipped without breaking the chain; a same-named entry takes the next node,
        // a new name or running out starts again at the highest slot.
        AnimationEntry[] entries = [Entry(0, "ramp"), Entry(1, "ramp"), Entry(2, "ramp"), Entry(3, "ramp"), Entry(4, "door"), Entry(5, "ramp"), Entry(6, "ramp", NameLookups.SkippedState), Entry(7, "ramp"), Entry(8, "gone")];
        int Count(string name) => name switch { "ramp" => 2, "door" => 1, _ => 0 };
        Assert.Equal([-1, 0, 1, 0, 0, 0, -1, 1, -1], NameLookups.RootPositions(entries, Count));
    }

    [Fact]
    public void ThePreviewFindsTheHighestLiveSlot()
    {
        // Slots 0 and 1 are live ramps, slot 2 a freed slot that still holds the name, slot 3 a lava node.
        GameScene scene = new();
        scene.Nodes.Add(new(0, "ramp", "object3d", null, [], [], new(), new()));
        scene.Nodes.Add(new(1, "ramp", "object3d", null, [], [], new(), new()));
        scene.Nodes.Add(new(2, "ramp", "none", null, [], [], new(), new()));
        scene.Nodes.Add(new(3, "lava", "object3d", null, [], [], new(), new()));
        byte[] header = new byte[36]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 0x02971222); header[4] = 15;
        var world = new ZbdDocument("gamez.zbd", new(header.Length, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), header) { Scene = scene };
        var package = new AnimationPackage { Prefix = [], Tail = [] };
        package.Entries.AddRange([Entry(0, ""), Entry(1, "ramp"), Entry(2, "ramp"), Entry(3, "lava", 0, "ramp")]);
        var context = new AnimationPreviewContext { Package = package, World = world };
        Assert.Equal(1, context.ResolveRoot(package.Entries[1]));
        Assert.Equal(0, context.ResolveRoot(package.Entries[2]));
        Assert.Equal([1, 0], context.LoadedNamed("ramp"));
        // A name its root's subtree lacks falls back to the whole world, the highest live slot first.
        Assert.Equal(1, context.ResolveNode(package.Entries[3], 1));
        Assert.Equal(1, context.FindNamed("ramp"));
    }

    [Fact]
    public void ReferencesSearchTheAttachmentFirstWhereverItIs()
    {
        // A hit wall (root dwall) attached to a wall outside it, as 1999 m4–m13's hit_wall entries are: LoadZbd binds the
        // attachment like any name (the root's subtree, else the highest slot of the whole world), and each reference
        // searches the attachment's subtree before the root's, so healthy is the highest wall's, not the root's own.
        GameScene scene = new();
        scene.Nodes.Add(new(0, "dwall", "object3d", null, [], [1], new(), new()));
        scene.Nodes.Add(new(1, "healthy", "object3d", null, [0], [], new(), new()));
        scene.Nodes.Add(new(2, "wall", "object3d", null, [], [3], new(), new()));
        scene.Nodes.Add(new(3, "healthy", "object3d", null, [2], [], new(), new()));
        scene.Nodes.Add(new(4, "wall", "object3d", null, [], [5], new(), new()));
        scene.Nodes.Add(new(5, "healthy", "object3d", null, [4], [], new(), new()));
        // Another node the entry can be bound to (a copy or a chosen root), with a part of the same name.
        scene.Nodes.Add(new(6, "dwall_b", "object3d", null, [], [7], new(), new()));
        scene.Nodes.Add(new(7, "healthy", "object3d", null, [6], [], new(), new()));
        // A box with two lids: scripts' FindSubNode takes the last child first, references the first.
        scene.Nodes.Add(new(8, "box", "object3d", null, [], [9, 10], new(), new()));
        scene.Nodes.Add(new(9, "lid", "object3d", null, [8], [], new(), new()));
        scene.Nodes.Add(new(10, "lid", "object3d", null, [8], [], new(), new()));
        byte[] header = new byte[36]; System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(header, 0x02971222); header[4] = 15;
        var world = new ZbdDocument("gamez.zbd", new(header.Length, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), header) { Scene = scene };
        var package = new AnimationPackage { Prefix = [], Tail = [] };
        var hit = Entry(1, "dwall", 0, "healthy"); hit.SetText(68, "wall");
        var own = Entry(2, "dwall", 0, "healthy"); own.SetText(68, "dwall");
        package.Entries.AddRange([Entry(0, ""), hit, own]);
        var context = new AnimationPreviewContext { Package = package, World = world };
        Assert.Equal(5, context.ResolveNode(hit, 1));
        // Playback finds the same node, and cleanup restores tracked nodes by the same rule.
        Assert.Equal(5, context.ResolveInstanceNode(hit, 1, 0));
        Assert.Equal(5, context.ResolveTrackedNode(hit, "healthy", 0));
        // Attached to its own root, the root's subtree is searched.
        Assert.Equal(1, context.ResolveNode(own, 1));
        Assert.Equal(1, context.ResolveInstanceNode(own, 1, 0));
        // A copy or rebinding to another node looks the attachment up only inside that node (CloneEntryForNode,
        // RebindEntryToNode); the engine disables one that lacks it, which the preview approximates by searching the node
        // itself, so the node's own part is found, never the shared wall's.
        Assert.Equal(7, context.ResolveInstanceNode(hit, 1, 6));
        Assert.Equal(7, context.ResolveNode(hit, 1, 6));
        Assert.Equal((10, 9), (context.FindSubBelow(8, "lid"), context.FindBelow(8, "lid")));
    }

    [Fact]
    public void TheReportListsAttachmentsOutsideTheRootAndEveryEntryOfAName()
    {
        GameZWorld world = new(); WorldNode top = new("world1", WorldNodeClass.World); world.Nodes.Add(top);
        WorldNode Add(WorldNode parent, string name)
        {
            WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried }; node.SetPayloadInt(0, 0x28);
            parent.Children.Add(node); node.Parents.Add(parent); world.Nodes.Add(node); return node;
        }
        Add(Add(top, "dwall"), "healthy");
        var lower = Add(top, "wall"); Add(lower, "healthy"); Add(lower, "dbase");
        var highest = Add(top, "wall"); Add(highest, "healthy"); Add(highest, "dbase");
        Add(top, "lamp"); Add(top, "glow"); Add(top, "glow");
        // dbase is only under the walls: found below the attachment, it is no whole-world lookup. glow is the entry's own
        // light, found before the world's glows.
        var hit = Entry(1, "dwall", 0, "healthy", "dbase", "glow"); hit.SetText(68, "wall");
        AnimationRecord Named(int size, int offset, int length, string name, byte mode = 0) { var record = new AnimationRecord(new byte[size]); record.SetText(offset, name, length); if (mode != 0) record.Bytes[4] = mode; return record; }
        // Tracked nodes resolve like references: dbase below the attachment, lamp only in the whole world.
        hit.References[0].AddRange([new(new byte[96]), Named(96, 0, 36, "dbase"), Named(96, 0, 36, "lamp")]);
        hit.References[2].AddRange([new(new byte[44]), Named(44, 0, 36, "glow")]);
        // A prerequisite path: its first node is a whole-world lookup, the next one is searched inside it.
        hit.References[6].AddRange([Named(48, 12, 28, "wall", 3), Named(48, 12, 28, "healthy", 2)]);
        // Two entries share a name and a root: the binding loop gives them the two walls, and both lookups are reported.
        var first = Entry(2, "wall"); first.SetText(0, "hit"); var second = Entry(3, "wall"); second.SetText(0, "hit");
        var package = new AnimationPackage { Prefix = [], Tail = [] };
        package.Entries.AddRange([Entry(0, ""), hit, first, second]);
        var lookups = WorldLookups.Resolve("m1", world, package, [], Token);
        var slots = GameZWriter.NodeSlots(world, TestContext.Current.CancellationToken);
        var attachment = Assert.Single(lookups, l => l.Kind == SourceLookup.AnimationAttachment);
        Assert.Equal(("wall", "anim1", 2, slots[highest]), (attachment.Name, attachment.Source, attachment.Candidates, attachment.Slot));
        Assert.Equal("the attachment wall of animation anim1", WorldLookups.Describe(attachment));
        var name = Assert.Single(lookups, l => l.Kind == SourceLookup.AnimationName);
        Assert.Equal(("lamp", 1), (name.Name, name.Candidates));
        var prerequisite = Assert.Single(lookups, l => l.Kind == SourceLookup.AnimationPrerequisite);
        Assert.Equal(("wall", 2, slots[highest]), (prerequisite.Name, prerequisite.Candidates, prerequisite.Slot));
        Assert.Equal("the prerequisite node wall of animation anim1", WorldLookups.Describe(prerequisite));
        var roots = lookups.Where(l => l.Kind == SourceLookup.AnimationRoot && l.Source == "hit").ToList();
        Assert.Equal(2, roots.Count);
        Assert.NotEqual(roots[0].Slot, roots[1].Slot);
    }

    [Fact]
    public void CopiesOfANamePairWithThemselvesWhenOneMovesOrIsAdded()
    {
        // Walls in slot order at the given x positions; FindNode wall finds the highest slot.
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
        IReadOnlyList<SourceLookupChange> Changes(GameZWorld before, GameZWorld after) =>
            WorldLookups.Changes(before, WorldLookups.Resolve("m1", before, null, findNodes, Token), after, WorldLookups.Resolve("m1", after, null, findNodes, Token), Token);
        // Moving the highest copy past another: the game still finds that copy, so nothing changed.
        Assert.Empty(Changes(Walls(20, 30, 10), Walls(20, 30, 25)));
        // A new copy takes the highest slot: the game now finds it.
        var change = Assert.Single(Changes(Walls(10, 30), Walls(10, 30, 20)));
        Assert.Equal((2, 3), (change.Before.Slot, change.After.Slot));
        // The comparison pairs the unchanged copies with themselves and shows the new one as rebuilt only.
        var tree = WorldComparer.CompareTree(Walls(10, 30), Walls(10, 30, 20), token: Token);
        var walls = tree.Roots.Single().Children;
        Assert.Equal(2, walls.Count(c => c.Expected != null && c.Actual != null && c.Differences.Count == 0));
        Assert.Single(walls, c => c.Expected == null);
    }

    [Fact]
    public async Task ExportsListSharedNamesAndWhatTheReplacedFilesFound()
    {
        using SourceWorldFixture fixture = new();
        fixture.WritePartDatabase();
        fixture.Write("gamegen/m1_zbd.gs", "source support\\tex_fxm1.gw\r\nQuit\r\n");
        fixture.Write("gamegen/support/tex_fxm1.gw", "FindNode crate\r\nFindNode ground\r\nQuit\r\n");
        // The part is copied twice, so FindNode crate has two candidates; the report lists it and leaves ground out.
        var check = await SourceBuilder.CheckAsync(fixture.Project, ["m1/gamez.zbd", "m1/anim.zbd"], token: Token);
        var crate = Assert.Single(check.Lookups, l => l.Name == "crate");
        Assert.Equal((SourceLookup.TextureEffect, "gamegen/support/tex_fxm1.gw", 2), (crate.Kind, crate.Source, crate.Candidates));
        Assert.DoesNotContain(check.Lookups, l => l.Name == "ground");
        Assert.Empty(check.LookupChanges);

        string destination = Path.Combine(fixture.Root, "game");
        await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/gamez.zbd", "m1/anim.zbd"], token: Token);
        // A second ground, the database's last record, takes a higher slot: FindNode ground, and the animation bound to ground,
        // now find it, a node of the same path but another shape. The export says so against the files it replaces.
        fixture.WritePartDatabase(secondGround: true);
        var again = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/gamez.zbd", "m1/anim.zbd"], overwrite: true, token: Token);
        var change = Assert.Single(again.LookupChanges, c => c.After.Kind == SourceLookup.TextureEffect && c.After.Name == "ground");
        Assert.Equal(("world/ground", "world/ground", 2), (change.Before.Found, change.After.Found, change.After.Candidates));
        Assert.True(change.After.Slot > change.Before.Slot);
        Assert.Contains(again.LookupChanges, c => c.After.Kind == SourceLookup.AnimationRoot && c.After.Name == "ground");
        Assert.Contains(again.Notes, n => n.StartsWith("m1: FindNode ground in gamegen/support/tex_fxm1.gw finds world/ground", StringComparison.Ordinal) && n.EndsWith("in the files this export replaced.", StringComparison.Ordinal));
        // Identical copies are the same node to the game: the crates never count as a change.
        Assert.DoesNotContain(again.LookupChanges, c => c.After.Name == "crate");

        // Exporting the world alone, the game binds it with the animations the destination keeps, so their lookups count too.
        fixture.WritePartDatabase();
        await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/gamez.zbd", "m1/anim.zbd"], overwrite: true, token: Token);
        fixture.WritePartDatabase(secondGround: true);
        var worldOnly = await SourceBuilder.ExportAsync(fixture.Project, destination, ["m1/gamez.zbd"], overwrite: true, token: Token);
        Assert.Contains(worldOnly.LookupChanges, c => c.After.Kind == SourceLookup.AnimationRoot && c.After.Name == "ground");
    }

    /// <summary>A world with two ramps (the second placed elsewhere, or not) and a door, in slot order <paramref name="order"/>.</summary>
    private static GameZWorld World(bool distinct, params string[] order)
    {
        GameZWorld world = new(); WorldNode root = new("world1", WorldNodeClass.World);
        Dictionary<string, WorldNode> members = new()
        {
            ["a"] = Node("ramp", new(1, 0, 0)), ["b"] = Node("ramp", distinct ? new(2, 0, 0) : new(1, 0, 0)), ["door"] = Node("door", new(3, 0, 0)),
        };
        world.Nodes.Add(root);
        foreach (string key in order) { var node = members[key]; root.Children.Add(node); node.Parents.Add(root); world.Nodes.Add(node); }
        return world;
        static WorldNode Node(string name, Vector3 at)
        {
            WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried };
            node.SetPayloadInt(0, 0x20);
            float[] matrix = [1, 0, 0, 0, 1, 0, 0, 0, 1, at.X, at.Y, at.Z];
            for (int i = 0; i < 12; i++) node.SetPayloadFloat(0x30 + i * 4, matrix[i]);
            return node;
        }
    }

    [Fact]
    public void TheMissionsLookupsAreResolvedAndChangesFound()
    {
        Dictionary<string, string> scripts = new()
        {
            ["gamegen/m1_zbd.gs"] = "source support\\commonm1.gw\nGameZReadZBDFile %MissionZBDFile%\nsource support\\tex_fxm1.gw\nQuit\n",
            ["gamegen/support/tex_fxm1.gw"] = "source support\\tex_fx.gw\nFindNode %worldName%\nFindNode ramp\nObject3DSetScroll on 0.0 1.5\nQuit\n",
            ["gamegen/support/tex_fx.gw"] = "FindNode door\nQuit\n",
        };
        var findNodes = WorldLookups.FindNodes(p => scripts.TryGetValue(p, out var text) ? Encoding.Latin1.GetBytes(text) : null, "m1", Token);
        Assert.Equal([("gamegen/support/tex_fx.gw", "door"), ("gamegen/support/tex_fxm1.gw", "ramp")], findNodes);
        var package = new AnimationPackage { Prefix = [], Tail = [] };
        package.Entries.AddRange([Entry(0, ""), Entry(1, "door", 0, "ramp")]);

        // The second ramp (b) has the highest slot, so FindNode and the animation's whole-world fallback find it.
        var before = World(true, "a", "b", "door");
        var a = WorldLookups.Resolve("m1", before, package, findNodes, Token);
        var ramp = Assert.Single(a, l => l.Kind == SourceLookup.TextureEffect && l.Name == "ramp");
        Assert.Equal((2, 2, "world1/ramp"), (ramp.Candidates, ramp.Slot, ramp.Found));
        Assert.True(ramp.Ambiguous);
        Assert.Contains(a, l => l.Kind == SourceLookup.AnimationRoot && l.Name == "door" && !l.Ambiguous);
        Assert.Contains(a, l => l.Kind == SourceLookup.AnimationName && l.Name == "ramp" && l.Slot == 2);

        // Made in the other order, the first ramp has the highest slot: both lookups of ramp find another node.
        var after = World(true, "b", "a", "door");
        var b = WorldLookups.Resolve("m1", after, package, findNodes, Token);
        Assert.True(WorldLookups.Differ(a, b));
        var changes = WorldLookups.Changes(before, a, after, b, Token);
        Assert.Equal([SourceLookup.AnimationName, SourceLookup.TextureEffect], changes.Select(c => c.After.Kind).Order());
        Assert.All(changes, c => Assert.Equal("ramp", c.After.Name));
        Assert.Contains("FindNode ramp in gamegen/support/tex_fxm1.gw finds world1/ramp (slot 2) instead of world1/ramp (slot 2)", WorldLookups.Describe(changes.First(c => c.After.Kind == SourceLookup.TextureEffect)));
        // The same order finds the same nodes; identical copies count as the same node.
        Assert.False(WorldLookups.Differ(a, WorldLookups.Resolve("m1", World(true, "a", "b", "door"), package, findNodes, Token)));
        var copies = World(false, "a", "b", "door"); var swapped = World(false, "b", "a", "door");
        Assert.Empty(WorldLookups.Changes(copies, WorldLookups.Resolve("m1", copies, package, findNodes, Token), swapped, WorldLookups.Resolve("m1", swapped, package, findNodes, Token), Token));
    }
}
