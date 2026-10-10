using System.IO;
using System.Text;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class BlenderScriptAdmissionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Model = "data/m1/models/admission.gltf", Entry = "gamegen/m1.gs", Shared = "gamegen/shared.gw";
    private const string Json = """{"asset":{"version":"2.0"},"nodes":[{"name":"ground"}],"scenes":[{"nodes":[0]}]}""";
    private const string EntryText = "source shared.gw\nsource shared.gw\n", SharedText = "# shared source\n";

    private static SourceWorkspace Prepare(SourceWorldFixture fixture, bool pending)
    {
        fixture.Write(Model, Json); fixture.Write(Entry, EntryText); fixture.Write(Shared, pending ? "# old\n" : SharedText);
        File.Delete(fixture.Path("gamegen/m2.gs"));
        SourceWorkspace workspace = new(fixture.Project);
        if (pending) workspace.Apply("Pending script", [(Shared, (byte[]?)Encoding.ASCII.GetBytes(SharedText))], Token);
        return workspace;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ScriptRemainingAllowanceReachesDiskAndOwnedOverlayBeforeCheckoutRead(bool pending)
    {
        using SourceWorldFixture fixture = new();
        var workspace = Prepare(fixture, pending); List<string> read = [];
        long revision = workspace.Revision; int history = workspace.History.Count;
        var error = Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(workspace, Model, Token, read.Add,
            maximumScriptSourceBytes: EntryText.Length + SharedText.Length - 1));
        Assert.Contains($"{SharedText.Length - 1}", error.Message);
        Assert.Equal([Model, Entry], read);
        Assert.Equal(revision, workspace.Revision); Assert.Equal(history, workspace.History.Count);
        Assert.Equal(Encoding.ASCII.GetBytes(SharedText), workspace.Read(Shared, Token));
        Assert.Empty(SourceBlender.Checkouts(fixture.Project, Token));
    }

    [Fact]
    public void ExactScriptBoundaryReadsRepeatedSourceOnceAndStillChargesCheckoutTotal()
    {
        using SourceWorldFixture fixture = new();
        var workspace = Prepare(fixture, false); List<string> read = [];
        int scriptTotal = EntryText.Length + SharedText.Length, total = Encoding.UTF8.GetByteCount(Json) + scriptTotal;
        var refused = Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(workspace, Model, Token, read.Add,
            maximumExportBytes: total - 1, maximumScriptSourceBytes: scriptTotal));
        Assert.Contains($"exceeds {SharedText.Length - 1:N0} bytes", refused.Message);
        Assert.Equal([Model, Entry], read); Assert.Empty(SourceBlender.Checkouts(fixture.Project, Token));
        read.Clear();
        var checkout = SourceBlender.Checkout(workspace, Model, Token, read.Add,
            maximumExportBytes: total, maximumScriptSourceBytes: scriptTotal, maximumScriptTextBytes: EntryText.Length);
        Assert.Equal([Model, Entry, Shared], read);
        Assert.True(File.Exists(checkout.Input)); Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void PerFileLimitAndCancellationRefuseBeforeScriptPayloadAdmission()
    {
        using SourceWorldFixture fixture = new();
        var workspace = Prepare(fixture, false); List<string> read = [];
        var refused = Assert.Throws<InvalidDataException>(() => SourceBlender.Checkout(workspace, Model, Token, read.Add,
            maximumScriptTextBytes: EntryText.Length - 1));
        Assert.Contains($"exceeds {EntryText.Length - 1:N0} bytes", refused.Message);
        Assert.Equal([Model], read); Assert.Empty(SourceBlender.Checkouts(fixture.Project, Token));
        using CancellationTokenSource stop = new(); read.Clear();
        Assert.ThrowsAny<OperationCanceledException>(() => SourceBlender.Checkout(workspace, Model, stop.Token, path =>
        {
            read.Add(path);
            if (path == Model) stop.Cancel();
        }));
        Assert.Equal([Model], read); Assert.False(workspace.IsDirty);
        Assert.Empty(SourceBlender.Checkouts(fixture.Project, Token));
    }
}
