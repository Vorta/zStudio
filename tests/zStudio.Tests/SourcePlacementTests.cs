using System.IO;
using System.Numerics;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Placement edits in a source world: a move made on the built archive changes only the coordinate tokens of the text
/// resources it was compiled from, including linked difficulty variants, and the rebuilt archive holds the new placement.
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

    private static async Task<(SourceWorkspace Workspace, ZbdDocument Archive, PickupPlacementEditSession Session)> BuildAsync(SourceWorldFixture fixture, SourceWorkspace? workspace = null)
    {
        workspace ??= new(fixture.Project);
        string preview = Path.Combine(SourceWorlds.PreviewRoot(fixture.Project), "preview-" + Guid.NewGuid().ToString("N"));
        var build = await SourceWorlds.BuildPreviewAsync(fixture.Project, "m1", preview, workspace.Overlay(), token: Token);
        Assert.Equal("built", build.Outputs.Single(o => o.Path == "m1/zrdr.zbd").Status);
        string path = Path.Combine(preview, "m1", "zrdr.zbd");
        var archive = FormatRegistry.Default.OpenBytes(path, await File.ReadAllBytesAsync(path, Token), token: Token);
        AssetRecord Member(string name) => archive.Assets.Single(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var session = PickupPlacementEditSession.Create([
            new(MissionDifficulty.Easy, archive, Member("puppies_easy.zrd")),
            new(MissionDifficulty.Medium, archive, Member("puppies.zrd")),
            new(MissionDifficulty.Hard, archive, Member("puppies.zrd"))], token: Token);
        Assert.Empty(session.Diagnostics);
        return (workspace, archive, session);
    }

    [Fact]
    public async Task AMoveChangesOnlyTheCoordinateTokensOfEveryLinkedSource()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/zrdr/puppies.zrd", Default); fixture.Write("data/m1/zrdr/puppies_easy.zrd", Easy);
        var (workspace, archive, session) = await BuildAsync(fixture);
        var ammo = session.Records.Single(r => r.Type == "HEMORTAR_AMMO" && r.Source.ResourceName == "PUPPIES.ZRD").Source;
        // The default record links to Easy's second record; Hard uses the default file itself, so it is one source.
        var scope = session.Scope(ammo);
        Assert.Equal(2, scope.Sources.Count);
        Assert.Contains("Easy", scope.Description); Assert.Contains("Hard", scope.Description);

        var after = session.PreviewTransform(ammo, session.Transform(ammo) with { Position = new(20.25f, 8, -5) });
        var writes = session.ScalarWrites(after);
        // Only X differs from the stored values, once per linked record.
        Assert.Equal(2, writes.Count); Assert.All(writes, w => Assert.Equal(20.25f, w.Value));
        var changes = SourceResourceEdits.SourceChanges(session.ArchiveBytes(writes[0].ArchivePath), writes.Select(w => new SourceResourceEdits.ScalarEdit(w.Offset, SourceResourceEdits.Float(w.Value))), r => workspace.Read(r, Token), Token);
        Assert.Equal(["data/m1/zrdr/puppies.zrd", "data/m1/zrdr/puppies_easy.zrd"], changes.Select(c => c.Relative).Order(StringComparer.Ordinal));
        // Each file differs from its source only where the edited token was: comments, spacing and other values stay.
        string defaultAfter = Encoding.Latin1.GetString(changes.Single(c => c.Relative.EndsWith("puppies.zrd")).Content);
        string easyAfter = Encoding.Latin1.GetString(changes.Single(c => c.Relative.EndsWith("puppies_easy.zrd")).Content);
        Assert.Equal(Default.Replace("( 12 8 -5 )", "( 20.25 8 -5 )"), defaultAfter);
        Assert.Equal(Easy.Replace("( 12 8 -5 )", "( 20.25 8 -5 )"), easyAfter);

        // Accepted into the workspace, the rebuilt archive holds the moved placement in both lists.
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
        var changes = SourceResourceEdits.SourceChanges(session.ArchiveBytes(writes[0].ArchivePath), writes.Select(w => new SourceResourceEdits.ScalarEdit(w.Offset, SourceResourceEdits.Float(w.Value))), r => workspace.Read(r, Token), Token);
        Assert.Equal(Default.Replace("( 0.0 1.5707964 0.0 )", "( 0.0 3.1415927 0.0 )"), Encoding.Latin1.GetString(changes.Single(c => c.Relative.EndsWith("puppies.zrd")).Content));
    }

    [Fact]
    public async Task ASourceChangedSinceTheBuildIsRefused()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/zrdr/puppies.zrd", Default); fixture.Write("data/m1/zrdr/puppies_easy.zrd", Easy);
        var (workspace, _, session) = await BuildAsync(fixture);
        var ammo = session.Records.Single(r => r.Type == "HEMORTAR_AMMO" && r.Source.ResourceName == "PUPPIES.ZRD").Source;
        var writes = session.ScalarWrites(session.PreviewTransform(ammo, session.Transform(ammo) with { Position = new(1, 2, 3) }));
        // The default list gains a record after the build: its tree no longer matches the built member.
        workspace.Apply("Add record", [("data/m1/zrdr/puppies.zrd", Encoding.Latin1.GetBytes(Default.Replace("\n)", "\n  ( NANITE 1 ( 0.0 0.0 0.0 ) ( 0.0 0.0 0.0 ) 1.0 )\n)")))], Token);
        var refused = Assert.Throws<InvalidDataException>(() => SourceResourceEdits.SourceChanges(session.ArchiveBytes(writes[0].ArchivePath), writes.Select(w => new SourceResourceEdits.ScalarEdit(w.Offset, SourceResourceEdits.Float(w.Value))), r => workspace.Read(r, Token), Token));
        Assert.Contains("rebuild", refused.Message);
    }

    [Fact]
    public void CompiledSourcesChangeOnlyTheEditedScalarBytes()
    {
        var tree = ZrdText.Parse(Default, Token);
        byte[] compiled = ZrdWriter.Write(tree, Token);
        var payload = ZrdDecoder.Read(compiled, Token);
        // The second coordinate of the first record.
        var y = payload.Children[0].Children[0].Children[2].Children[1];
        byte[] result = SourceResourceEdits.Apply(compiled, compiled, [new(y.SourceOffset, SourceResourceEdits.Float(9.5f))], "puppies.zrd", Token);
        var changed = Enumerable.Range(0, compiled.Length).Where(i => compiled[i] != result[i]).ToArray();
        Assert.All(changed, i => Assert.InRange(i, y.SourceOffset, y.SourceOffset + 7));
        var reread = ZrdDecoder.Read(result, Token).Children[0].Children[0].Children[2].Children[1];
        Assert.Equal(ZrdKind.Float, reread.Kind); Assert.Equal(9.5f, BitConverter.UInt32BitsToSingle(reread.Bits));
        // Offsets that are not numeric scalars are refused.
        Assert.Throws<InvalidDataException>(() => SourceResourceEdits.Apply(compiled, compiled, [new(payload.Children[0].SourceOffset, SourceResourceEdits.Float(1))], "puppies.zrd", Token));
    }
}
