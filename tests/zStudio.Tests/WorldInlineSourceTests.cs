using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldInlineSourceTests
{
    [Fact]
    public void ReparentCannotSplitASharedInstanceAcrossInlineNamespaces()
    {
        var token = TestContext.Current.CancellationToken;
        using SourceWorldFixture fixture = new();
        WorldNode Node(string name)
        {
            WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried, Zone = 255 };
            node.SetPayloadInt(0, 0x28); return node;
        }
        var section = Node("section"); var a = Node("a"); var b = Node("b");
        var shared = Node("shared"); var outside = Node("outside");
        a.Children.Add(shared); b.Children.Add(shared); section.Children.AddRange([a, b]);
        var document = WorldGltf.Export([section, outside], 255, new()
        {
            Texture = _ => throw new InvalidOperationException(), InlineSource = n => n == section ? "part:1" : null,
            Content = n => n == section ? n.Children : null, Group = n => n == section, Token = token,
        });
        var (json, bin) = document.Write("m1.bin", token);
        fixture.Write("data/m1/models/m1.gltf", json); fixture.Write("data/m1/models/m1.bin", bin);
        var root = JsonNode.Parse(json)!.AsObject();
        int Index(string name) => root["nodes"]!.AsArray().Select((node, index) => (node, index)).Single(x => (string?)x.node?["name"] == name).index;
        string before = root.ToJsonString();
        Assert.Contains("split a shared instance", Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Reparent(root, Index("a"), Index("outside"), token: token)).Message);
        Assert.Equal(before, root.ToJsonString());
        // Moving inside the same scope, then taking the complete cohort out, preserves one shared object.
        GltfNodeEdits.Reparent(root, Index("b"), Index("a"), token: token);
        GltfNodeEdits.Reparent(root, Index("a"), Index("outside"), token: token);
        IReadOnlyList<WorldNode> Import(JsonObject edited) => WorldGltf.Import(
            GltfDocument.Read(System.Text.Encoding.UTF8.GetBytes(edited.ToJsonString()), _ => bin, token), "data/m1/models/m1.gltf", 255, new()
            {
                World = new(), Reference = (_, _) => throw new InvalidOperationException(),
                TextureName = (_, _, _) => throw new InvalidOperationException(), Token = token,
            });
        var together = Import(root).Single(n => n.Name == "outside").Children.Single();
        Assert.Same(together.Children[0], together.Children[1].Children.Single());
        var nested = JsonNode.Parse(json)!.AsObject();
        GltfNodeEdits.Reparent(nested, Index("section"), Index("outside"), token: token); // Its inline prefix keeps its scope.
        var nestedSection = Import(nested).Single(n => n.Name == "outside").Children.Single();
        Assert.Same(nestedSection.Children[0].Children.Single(), nestedSection.Children[1].Children.Single());

        SourceWorkspace workspace = new(fixture.Project);
        WorldAssembler Build()
        {
            var assembler = new WorldAssembler(new WorkspaceFiles(workspace), token);
            assembler.Assemble("m1.gs"); Assert.NotEmpty(GameZWriter.Write(assembler.World, token)); return assembler;
        }
        SourceObjectTarget Target(WorldAssembler built, string name) => new(workspace, "m1", built.World,
            built.World.Nodes.Single(n => n.Name == name), built.Provenance, built.Executions);
        var original = Build(); Assert.Single(original.World.Nodes, n => n.Name == "shared");
        long revision = workspace.Revision;
        Assert.Contains("split a shared instance", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(
            Target(original, "a"), original.World.Nodes.Single(n => n.Name == "outside"), token)).Message);
        Assert.Equal(revision, workspace.Revision); Assert.False(workspace.CanUndo); Assert.False(workspace.IsDirty);
        Assert.Equal(json, workspace.Read("data/m1/models/m1.gltf", token));
        // Refused planning leaves the workspace usable: remove one holder, keeping its shared child through save.
        var remove = SourceObjectEdits.PlanDelete(Target(original, "a"), token);
        workspace.Apply(remove.Label, remove.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        Assert.Single(Build().World.Nodes, n => n.Name == "shared");
        workspace.Undo(); Assert.Single(Build().World.Nodes, n => n.Name == "shared");
        workspace.Redo(); workspace.Save(token); workspace = new(fixture.Project);
        Assert.Single(Build().World.Nodes, n => n.Name == "shared");
    }

    private sealed class WorkspaceFiles(SourceWorkspace workspace) : IProjectFiles
    {
        public bool Exists(string relative) => workspace.Read(relative) != null;
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits)
        {
            byte[] bytes = workspace.Read(relative, token) ?? throw new FileNotFoundException(relative);
            limits.Validate(bytes); return bytes;
        }
    }

    [Fact]
    public void EditingAndDeletingALaterInlineCopySurvivesRebuildUndoAndSave()
    {
        var token = TestContext.Current.CancellationToken;
        using SourceWorldFixture fixture = new();
        WorldNode Node(string name, WorldModel? model = null)
        {
            WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried, Zone = 255, Model = model };
            node.SetPayloadInt(0, 0x28); return node;
        }
        ModelBuilder builder = new();
        builder.Add(new([new(0, 0, 0), new(4, 0, 0), new(0, 0, -4)], [], [], [], new WorldMaterial()));
        var model = builder.Finish(); var first = Node("group1"); var later = Node("group2");
        first.Children.Add(Node("item", model)); later.Children.Add(Node("item", model));
        var document = WorldGltf.Export([first, later], 255, new()
        {
            Texture = _ => throw new InvalidOperationException(), InlineSource = n => n == first || n == later ? "part:1" : null,
            Content = n => n == first || n == later ? n.Children : null, Group = n => n == first || n == later, Token = token,
        });
        var (json, bin) = document.Write("m1.bin", token);
        fixture.Write("data/m1/models/m1.gltf", json); fixture.Write("data/m1/models/m1.bin", bin);
        SourceWorkspace workspace = new(fixture.Project);
        (WorldAssembler Assembler, WorldNode[] Items) Build()
        {
            var assembler = new WorldAssembler(new WorkspaceFiles(workspace), token);
            var world = assembler.Assemble("m1.gs");
            var bytes = GameZWriter.Write(world, token);
            Assert.NotEmpty(bytes); // Allocation and publication must admit the edited hierarchy as an actual world.
            return (assembler, world.Nodes.Where(n => n.Name == "item").OrderBy(n => assembler.Provenance[n].ModelNode).ToArray());
        }
        void Apply(SourceEditPlan plan) => workspace.Apply(plan.Label, plan.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), token);
        var original = Build(); Assert.Equal(2, original.Items.Length); Assert.Same(original.Items[0].Model, original.Items[1].Model);
        var moved = new ObjectTransform(new(7, 2, -3), System.Numerics.Vector3.Zero, System.Numerics.Vector3.One);
        Apply(SourceObjectEdits.PlanTransform(workspace, "item", original.Assembler.Provenance[original.Items[1]], original.Assembler.Executions, moved, token));
        var afterMove = Build();
        Assert.Null(WorldUpdate.LocalMatrix(afterMove.Items[0]));
        Assert.Equal(moved.Position, WorldUpdate.LocalMatrix(afterMove.Items[1])!.Value.Translation);
        Apply(SourceObjectEdits.PlanDelete(new(workspace, "m1", afterMove.Assembler.World, afterMove.Items[1], afterMove.Assembler.Provenance, afterMove.Assembler.Executions), token));
        Assert.Single(Build().Items);
        workspace.Undo(); Assert.Equal(moved.Position, WorldUpdate.LocalMatrix(Build().Items[1])!.Value.Translation);
        workspace.Save(token);
        workspace = new(fixture.Project);
        var reopened = Build(); Assert.Equal(2, reopened.Items.Length);
        Assert.Equal(moved.Position, WorldUpdate.LocalMatrix(reopened.Items[1])!.Value.Translation);
        Assert.Null(WorldUpdate.LocalMatrix(reopened.Items[0]));
    }

    [Fact]
    public void UnknownFileBoundariesStayInlineWithNestedCachesOwnRecordsAndIndependentInstances()
    {
        var token = TestContext.Current.CancellationToken;
        WorldNode Node(string name)
        {
            WorldNode node = new(name, WorldNodeClass.Object3D) { Flags = WorldGltf.DefaultCarried, Zone = 255 };
            node.SetPayloadInt(0, 0x28); return node;
        }
        var first = Node("group1"); var second = Node("group2"); var inner = Node("group3");
        var shared = Node("shared"); var outerRecord = Node("after"); var innerRecord = Node("inside");
        var a = Node("a"); var b = Node("b"); a.Children.Add(shared); b.Children.Add(shared);
        inner.Children.Add(innerRecord);
        first.Children.AddRange([a, b, inner, outerRecord]); second.Children.AddRange([a, b, inner]);
        Dictionary<WorldNode, IReadOnlyCollection<WorldNode>> content = new(ReferenceEqualityComparer.Instance)
        { [first] = new[] { a, b, inner }, [second] = new[] { a, b, inner }, [inner] = new[] { innerRecord } };
        Dictionary<WorldNode, string> identities = new(ReferenceEqualityComparer.Instance)
        { [first] = "part:1", [second] = "part:1", [inner] = "part:2" };
        var source = WorldGltf.Export([first, second], 255, new()
        {
            Texture = _ => throw new InvalidOperationException(),
            Content = n => content.GetValueOrDefault(n), InlineSource = n => identities.GetValueOrDefault(n),
            Group = content.ContainsKey, Token = token,
        });
        var (json, bin) = source.Write("m1.bin", token);
        var document = GltfDocument.Read(json, _ => bin, token);
        Assert.DoesNotContain(document.AllNodes(), n => n.Extras?[WorldGltf.Key]?["ref"] != null);
        GameZWorld world = new();
        WorldGltf.ImportContext context = new()
        {
            World = world, Reference = (_, _) => throw new InvalidOperationException("Inline sections must not load invented files."),
            TextureName = (_, _, _) => throw new InvalidOperationException(), Token = token,
        };
        var imported = WorldGltf.Import(document, "data/m1/models/m1.gltf", 255, context);
        var firstCopy = imported[0]; var secondCopy = imported[1];
        Assert.Equal(["a", "b", "group3", "after"], firstCopy.Children.Select(n => n.Name));
        Assert.Equal(["a", "b", "group3"], secondCopy.Children.Select(n => n.Name));
        Assert.Same(firstCopy.Children[0].Children[0], firstCopy.Children[1].Children[0]);
        Assert.Same(secondCopy.Children[0].Children[0], secondCopy.Children[1].Children[0]);
        Assert.NotSame(firstCopy.Children[0].Children[0], secondCopy.Children[0].Children[0]);
        Assert.Equal(context.InlineSourceKeys[firstCopy], context.InlineSourceKeys[secondCopy]);
        Assert.Equal(3, context.InlineSourceContents[firstCopy].Count);
        Assert.Single(context.InlineSourceContents[firstCopy.Children[2]]);
        Assert.NotEqual(context.InlineSourceKeys[firstCopy], context.InlineSourceKeys[firstCopy.Children[2]]);
    }
}
