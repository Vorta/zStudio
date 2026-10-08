using System.IO;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ProjectInputAdmissionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void NarrowingPreservesStructuralAdmissionAndCannotIncreaseCapacity()
    {
        var resource = ProjectReadLimits.Resource(512, 64).WithMaximum(1024);
        Assert.Equal(512, resource.MaximumBytes);
        Assert.Contains("Resource text", Assert.Throws<InvalidDataException>(() => resource.Validate(Encoding.ASCII.GetBytes("( )".PadRight(65)))).Message);
        Assert.Equal(32, resource.WithMaximum(32).MaximumBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => resource.WithMaximum(-1));
        Assert.Equal(SourceProject.MaximumSourceTextBytes, ProjectReadLimits.Text(long.MaxValue).MaximumBytes);
        Assert.Throws<InvalidDataException>(() => ProjectReadLimits.Model(512, 64).WithMaximum(1024).Validate(Encoding.ASCII.GetBytes("{}".PadRight(65))));
    }

    [Fact]
    public void RawAdmissionReadsNothingAndResourceAdmissionReadsOnlyTheStructuralPrefixBeforeRefusal()
    {
        byte[] text = Encoding.ASCII.GetBytes("( 1 )".PadRight(256));
        using CountingStream raw = new(text);
        Assert.Throws<InvalidDataException>(() => SourceRead.All(raw, ProjectReadLimits.Bytes(128), "source", Token));
        Assert.Equal(0, raw.ReadBytes);
        using CountingStream typed = new(text);
        Assert.Contains("Resource text", Assert.Throws<InvalidDataException>(() => SourceRead.All(typed, ProjectReadLimits.Resource(512, 128), "compiled-looking.zrd", Token)).Message);
        Assert.Equal(20, typed.ReadBytes);
        byte[] binary = ZrdWriter.Write(ZrdText.Parse("( " + string.Join(' ', Enumerable.Range(0, 40)) + " )", Token), Token);
        Assert.True(binary.Length > 128);
        using CountingStream accepted = new(binary);
        byte[] read = SourceRead.All(accepted, ProjectReadLimits.Resource(512, 128), "text-looking.zad", Token);
        Assert.Equal(binary, read);
        Assert.Equal(40, Assert.Single(ZrdDecoder.Read(read, Token).Children).Children.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SnapshotAndDiskProvidersApplyEveryRequestToDiskAndOverlay(bool pending)
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/m1/zrdr/admission.zrd";
        byte[] bytes = Encoding.ASCII.GetBytes("( 1 )".PadRight(256));
        fixture.Write(path, Encoding.ASCII.GetString(bytes));
        Dictionary<string, byte[]>? overlay = pending ? new() { [path] = bytes } : null;
        SourceBuilder.Snapshot snapshot = new(fixture.Project, overlay);
        IProjectFiles provider = snapshot.Files();
        Assert.Equal(bytes, provider.Read(path, Token, ProjectReadLimits.Document));
        Assert.Contains("Resource text", Assert.Throws<InvalidDataException>(() => provider.Read(path, Token, ProjectReadLimits.Resource(512, 128))).Message);
        Assert.Throws<InvalidDataException>(() => provider.Read(path, Token, ProjectReadLimits.Bytes(128)));
        Assert.Contains(path, snapshot.Dependencies());
        IProjectFiles disk = new SourceWorlds.DiskFiles(fixture.Project, overlay);
        Assert.Contains("Resource text", Assert.Throws<InvalidDataException>(() => disk.Read(path, Token, ProjectReadLimits.Resource(512, 128))).Message);
        Assert.Equal(bytes, disk.Read(path, Token, ProjectReadLimits.Document));
        if (pending) Assert.Same(bytes, provider.Read(path, Token, ProjectReadLimits.Document));
        Assert.Equal(bytes, File.ReadAllBytes(fixture.Path(path)));
    }

    [Fact]
    public void SnapshotStillRejectsSameStampChangedContentAndNewlyPresentDependencies()
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/m1/zrdr/admission.zrd", missing = "data/m1/zrdr/absent.zrd";
        fixture.Write(path, "( 1 )");
        SourceBuilder.Snapshot snapshot = new(fixture.Project);
        Assert.False(snapshot.Exists(missing));
        Assert.Equal("( 1 )", Encoding.ASCII.GetString(snapshot.Read(path, Token, ProjectReadLimits.Resource(128, 64))));
        DateTime stamp = File.GetLastWriteTimeUtc(fixture.Path(path));
        fixture.Write(path, "( 2 )"); File.SetLastWriteTimeUtc(fixture.Path(path), stamp);
        Assert.Contains("changed", Assert.Throws<InvalidDataException>(() => snapshot.Read(path, Token, ProjectReadLimits.Resource(128, 64))).Message);
        fixture.Write(missing, "( 3 )");
        Assert.Contains("changed", Assert.Throws<InvalidDataException>(() => snapshot.Read(missing, Token, ProjectReadLimits.Resource(128, 64))).Message);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("lines")]
    [InlineData("tokens")]
    public void DamageMaskDiscoverySharesInputAndParserAllowancesAcrossScripts(string limit)
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m1.gs", "# first\nWriteTextureSetMap first.tif\n");
        fixture.Write("gamegen/m2.gs", "# second\nWriteTextureSetMap second.tif\n");
        SourceBuilder.Snapshot snapshot = new(fixture.Project);
        Assert.Throws<InvalidDataException>(() => snapshot.DamageMasks(Token,
            maximumSourceBytes: limit == "bytes" ? 50 : 1024,
            maximumTokens: limit == "tokens" ? 3 : 100,
            maximumLines: limit == "lines" ? 3 : 100));
        Assert.Equal(["first", "second"], snapshot.DamageMasks(Token, 1024, 100, 100).Order(StringComparer.Ordinal));
        Assert.Equal("# first\nWriteTextureSetMap first.tif\n", File.ReadAllText(fixture.Path("gamegen/m1.gs")));
    }

    [Fact]
    public void TypedRefusalsDoNotPublishHashesAndCancellationKeepsItsToken()
    {
        using SourceWorldFixture fixture = new();
        const string path = "data/m1/zrdr/admission.zrd";
        fixture.Write(path, "( )".PadRight(256));
        SourceBuilder.Snapshot snapshot = new(fixture.Project);
        Assert.Throws<InvalidDataException>(() => snapshot.Read(path, Token, ProjectReadLimits.Resource(512, 128)));
        Assert.Empty(snapshot.Hashes());
        using CancellationTokenSource canceled = CancellationTokenSource.CreateLinkedTokenSource(Token);
        canceled.Cancel();
        Assert.Equal(canceled.Token, Assert.Throws<OperationCanceledException>(() => snapshot.Read(path, canceled.Token, ProjectReadLimits.Document)).CancellationToken);
        using FileStream locked = new(fixture.Path(path), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.ThrowsAny<IOException>(() => snapshot.Read(path, Token, ProjectReadLimits.Resource(512, 128)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AddingAModelAdmitsScriptAndDefinitionsBeforeChangingHistory(bool oversizedDefinitions)
    {
        using SourceWorldFixture fixture = new();
        const string script = "NewWorld world\nGameZWriteZBDFile world.zbd\n";
        const string definitions = "ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ) )\n";
        fixture.Write("gamegen/m1.gs", oversizedDefinitions ? script : script + "#".PadRight(512));
        fixture.Write("data/m1/zrdr/anim.zad", oversizedDefinitions ? definitions.PadRight(512) : definitions);
        SourceWorkspace workspace = new(fixture.Project);
        SourceWorldAddition addition = new(new(fixture.Tank, "added_tank", new(1, 0, 2)), [SourceWorldFixture.TankDefinitions]);
        Assert.Throws<InvalidDataException>(() => SourceWorlds.AddModel(workspace, "m1", addition, Token, 128));
        Assert.False(workspace.CanUndo);
        Assert.False(workspace.IsDirty);
        fixture.Write("gamegen/m1.gs", script);
        fixture.Write("data/m1/zrdr/anim.zad", definitions);
        Assert.NotNull(SourceWorlds.AddModel(workspace, "m1", addition, Token, 128));
        Assert.Equal(1, workspace.UndoCount);
        Assert.Equal(script, File.ReadAllText(fixture.Path("gamegen/m1.gs")));
        workspace.Undo();
        Assert.False(workspace.IsDirty);
    }

    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal int ReadBytes { get; private set; }
        public override int Read(Span<byte> buffer) { int count = base.Read(buffer); ReadBytes += count; return count; }
    }
}
