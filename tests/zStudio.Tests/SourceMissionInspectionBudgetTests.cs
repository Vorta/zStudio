using System.IO;
using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceMissionInspectionBudgetTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void FileLimitPrecedesReadingDecodingAndParsingIncludingPendingContent()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m2.gs", "#" + new string('x', 200));
        SourceWorkspace workspace = new(fixture.Project);
        SourceScriptInspection disk = new(workspace, Token, maximumFileBytes: 100);
        Assert.Contains("script input limits", Assert.Throws<IOException>(() => disk.Read("gamegen/m2.gs")).Message);
        Assert.Equal(0, disk.ParsedScripts);
        Assert.False(workspace.IsDirty);
        workspace.Apply("Pending script", [("gamegen/m2.gs", Encoding.Latin1.GetBytes("#" + new string('y', 300)))], Token);
        long revision = workspace.Revision;
        SourceScriptInspection pending = new(workspace, Token, maximumFileBytes: 100);
        Assert.Contains("script input limits", Assert.Throws<IOException>(() => pending.Read("gamegen/m2.gs")).Message);
        Assert.Equal(0, pending.ParsedScripts);
        Assert.Equal(revision, workspace.Revision);
        Assert.Equal("Pending script", workspace.UndoLabel);
    }

    [Fact]
    public void CommentOnlyMissionInputsShareAByteLimitAndRefusalLeavesHistoryUnchanged()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m2.gs", "#" + new string('a', 79));
        fixture.Write("gamegen/m3.gs", "#" + new string('b', 79));
        SourceWorkspace workspace = new(fixture.Project);
        SourceScriptInspection inspection = new(workspace, Token, maximumBytes: 120);
        var error = Assert.Throws<IOException>(() => SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", Token, inspection));
        Assert.Contains("script input limits", error.Message);
        Assert.Equal(1, inspection.ParsedScripts);
        Assert.Equal(0, workspace.Revision);
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.IsDirty);
        Assert.Equal("#" + new string('a', 79), File.ReadAllText(fixture.Path("gamegen/m2.gs")));
        Assert.Empty(SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", Token, new(workspace, Token, maximumBytes: 160)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AggregateTokensAndPhysicalLinesAreReservedBeforeConstructingAnotherSyntax(bool tokenLimit)
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/a.gw", tokenLimit ? "echo a b\n" : "# a\n# b\n");
        fixture.Write("gamegen/b.gw", tokenLimit ? "echo c d\n" : "# c\n# d\n");
        SourceWorkspace workspace = new(fixture.Project);
        SourceScriptInspection inspection = new(workspace, Token, maximumTokens: tokenLimit ? 5 : 100, maximumLines: tokenLimit ? 100 : 3);
        Assert.NotNull(inspection.Read("gamegen/a.gw"));
        Assert.Contains(tokenLimit ? "script tokens" : "script lines", Assert.Throws<IOException>(() => inspection.Read("gamegen/b.gw")).Message);
        Assert.Equal(1, inspection.ParsedScripts);
    }

    [Fact]
    public void RepeatedOwnershipQueriesReuseParsedIdentityButStillSpendTheSameWorkAllowance()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m2.gs", "source shared.gw\nsource other.gw\n");
        fixture.Write("gamegen/shared.gw", "# shared\n");
        fixture.Write("gamegen/other.gw", "# other\n");
        SourceWorkspace workspace = new(fixture.Project);
        SourceScriptInspection inspection = new(workspace, Token, maximumBytes: 100, maximumWork: 1000);
        Assert.Equal(["m2"], SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", Token, inspection));
        Assert.Equal(["m2"], SourceObjectEdits.MissionsRunning(workspace, "gamegen/other.gw", "m1", Token, inspection));
        Assert.Equal(3, inspection.ParsedScripts);
        var original = inspection.Read("gamegen/shared.gw");
        Assert.Same(original, inspection.Read("GAMEGEN\\SHARED.GW"));
        Assert.Equal(3, inspection.ParsedScripts);
        // These queries use only cached source, so a byte-only or per-query budget would never stop them.
        Assert.Contains("inspection work", Assert.Throws<IOException>(() =>
        {
            for (int i = 0; i < 20; i++) SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", Token, inspection);
        }).Message);
        Assert.Equal(3, inspection.ParsedScripts);
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void TransformInspectionUsesTheSameInputAllowanceAndCannotSwallowItsRefusal()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m2.gs", "#" + new string('x', 63));
        fixture.Write("gamegen/m3.gs", "SetModelDirectory ../data/m2/models/bft\nLoadGameGen tank.flt tank\nFindSubNode hull\nObject3DRotate 0 1 0\n");
        SourceWorkspace workspace = new(fixture.Project);
        SourceScriptInspection inspection = new(workspace, Token, maximumBytes: 100);
        // A prior ownership check in the same operation has already admitted this script.
        Assert.NotNull(inspection.Read("gamegen/m2.gs"));
        WorldNodeProvenance origin = new() { ModelFile = fixture.Tank };
        Assert.Contains("script input limits", Assert.Throws<IOException>(() => SourceObjectEdits.TransformElsewhere(workspace, "m1", origin, "hull", Token, inspection: inspection)).Message);
        Assert.Equal(1, inspection.ParsedScripts);
        Assert.False(workspace.CanUndo);
        var hit = SourceObjectEdits.TransformElsewhere(workspace, "m1", origin, "hull", Token);
        Assert.NotNull(hit);
        Assert.Equal("m3", hit.Value.Mission);
        Assert.Equal("Object3DRotate", hit.Value.Instruction.Command);
    }

    [Fact]
    public void MissingScriptsStayAbsentLockedScriptsRefuseAndMacrosStayUnknown()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m2.gs", "source missing.gw\n");
        SourceWorkspace workspace = new(fixture.Project);
        Assert.Empty(SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", Token));
        fixture.Write("gamegen/m2.gs", "source %part%.gw\n");
        Assert.Contains("may also run in m2", Assert.Throws<InvalidDataException>(() => SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", Token)).Message);
        using (FileStream held = new(fixture.Path("gamegen/m2.gs"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Contains("could not be read", Assert.Throws<IOException>(() => SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", Token)).Message);
        fixture.Write("gamegen/m2.gs", "source shared.gw\nsource %part%.gw\n");
        Assert.Equal(["m2"], SourceObjectEdits.MissionsRunning(workspace, "gamegen/shared.gw", "m1", Token));
    }

    [Fact]
    public void CancellationAfterAnAdmittedFileStopsCachedAndFreshInspectionWithTheSameToken()
    {
        using SourceWorldFixture fixture = new();
        using CancellationTokenSource cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        SourceWorkspace workspace = new(fixture.Project);
        SourceScriptInspection inspection = new(workspace, cancellation.Token);
        Assert.NotNull(inspection.Read("gamegen/m2.gs"));
        cancellation.Cancel();
        Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(() => inspection.Read("gamegen/m2.gs")).CancellationToken);
        Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(() => inspection.Read("gamegen/m1.gs")).CancellationToken);
        Assert.Equal(cancellation.Token, Assert.Throws<OperationCanceledException>(() => GameGenScriptSyntax.Parse("echo x\r\n", cancellation.Token)).CancellationToken);
        Assert.Equal(1, inspection.ParsedScripts);
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void CleanOversizedBaselineDoesNotMaskANewerSmallDiskFileWhileDirtyContentStillRefuses()
    {
        using SourceWorldFixture fixture = new();
        const string path = "gamegen/owned.gw";
        fixture.Write(path, "#" + new string('x', 200));
        SourceWorkspace workspace = new(fixture.Project);
        workspace.Apply("Change", [(path, Encoding.Latin1.GetBytes("# changed"))], Token);
        workspace.Undo(); // Keeps the original baseline buffer as the clean working value.
        Assert.Throws<InvalidDataException>(() => workspace.Read(path, Token, 100));
        fixture.Write(path, "# now small");
        Assert.Equal("# now small", Encoding.Latin1.GetString(Assert.IsType<byte[]>(workspace.Read(path, Token, 100))));
        Assert.Equal(0, workspace.UndoCount);

        SourceWorkspace dirty = new(fixture.Project);
        dirty.Apply("Large draft", [(path, Encoding.Latin1.GetBytes("#" + new string('y', 200)))], Token);
        fixture.Write(path, "# external small");
        Assert.Throws<InvalidDataException>(() => dirty.Read(path, Token, 100));
        Assert.Equal("Large draft", dirty.UndoLabel);
        Assert.Equal("#" + new string('y', 200), Encoding.Latin1.GetString(Assert.IsType<byte[]>(dirty.Read(path, Token, 300))));
    }

    [Fact]
    public void PreparedDependenciesRemainSealedEvenWhenInspectionReusesItsSyntax()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m2.gs", "source shared.gw\n");
        SourceWorkspace workspace = new(fixture.Project);
        var prepared = workspace.BeginPreparedEdit();
        SourceScriptInspection inspection = new(prepared.Workspace, Token);
        Assert.Equal(["m2"], SourceObjectEdits.MissionsRunning(prepared.Workspace, "gamegen/shared.gw", "m1", Token, inspection));
        Assert.Equal(["m2"], SourceObjectEdits.MissionsRunning(prepared.Workspace, "gamegen/shared.gw", "m1", Token, inspection));
        fixture.Write("gamegen/m2.gs", "source different.gw\n");
        Assert.Throws<SourceFileChangedException>(() => workspace.VerifyPreparedEdit(prepared, Token));
        Assert.Equal(0, workspace.Revision);
        Assert.False(workspace.CanUndo);
    }

    [Fact]
    public void ModelAdmissionPrecedesTheSecondContainerReadAndIsSharedAcrossTransformChecks()
    {
        using SourceWorldFixture fixture = new();
        const string first = "data/check/first.gltf", second = "data/check/second.glb";
        byte[] leaf = Glb("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"hull\"}]}");
        byte[] parent = Glb("{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"hull\",\"extras\":{\"recoil\":{\"ref\":\"first.gltf\"}}}]}");
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.Path(first))!);
        File.WriteAllBytes(fixture.Path(first), leaf); // Deliberately .gltf: framing, not suffix, decides GLB JSON.
        File.WriteAllBytes(fixture.Path(second), parent);
        fixture.Write("gamegen/m2.gs", "SetModelDirectory ../data/check\nLoadGameGen second.glb root\nFindSubNode hull\nObject3DRotate 0 1 0\n");
        SourceWorkspace workspace = new(fixture.Project);
        WorldNodeProvenance origin = new() { ModelFile = first };
        SourceScriptInspection limited = new(workspace, Token, maximumModelBytes: leaf.Length + parent.Length - 1);
        Assert.Contains("remaining model input limit", Assert.Throws<IOException>(() => SourceObjectEdits.TransformElsewhere(workspace, "m1", origin, "hull", Token, inspection: limited)).Message);
        Assert.Equal(leaf.Length, limited.RetainedModelBytes);
        Assert.False(workspace.CanUndo);
        Assert.Equal(parent, File.ReadAllBytes(fixture.Path(second)));
        SourceScriptInspection allowed = new(workspace, Token, maximumModelBytes: leaf.Length + parent.Length);
        Assert.NotNull(SourceObjectEdits.TransformElsewhere(workspace, "m1", origin, "hull", Token, inspection: allowed));
        Assert.NotNull(SourceObjectEdits.TransformElsewhere(workspace, "m1", origin, "hull", Token, inspection: allowed));
        Assert.Equal(leaf.Length + parent.Length, allowed.RetainedModelBytes);
        Assert.Same(allowed.ReadModel(first), allowed.ReadModel("DATA\\CHECK\\FIRST.GLTF"));

        static byte[] Glb(string json)
        {
            byte[] text = Encoding.UTF8.GetBytes(json);
            int length = (text.Length + 3) & ~3;
            byte[] result = new byte[12 + 8 + length + 8 + 64];
            "glTF"u8.CopyTo(result);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4), 2);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8), (uint)result.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(12), (uint)length);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(16), 0x4E4F534A);
            result.AsSpan(20, length).Fill((byte)' '); text.CopyTo(result, 20);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(20 + length), 64);
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(24 + length), 0x004E4942);
            return result;
        }
    }

    [Fact]
    public void CancellableParsingKeepsTheExistingDialectAndSourceSpans()
    {
        const string text = "#comment\r\necho a,,b\r\n\v\fecho\tvalue\u00a0kept\n\uFEFFecho b\rc";
        var syntax = GameGenScriptSyntax.Parse(text, Token);
        Assert.Equal(text, syntax.Text);
        Assert.Equal("\r\n", syntax.Newline);
        Assert.Equal(4, syntax.Lines.Count);
        Assert.Empty(syntax.Line(1).Tokens);
        Assert.Equal(["echo", "a", "", "b"], syntax.Line(2).Tokens);
        Assert.Equal([10, 15, 17, 18], syntax.Line(2).Spans.Select(s => s.Start));
        Assert.Equal([4, 1, 0, 1], syntax.Line(2).Spans.Select(s => s.Length));
        Assert.Equal(["echo", "value\u00a0kept"], syntax.Line(3).Tokens);
        Assert.Equal([23, 28], syntax.Line(3).Spans.Select(s => s.Start));
        Assert.Equal(["\uFEFFecho", "b\rc"], syntax.Line(4).Tokens);
        Assert.Equal([39, 45], syntax.Line(4).Spans.Select(s => s.Start));
        Assert.Equal(4, syntax.Line(4).Number); // Bare CR stays in a token; final EOF has no phantom line.
    }
}
