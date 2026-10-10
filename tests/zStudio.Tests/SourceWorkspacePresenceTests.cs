using System.IO;
using System.Text;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class SourceWorkspacePresenceTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string root = Path.Combine(Path.GetTempPath(), "zstudio-presence-" + Guid.NewGuid().ToString("N"));
    private const string FileName = "data/present.bin", Added = "data/added.bin", Target = "gamegen/edit.gs";
    public SourceWorkspacePresenceTests()
    {
        Directory.CreateDirectory(Path.Combine(root, "data"));
        Directory.CreateDirectory(Path.Combine(root, "gamegen"));
        File.WriteAllBytes(At(FileName), [1, 2, 3]);
        File.WriteAllText(At(Target), "Quit\n");
    }
    private string At(string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    public void Dispose() => Directory.Delete(root, true);

    [Fact]
    public void PresenceNeverOpensPayloadEvenAfterACleanSaveAndExternalChanges()
    {
        SourceWorkspace workspace = new(root);
        workspace.Apply("change", [(FileName, new byte[64 * 1024])], Token);
        workspace.Save(Token);
        // FileShare.None rejects payload reads/hashes. Metadata presence remains available.
        using (var held = new FileStream(At(FileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.True(workspace.Exists(FileName, Token));
            Func<string, bool> metadataPresence = workspace.Exists;
            Assert.True(metadataPresence(FileName));
        }
        File.WriteAllBytes(At(FileName), [9]);
        Assert.True(workspace.Exists(FileName, Token));
        File.Delete(At(FileName));
        Assert.False(workspace.Exists(FileName, Token));
        File.WriteAllBytes(At(FileName), [8]);
        Assert.True(workspace.Exists(FileName, Token));
        Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void PendingPresenceFollowsUndoRedoRetractionSaveAndDiscard()
    {
        SourceWorkspace workspace = new(root);
        var added = workspace.Apply("add", [(Added, new byte[] { 1 })], Token)!;
        Assert.True(workspace.Exists(Added, Token));
        workspace.Undo(); Assert.False(workspace.Exists(Added, Token)); Assert.False(workspace.IsDirty);
        workspace.Redo(); Assert.True(workspace.Exists(Added, Token)); Assert.True(workspace.IsDirty);
        workspace.Retract(added); Assert.False(workspace.Exists(Added, Token)); Assert.False(workspace.IsDirty);
        workspace.Apply("add again", [(Added, new byte[] { 2 })], Token);
        workspace.Apply("remove pending addition", [(Added, (byte[]?)null)], Token);
        Assert.False(workspace.Exists(Added, Token)); Assert.False(workspace.IsDirty);
        workspace.Undo(); Assert.True(workspace.Exists(Added, Token)); Assert.True(workspace.IsDirty);
        workspace.Redo(); Assert.False(workspace.Exists(Added, Token)); Assert.False(workspace.IsDirty);
        workspace.Undo();
        workspace.Save(Token); Assert.False(workspace.IsDirty);
        // Existing deletion refusal remains: the overlay cannot hide a saved file from builds.
        Assert.Throws<NotSupportedException>(() => workspace.Undo());
        Assert.Throws<NotSupportedException>(() => workspace.Apply("delete saved", [(Added, (byte[]?)null)], Token));
        Assert.True(workspace.Exists(Added, Token)); Assert.False(workspace.IsDirty);
        Assert.Equal(new byte[] { 2 }, File.ReadAllBytes(At(Added)));
        workspace.Apply("change saved", [(Added, new byte[] { 3 })], Token);
        workspace.Discard(); Assert.True(workspace.Exists(Added, Token)); Assert.False(workspace.IsDirty);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreparedPresenceRejectsAddedOrRemovedFiles(bool initiallyPresent)
    {
        SourceWorkspace workspace = new(root);
        string dependency = initiallyPresent ? FileName : Added;
        var prepared = workspace.BeginPreparedEdit();
        Assert.Equal(initiallyPresent, prepared.Workspace.Exists(dependency, Token));
        prepared.Workspace.Apply("prepared", [(Target, Encoding.UTF8.GetBytes("Quit 2\n"))], Token);
        if (initiallyPresent) File.Delete(At(dependency));
        else File.WriteAllText(At(dependency), "arrived");
        var error = Assert.Throws<SourceFileChangedException>(() => workspace.VerifyPreparedEdit(prepared, Token));
        Assert.Contains(dependency, error.Files);
        Assert.Throws<SourceFileChangedException>(() => workspace.AcceptPreparedEdit(prepared, Token));
        Assert.Throws<SourceFileChangedException>(() => prepared.Workspace.Read(dependency, Token));
        Assert.False(workspace.IsDirty); Assert.Empty(workspace.History);
    }

    [Fact]
    public void HeldPresencePermitsContentWritesAndAcceptanceButPreventsRemoval()
    {
        SourceWorkspace workspace = new(root);
        var prepared = workspace.BeginPreparedEdit();
        Assert.True(prepared.Workspace.Exists(FileName, Token));
        prepared.Workspace.Apply("prepared", [(Target, Encoding.UTF8.GetBytes("Quit 2\n"))], Token);
        File.WriteAllText(At(FileName), "changed before verification");
        using var verified = workspace.VerifyPreparedEdit(prepared, Token);
        File.WriteAllText(At(FileName), "changed after verification");
        if (OperatingSystem.IsWindows()) Assert.ThrowsAny<IOException>(() => File.Delete(At(FileName)));
        Assert.NotNull(workspace.AcceptPreparedEdit(prepared, Token, verified));
        Assert.Equal("changed after verification", File.ReadAllText(At(FileName)));
        workspace.Undo(); Assert.False(workspace.IsDirty);
        verified.Dispose();
        File.Delete(At(FileName));
        Assert.False(workspace.Exists(FileName, Token));
    }

    [Fact]
    public void PresenceUpgradedToContentRetainsDigestEvidence()
    {
        SourceWorkspace workspace = new(root);
        var prepared = workspace.BeginPreparedEdit();
        Assert.True(prepared.Workspace.Exists(FileName, Token));
        Assert.Equal(new byte[] { 1, 2, 3 }, prepared.Workspace.Read(FileName, Token));
        var stamp = File.GetLastWriteTimeUtc(At(FileName));
        File.WriteAllBytes(At(FileName), [4, 5, 6]); File.SetLastWriteTimeUtc(At(FileName), stamp);
        Assert.Throws<SourceFileChangedException>(() => workspace.VerifyPreparedEdit(prepared, Token));
        Assert.Throws<SourceFileChangedException>(() => workspace.ValidatePreparedEdit(prepared, Token));
    }

    [Fact]
    public void LaterPresenceQueriesInvalidateHeldVerificationAndCancellationPrecedesIo()
    {
        SourceWorkspace workspace = new(root);
        var prepared = workspace.BeginPreparedEdit();
        Assert.True(prepared.Workspace.Exists(FileName, Token));
        using var verified = workspace.VerifyPreparedEdit(prepared, Token);
        Assert.False(prepared.Workspace.Exists(Added, Token));
        Assert.Throws<InvalidOperationException>(() => workspace.ValidatePreparedEdit(prepared, Token, verified));
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => workspace.Exists("../outside", canceled.Token));
        Assert.Throws<OperationCanceledException>(() => workspace.VerifyPreparedEdit(prepared, canceled.Token));
        Assert.Throws<InvalidDataException>(() => workspace.Exists("../outside", Token));
        Assert.False(workspace.Exists("data", Token));
        Assert.Equal(0, workspace.Revision); Assert.Empty(workspace.History);
    }

    [Fact]
    public void AbsentPresenceIsRecheckedAtAcceptanceAndDisposedEvidenceIsRejected()
    {
        SourceWorkspace workspace = new(root);
        var prepared = workspace.BeginPreparedEdit();
        Assert.False(prepared.Workspace.Exists(Added, Token));
        prepared.Workspace.Apply("prepared", [(Target, Encoding.UTF8.GetBytes("Quit 2\n"))], Token);
        using var verified = workspace.VerifyPreparedEdit(prepared, Token);
        File.WriteAllText(At(Added), "arrived after verification");
        Assert.Throws<SourceFileChangedException>(() => workspace.AcceptPreparedEdit(prepared, Token, verified));
        Assert.False(workspace.IsDirty); Assert.Empty(workspace.History);
        File.Delete(At(Added));
        Assert.NotNull(workspace.ValidatePreparedEdit(prepared, Token, verified));
        verified.Dispose();
        Assert.Throws<InvalidOperationException>(() => workspace.AcceptPreparedEdit(prepared, Token, verified));
        Assert.NotNull(workspace.AcceptPreparedEdit(prepared, Token));
        workspace.Undo(); Assert.False(workspace.IsDirty);
    }

    [Fact]
    public void PresenceDependenciesShareTheVerificationFileLimitWithContentReads()
    {
        SourceWorkspace workspace = new(root);
        var prepared = workspace.BeginPreparedEdit();
        for (int i = 0; i < 4097; i++)
        {
            string path = $"data/missing{i}.bin";
            if (i % 2 == 0) Assert.False(prepared.Workspace.Exists(path, Token));
            else Assert.Null(prepared.Workspace.Read(path, Token));
        }
        Assert.Contains("4,096", Assert.Throws<InvalidDataException>(() => workspace.VerifyPreparedEdit(prepared, Token)).Message);
        Assert.False(workspace.IsDirty); Assert.Empty(workspace.History);
    }

    [Theory]
    [InlineData("disk")]
    [InlineData("dirty")]
    [InlineData("saved")]
    public void TypedResourceAllowanceIsAppliedToDiskAndAcceptedContent(string state)
    {
        SourceWorkspace workspace = new(root);
        byte[] text = Encoding.UTF8.GetBytes("( " + new string(' ', 128) + ")");
        if (state == "disk") File.WriteAllBytes(At(FileName), text);
        else { workspace.Apply("text", [(FileName, text)], Token); if (state == "saved") workspace.Save(Token); }
        var error = Assert.Throws<InvalidDataException>(() => workspace.Read(FileName, Token, ProjectReadLimits.Resource(1024, 32)));
        Assert.Contains("Resource text", error.Message);
        Assert.Equal(text, workspace.Read(FileName, Token, ProjectReadLimits.Resource(1024, 256)));
        Assert.Throws<InvalidDataException>(() => workspace.Read(FileName, Token, ProjectReadLimits.Resource(1024, 256).WithMaximum(16)));
    }

    [Fact]
    public void ResourceBinaryHeaderUsesBinaryAllowanceAndRepeatedReadsCannotRelaxTypedLimits()
    {
        byte[] binary = new byte[128]; binary[0] = 1;
        File.WriteAllBytes(At(FileName), binary);
        SourceWorkspace workspace = new(root);
        var prepared = workspace.BeginPreparedEdit();
        Assert.Equal(binary, prepared.Workspace.Read(FileName, Token, ProjectReadLimits.Resource(128, 16)));
        Assert.Throws<InvalidDataException>(() => prepared.Workspace.Read(FileName, Token, ProjectReadLimits.Bytes(127)));
        Assert.Throws<InvalidDataException>(() => prepared.Workspace.Read(FileName, Token, ProjectReadLimits.Model(128, 16)));
        Assert.Equal(binary, prepared.Workspace.Read(FileName, Token, ProjectReadLimits.Resource(128, 16)));
    }
}
