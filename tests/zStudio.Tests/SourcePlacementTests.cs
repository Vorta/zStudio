using System.IO;
using System.Numerics;
using System.Text;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

/// <summary>
/// Source placement edits change only the owning coordinate tokens, including linked difficulty variants,
/// and source scene evaluation retains the accepted placement without producing an archive.
/// </summary>
public sealed class SourcePlacementTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Default and Easy share one placement (matched by type, position and rotation); Hard falls back to the default file.
    private const string Default = """
        # Mission 1 pickups: the default list.
        (
          ( HEMORTAR_AMMO 1 ( 12 8 -5 ) ( 0.0 0.0 0.0 ) 12.5 )   # integer coordinates, as hand-written sources may have
          ( NANITE 50 ( 100.0 0.0 -100.0 ) ( 0.0 1.5707964 0.0 ) 30.0 )
        )
        """;
    private const string Easy = """
        (
          # Easy keeps the nanite first and gives more ammunition.
          ( NANITE 80 ( 100.0 0.0 -100.0 ) ( 0.0 1.5707964 0.0 ) 30.0 )
          ( HEMORTAR_AMMO 5 ( 12 8 -5 ) ( 0.0 0.0 0.0 ) 12.5 )
        )
        """;

    private static async Task<(SourceWorkspace Workspace, SourceSceneSnapshot Scene, PickupPlacementEditSession Session)> BuildAsync(SourceWorldFixture fixture, SourceWorkspace? workspace = null)
    {
        workspace ??= new(fixture.Project);
        var archive = await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", workspace.Overlay(), token: Token);
        var session = PickupPlacementEditSession.FromSources(archive, Token);
        Assert.Empty(session.Diagnostics);
        return (workspace, archive, session);
    }

    [Fact]
    public async Task AMoveChangesOnlyTheCoordinateTokensOfEveryLinkedSource()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/zrdr/puppies.zrd", Default); fixture.Write("data/m1/zrdr/puppies_easy.zrd", Easy);
        // Obtain the edit identity from the shown mission, as GUI/MCP do. Source paths contain directory
        // separators from both the Windows root and project-relative source path and must canonicalize alike.
        WorldNode pickup = new("pu001", WorldNodeClass.Object3D), collision = new("bvol", WorldNodeClass.Object3D);
        pickup.SetPayloadInt(0, 0x28); collision.SetPayloadInt(0, 0x28); pickup.Children.Add(collision);
        var (json, bin) = WorldGltf.Export([pickup], 0xFF, new() { Texture = texture => (texture.Name + ".png", 0), Token = Token }).Write("pickup.bin", Token);
        fixture.Write("data/m1/models/pickup.gltf", json); fixture.Write("data/m1/models/pickup.bin", bin);
        string script = File.ReadAllText(fixture.Path("gamegen/m1.gs"));
        fixture.Write("gamegen/m1.gs", script.Replace("# no vehicles", "LoadGameGen pickup.gltf pickup_template"));
        var (workspace, archive, session) = await BuildAsync(fixture);
        var ammo = Assert.Single(archive.MissionScene(MissionDifficulty.Medium, Token).Actors, actor => actor.Pickup?.TypeIndex == 1).Pickup!.Source;
        Assert.NotNull(session.Find(ammo)); Assert.NotNull(session.SourcePresentationKey(ammo));
        // The default record links to Easy's second record; Hard uses the default file itself, so it is one source.
        var scope = session.Scope(ammo);
        Assert.Equal(2, scope.Sources.Count);
        Assert.Contains("Easy", scope.Description); Assert.Contains("Hard", scope.Description);

        var after = session.PreviewTransform(ammo, session.Transform(ammo) with { Position = new(20.25f, 8, -5) });
        var writes = session.ScalarWrites(after);
        // Only X differs from the stored values, once per linked record.
        Assert.Equal(2, writes.Count); Assert.All(writes, w => Assert.Equal(20.25f, w.Value));
        var changes = session.SourceChanges(after, workspace, Token);
        Assert.Equal(["data/m1/zrdr/puppies.zrd", "data/m1/zrdr/puppies_easy.zrd"], changes.Select(c => c.Relative).Order(StringComparer.Ordinal));
        // Each file differs from its source only where the edited token was: comments, spacing and other values stay.
        string defaultAfter = Encoding.Latin1.GetString(changes.Single(c => c.Relative.EndsWith("puppies.zrd")).Content);
        string easyAfter = Encoding.Latin1.GetString(changes.Single(c => c.Relative.EndsWith("puppies_easy.zrd")).Content);
        Assert.Equal(Default.Replace("( 12 8 -5 )", "( 20.25 8 -5 )"), defaultAfter);
        Assert.Equal(Easy.Replace("( 12 8 -5 )", "( 20.25 8 -5 )"), easyAfter);

        // Accepted workspace source evaluation holds the moved placement in both lists.
        workspace.Apply("Move HEMORTAR_AMMO", changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        var (_, _, rebuilt) = await BuildAsync(fixture, workspace);
        Assert.All(rebuilt.Records.Where(r => r.Type == "HEMORTAR_AMMO"), r => Assert.Equal(new Vector3(20.25f, 8, -5), r.OriginalPosition));
        Assert.Equal(new Vector3(100, 0, -100), rebuilt.Records.First(r => r.Type == "NANITE").OriginalPosition);
        // Undo restores the exact source bytes.
        workspace.Undo();
        Assert.Equal(Default, Encoding.Latin1.GetString(workspace.Read("data/m1/zrdr/puppies.zrd", Token)!));
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public async Task ARotationMoveWritesTheRotationTokens()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/zrdr/puppies.zrd", Default); fixture.Write("data/m1/zrdr/puppies_easy.zrd", Easy);
        var (workspace, _, session) = await BuildAsync(fixture);
        var nanite = session.Records.Single(r => r.Type == "NANITE" && r.Source.ResourceName == "PUPPIES.ZRD").Source;
        var after = session.PreviewTransform(nanite, new(new(100, 0, -100), new(0, 3.1415927f, 0)));
        var writes = session.ScalarWrites(after);
        var changes = session.SourceChanges(after, workspace, Token);
        Assert.Equal(Default.Replace("( 0.0 1.5707964 0.0 )", "( 0.0 3.1415927 0.0 )"), Encoding.Latin1.GetString(changes.Single(c => c.Relative.EndsWith("puppies.zrd")).Content));
    }

    [Fact]
    public async Task ResourceMembershipInvalidatesFallbacksIncludingPendingAndCommonSources()
    {
        using SourceWorldFixture fixture = new();
        SourceWorkspace workspace = new(fixture.Project);
        // No resource exists yet: a later mission or common file must still invalidate this scene.
        var empty = (await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", workspace.Overlay(), token: Token)).BuildState;
        Assert.NotNull(empty.ResourceInputs); Assert.Empty(empty.ResourceInputs);
        Assert.False(empty.ResourceInputsChanged(workspace, Token));
        const string normal = "data/m1/zrdr/puppies.zrd", easy = "data/m1/zrdr/puppies_easy.zrd";
        fixture.Write(normal, Default);
        Assert.True(empty.ResourceInputsChanged(workspace, Token));
        var built = (await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", workspace.Overlay(), token: Token)).BuildState;
        Assert.False(built.ResourceInputsChanged(workspace, Token));
        workspace.Apply("Add Easy", [(easy, Encoding.Latin1.GetBytes(Easy))], Token);
        Assert.True(built.ResourceInputsChanged(workspace, Token));
        workspace.Undo(); Assert.False(built.ResourceInputsChanged(workspace, Token));
        workspace.Redo();
        var pending = (await SourceWorlds.LoadSceneAsync(fixture.Project, "m1", workspace.Overlay(), token: Token)).BuildState;
        Assert.False(pending.ResourceInputsChanged(workspace, Token));
        workspace.Save(Token); Assert.False(pending.ResourceInputsChanged(workspace, Token));
        File.Delete(fixture.Path(easy)); Assert.True(pending.ResourceInputsChanged(workspace, Token));
        fixture.Write(easy, Easy); Assert.False(pending.ResourceInputsChanged(workspace, Token));
        fixture.Write("data/common/multi_bft/zrdr/puppies.zrd", Default);
        Assert.True(pending.ResourceInputsChanged(workspace, Token));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => pending.ResourceInputsChanged(workspace, canceled.Token));
    }

    [Fact]
    public async Task AiPathBatchChangesOnlyCoordinateTokensAndSurvivesSourceSaveRebuild()
    {
        // Must-have source parity: the same batch preview writes source tokens, one undo and a verified rebuild.
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/zrdr/puppies.zrd", Default); fixture.Write("data/m1/zrdr/puppies_easy.zrd", Easy);
        const string relative = "data/m1/zrdr/net_01.zrd";
        const string text = "( version ( 105 ) # Keep this network comment.\n node_00 ( 12 ( 1.0 2.0 3.0 ) ( 1 -1 -7 ) )\n node_01 ( 23 ( 3.0 2.0 3.0 ) ( 0 -1 -1 ) ) )";
        fixture.Write(relative, text);
        var (workspace, archive, session) = await BuildAsync(fixture);
        var selected = session.OtherCoordinates.ToDictionary(c => c.Source, c => c.OriginalPosition);
        Assert.Equal(2, selected.Count);
        var positions = AiPathTransforms.Scale(selected, new(2, 1, 1));
        var after = session.PreviewAiPositions(positions);
        var changes = session.SourceChanges(after, workspace, Token);
        var change = Assert.Single(changes);
        Assert.Equal(relative, change.Relative);
        string expected = text.Replace("( 1.0 2.0 3.0 )", "( 0.0 2.0 3.0 )").Replace("( 3.0 2.0 3.0 )", "( 4.0 2.0 3.0 )");
        Assert.Equal(expected, Encoding.Latin1.GetString(change.Content));
        workspace.Apply("Scale AI path", changes.Select(c => (c.Relative, (byte[]?)c.Content)), Token);
        workspace.Undo(); Assert.Equal(text, Encoding.Latin1.GetString(workspace.Read(relative, Token)!));
        workspace.Redo(); workspace.Save(Token);
        var (_, rebuilt, _) = await BuildAsync(fixture);
        var network = Assert.Single(rebuilt.MissionScene(MissionDifficulty.Medium, Token).AiNetworks.Networks);
        Assert.Equal([new Vector3(0, 2, 3), new Vector3(4, 2, 3)], network.Nodes.Select(n => n.Position));
        Assert.Equal([12, 23], network.Nodes.Select(n => n.RawValue));
        Assert.Equal([1, -1, -7], network.Nodes[0].Links.Select(l => l.TargetIndex));
        Assert.Equal(expected, File.ReadAllText(fixture.Path(relative)));
    }

    [Fact]
    public async Task ASourceChangedSinceTheBuildIsRefused()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/zrdr/puppies.zrd", Default); fixture.Write("data/m1/zrdr/puppies_easy.zrd", Easy);
        var (workspace, _, session) = await BuildAsync(fixture);
        var ammo = session.Records.Single(r => r.Type == "HEMORTAR_AMMO" && r.Source.ResourceName == "PUPPIES.ZRD").Source;
        var after = session.PreviewTransform(ammo, session.Transform(ammo) with { Position = new(1, 2, 3) });
        // The default list gains a record after evaluation: its syntax belongs to a different revision.
        workspace.Apply("Add record", [("data/m1/zrdr/puppies.zrd", Encoding.Latin1.GetBytes(Default.Replace("\n)", "\n  ( NANITE 1 ( 0.0 0.0 0.0 ) ( 0.0 0.0 0.0 ) 1.0 )\n)")))], Token);
        var refused = Assert.Throws<SourceFileChangedException>(() => session.SourceChanges(after, workspace, Token));
        Assert.Contains("reload", refused.Message);
    }

    [Fact]
    public void CompiledSourcesChangeOnlyTheEditedScalarBytes()
    {
        var tree = ZrdText.Parse(Default, Token);
        byte[] compiled = ZrdWriter.Write(tree, Token);
        var payload = ZrdDecoder.Read(compiled, Token);
        // The second coordinate of the first record.
        var y = payload.Children[0].Children[0].Children[2].Children[1];
        var document = new SourceResourceDocument(Path.GetTempPath(), new("puppies.zrd", compiled, payload, null), Token);
        byte[] result = document.Replace(new Dictionary<int, float> { [checked((int)y.SourceOffset)] = 9.5f }, Token);
        var changed = Enumerable.Range(0, compiled.Length).Where(i => compiled[i] != result[i]).ToArray();
        Assert.All(changed, i => Assert.InRange(i, y.SourceOffset, y.SourceOffset + 7));
        var reread = ZrdDecoder.Read(result, Token).Children[0].Children[0].Children[2].Children[1];
        Assert.Equal(ZrdKind.Float, reread.Kind); Assert.Equal(9.5f, BitConverter.UInt32BitsToSingle(reread.Bits));
        // Offsets that are not numeric scalars are refused.
        Assert.Throws<InvalidDataException>(() => document.Replace(new Dictionary<int, float> { [checked((int)payload.Children[0].SourceOffset)] = 1 }, Token));
    }
}
