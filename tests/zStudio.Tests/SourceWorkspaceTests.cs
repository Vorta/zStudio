using System.IO;
using System.Text;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>The project-wide workspace: one history over every source file, dirty state against the disk, save and external changes.</summary>
public sealed class SourceWorkspaceTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private readonly string root = Path.Combine(Path.GetTempPath(), "zstudio-workspace-" + Guid.NewGuid().ToString("N"));
    public SourceWorkspaceTests()
    {
        Write("gamegen/m1.gs", "set worldName world\r\nQuit\r\n");
        Write("data/m1/zrdr/puppies.zrd", "# pickups\n( ( NANITE 1 ( 1.0 2.0 3.0 ) ( 0.0 0.0 0.0 ) 30.0 ) )\n");
        Write("data/m2/zrdr/puppies.zrd", "( )\n");
    }
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }
    private string At(string relative) => System.IO.Path.Combine(root, relative.Replace('/', '\\'));
    private void Write(string relative, string text) { Directory.CreateDirectory(System.IO.Path.GetDirectoryName(At(relative))!); File.WriteAllText(At(relative), text, Encoding.Latin1); }
    private static byte[] Bytes(string text) => Encoding.Latin1.GetBytes(text);
    private static string Text(byte[]? bytes) => Encoding.Latin1.GetString(bytes!);

    [Fact]
    public void PreparedVerificationBoundsHeldDependenciesBeforeOpeningFiles()
    {
        SourceWorkspace workspace = new(root);
        var edit = workspace.BeginPreparedEdit();
        for (int i = 0; i < 4097; i++) Assert.Null(edit.Workspace.Read($"data/m1/missing{i}.zrd", Token));
        Assert.Contains("4,096", Assert.Throws<InvalidDataException>(() => workspace.VerifyPreparedEdit(edit, Token)).Message);
    }

    [Fact]
    public async Task PreparedVerificationHoldsContentAndCannotOutliveItsEvidence()
    {
        SourceWorkspace workspace = new(root);
        var edit = workspace.BeginPreparedEdit();
        edit.Workspace.Apply("Prepared", [("gamegen/m1.gs", Bytes("Quit 99\n"))], Token);
        using var verified = await Task.Run(() => workspace.VerifyPreparedEdit(edit, Token), Token);
        Assert.ThrowsAny<IOException>(() => File.WriteAllText(At("gamegen/m1.gs"), "changed"));
        Assert.ThrowsAny<IOException>(() => File.Delete(At("gamegen/m1.gs")));
        var accepted = workspace.AcceptPreparedEdit(edit, Token, verified);
        Assert.NotNull(accepted);
        verified.Dispose();
        File.WriteAllText(At("gamegen/m1.gs"), "changed");
        Assert.Throws<InvalidOperationException>(() => workspace.ValidatePreparedEdit(edit, Token, verified));
    }

    [Fact]
    public async Task PreparedVerificationRejectsLaterReadsAndMissingFilesAppearing()
    {
        SourceWorkspace workspace = new(root);
        var edit = workspace.BeginPreparedEdit();
        Assert.Null(edit.Workspace.Read("data/m1/new.zrd", Token));
        using var verified = await Task.Run(() => workspace.VerifyPreparedEdit(edit, Token), Token);
        Write("data/m1/new.zrd", "( )");
        Assert.Throws<SourceFileChangedException>(() => workspace.ValidatePreparedEdit(edit, Token, verified));
        File.Delete(At("data/m1/new.zrd"));
        edit.Workspace.Read("gamegen/m1.gs", Token);
        Assert.Throws<InvalidOperationException>(() => workspace.ValidatePreparedEdit(edit, Token, verified));
    }

    [Fact]
    public async Task AsyncReloadFindsSameStampChangesAndKeepsDirtyConflicts()
    {
        SourceWorkspace workspace = new(root);
        workspace.Apply("Saved", [("gamegen/m1.gs", Bytes("Quit 1\n"))], Token);
        workspace.Save(Token);
        var stamp = File.GetLastWriteTimeUtc(At("gamegen/m1.gs"));
        Write("gamegen/m1.gs", "Quit 2\n"); File.SetLastWriteTimeUtc(At("gamegen/m1.gs"), stamp);
        Assert.Equal(["gamegen/m1.gs"], await workspace.ReloadAsync(Token));
        Assert.False(workspace.CanUndo);
        workspace.Apply("Dirty", [("gamegen/m1.gs", Bytes("Quit 3\n"))], Token);
        Write("gamegen/m1.gs", "Quit 4\n"); File.SetLastWriteTimeUtc(At("gamegen/m1.gs"), stamp);
        await Assert.ThrowsAsync<SourceFileChangedException>(() => workspace.ReloadAsync(Token));
        Assert.True(workspace.IsDirty);
    }

    [Fact]
    public void SameStampExternalChangesAreVisibleToReadReloadAndPreparedEdits()
    {
        const string file = "gamegen/m1.gs";
        SourceWorkspace workspace = new(root);
        workspace.Apply("first", [(file, Bytes("Quit 1\n"))], Token); workspace.Save(Token);
        var stamp = File.GetLastWriteTimeUtc(At(file));
        Write(file, "Quit 2\n"); File.SetLastWriteTimeUtc(At(file), stamp);
        Assert.Contains(file, workspace.ExternalChanges());
        Assert.Equal("Quit 2\n", Text(workspace.Read(file, Token)));
        Assert.Contains(file, workspace.Reload()); Assert.Empty(workspace.History);
        var prepared = workspace.BeginPreparedEdit();
        prepared.Workspace.Read(file, Token);
        prepared.Workspace.Apply("prepared", [(file, Bytes("Quit 3\n"))], Token);
        Write(file, "Quit 4\n"); File.SetLastWriteTimeUtc(At(file), stamp);
        Assert.Throws<SourceFileChangedException>(() => workspace.AcceptPreparedEdit(prepared, Token));
        Assert.False(workspace.IsDirty); Assert.Empty(workspace.History);
    }

    [Theory]
    [InlineData("dependency")][InlineData("target")][InlineData("deleted_dependency")][InlineData("workspace")][InlineData("guard")][InlineData("cancel")]
    public void PreparedEditConflictsNeverPublishAndRetryKeepsUndo(string conflict)
    {
        SourceWorkspace w = new(root); int events = 0; w.Changed += _ => events++;
        const string target = "gamegen/m1.gs", input = "data/m2/zrdr/puppies.zrd";
        byte[] original = w.Read(target, Token)!;
        var prepared = w.BeginPreparedEdit();
        prepared.Workspace.Read(input, Token);
        prepared.Workspace.Apply("Prepared", [(target, Bytes("Quit 99\n"))], Token);
        Assert.False(w.IsDirty); Assert.Empty(w.History); Assert.Equal(0, events);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        switch (conflict)
        {
            case "dependency": Write(input, "changed dependency\n"); break;
            case "deleted_dependency": File.Delete(At(input)); break;
            case "target": Write(target, "changed target\n"); break;
            case "workspace": w.Apply("Other", [(input, Bytes("other\n"))], Token); break;
            case "guard": w.EditGuard = _ => "draft"; break;
            case "cancel": cancel.Cancel(); break;
        }
        long revision = w.Revision;
        Assert.ThrowsAny<Exception>(() => w.AcceptPreparedEdit(prepared, cancel.Token));
        Assert.Equal(revision, w.Revision);
        Assert.Equal(conflict == "workspace" ? 1 : 0, w.History.Count);
        w.EditGuard = null;
        var retry = w.BeginPreparedEdit();
        retry.Workspace.Apply("Retry", [(target, Bytes("Quit 100\n"))], Token);
        var accepted = w.AcceptPreparedEdit(retry, Token)!;
        Assert.Equal("Quit 100\n", Text(w.Read(target, Token)));
        w.Retract(accepted);
        Assert.Equal(conflict == "target" ? "changed target\n" : Text(original), Text(w.Read(target, Token)));
    }

    [Fact]
    public void PreparedEditFreezesTheBytesItReadAndCannotRefreshOverAnExternalChange()
    {
        SourceWorkspace w = new(root);
        var edit = w.BeginPreparedEdit();
        edit.Workspace.Read("gamegen/m1.gs", Token);
        Write("gamegen/m1.gs", "external newer script\n");
        Assert.Throws<SourceFileChangedException>(() => edit.Workspace.Apply("Edit", [("gamegen/m1.gs", Bytes("Quit\n"))], Token));
        Assert.False(w.IsDirty); Assert.Empty(w.History);

        // A multi-file replacement must admit its old content before retaining it, atomically even
        // without a prepared fork. Tiny limits cover the former multi-GiB old-buffer failure.
        const string a = "data/m1/a.bin", b = "data/m1/b.bin", c = "data/m1/c.bin";
        foreach (string path in new[] { a, b, c }) Write(path, new string('x', 32));
        SourceWorkspace bounded = new(root, null, 70);
        byte[] replacement = [1, 2, 3, 4];
        Assert.Throws<InvalidDataException>(() => bounded.Apply("Too many old files", [(a, replacement), (b, replacement), (c, replacement)], Token));
        Assert.Empty(bounded.History); Assert.False(bounded.IsDirty); Assert.Equal(0, bounded.Revision);
        Write(a, new string('y', 32));
        Assert.Empty(bounded.ExternalChanges(Token)); // Failed Apply did not publish its partial baselines.
        var retry = bounded.BeginPreparedEdit();
        retry.Workspace.Apply("Two files", [(a, replacement), (b, replacement)], Token);
        Assert.Empty(bounded.History);
        bounded.AcceptPreparedEdit(retry, Token);
        // 64 before bytes + one shared 4-byte replacement, counted once across paths/history/fork.
        Assert.Same(bounded.Read(a, Token), bounded.Read(b, Token));
        var next = bounded.BeginPreparedEdit();
        Assert.Throws<InvalidDataException>(() => next.Workspace.Apply("More retained content", [(a, new byte[4])], Token));
        Assert.Single(bounded.History); Assert.Same(replacement, bounded.Read(a, Token));
        bounded.Undo();
        Assert.Equal(new string('y', 32), Text(bounded.Read(a, Token)));
        Assert.Equal(new string('x', 32), Text(bounded.Read(b, Token)));
    }

    [Fact]
    public void OneHistoryCoversEveryFileAndSaveWritesThemTogether()
    {
        SourceWorkspace workspace = new(root);
        List<SourceWorkspaceChange> changes = [];
        workspace.Changed += changes.Add;
        // Two missions' files change in one step, then a third file in another.
        var both = workspace.Apply("Move shared pickup", [("data/m1/zrdr/puppies.zrd", Bytes("( )\n")), ("data/m2/zrdr/puppies.zrd", Bytes("( ( NANITE 1 ( 5.0 5.0 5.0 ) ( 0.0 0.0 0.0 ) 1.0 ) )\n"))], Token);
        Assert.NotNull(both);
        workspace.Apply("Edit script", [("gamegen/m1.gs", Bytes("set worldName world2\r\nQuit\r\n"))], Token);
        Assert.Equal(["data/m1/zrdr/puppies.zrd", "data/m2/zrdr/puppies.zrd", "gamegen/m1.gs"], workspace.DirtyFiles);
        Assert.Equal("Edit script", workspace.UndoLabel);
        // Disk is untouched until saved; the workspace serves its content.
        Assert.StartsWith("# pickups", File.ReadAllText(At("data/m1/zrdr/puppies.zrd")));
        Assert.Equal("( )\n", Text(workspace.Read("data/m1/zrdr/puppies.zrd", Token)));
        Assert.Equal(3, workspace.Overlay().Count);

        // Undo goes back across files in one step.
        workspace.Undo(); workspace.Undo();
        Assert.False(workspace.IsDirty); Assert.Empty(workspace.Overlay());
        Assert.Equal("Move shared pickup", workspace.RedoLabel);
        workspace.Redo(); workspace.Redo();
        Assert.Equal(["data/m1/zrdr/puppies.zrd", "data/m2/zrdr/puppies.zrd", "gamegen/m1.gs"], workspace.Save(Token).Order(StringComparer.Ordinal));
        Assert.False(workspace.IsDirty);
        Assert.Equal("( )\n", File.ReadAllText(At("data/m1/zrdr/puppies.zrd")));
        // Saved changes can still be undone, which makes the files dirty again, and saved again.
        workspace.Undo();
        Assert.Equal(["gamegen/m1.gs"], workspace.DirtyFiles);
        workspace.Save(Token);
        Assert.Equal("set worldName world\r\nQuit\r\n", File.ReadAllText(At("gamegen/m1.gs")));
        Assert.Equal(["apply", "apply", "undo", "undo", "redo", "redo", "save", "undo", "save"], changes.Select(c => c.Kind));
        // Content changes are logged by revision; a save is not a content change.
        Assert.Equal(["gamegen/m1.gs"], workspace.ChangedSince(workspace.ContentRevision - 1));
        Assert.Empty(workspace.ChangedSince(workspace.ContentRevision));
    }

    [Fact]
    public void OnlySourceFilesCanBeChanged()
    {
        SourceWorkspace workspace = new(root);
        foreach (string path in new[] { "zstudio/recovery/x.json", "readme.txt", "data/../gamegen/m1.gs", "../outside.zrd", "C:/abs.zrd" })
            Assert.ThrowsAny<Exception>(() => workspace.Apply("bad", [(path, Bytes("x"))], Token));
        Assert.Throws<InvalidDataException>(() => workspace.Apply("twice", [("gamegen/m1.gs", Bytes("a")), ("gamegen/M1.gs", Bytes("b"))], Token));
        // A name no save can write (a Windows device name, which a Blender export can give a new texture) is refused when the
        // edit is made, with the rest of that edit, instead of becoming an accepted edit that blocks every later save.
        Assert.Contains("rename 'aux.png'", Assert.Throws<InvalidDataException>(() =>
            workspace.Apply("device", [("gamegen/m1.gs", Bytes("Quit 1\n")), ("data/m1/textures/aux.png", Bytes("x"))], Token)).Message);
        // So is a file the save would refuse for its length or for what is on disk: a name longer than Windows stores (in a
        // folder the save would create), an existing file used as a folder (or one the same edit writes), a folder in its place.
        foreach (string[] files in new string[][] { ["data/newdir/" + new string('a', 260) + ".txt"], ["data/m1/zrdr/puppies.zrd/x.zrd"], ["data/m1/zrdr"], ["data/m1/new.zrd", "data/m1/new.zrd/x.zrd"] })
            Assert.Throws<InvalidDataException>(() => workspace.Apply("unsavable", [("gamegen/m1.gs", Bytes("Quit 1\n")), .. files.Select(f => (f, (byte[]?)Bytes("x")))], Token));
        Assert.Empty(workspace.History); Assert.False(workspace.IsDirty);
        // A change of nothing is not a step.
        Assert.Null(workspace.Apply("same", [("gamegen/m1.gs", workspace.Read("gamegen/m1.gs", Token))], Token));
        // New files can be created; deleting a file the disk holds is not supported yet.
        Assert.NotNull(workspace.Apply("new", [("data/m1/zrdr/new.zrd", Bytes("( 1 )\n"))], Token));
        Assert.True(workspace.Exists("data/m1/zrdr/new.zrd", Token));
        Assert.Throws<NotSupportedException>(() => workspace.Apply("delete", [("gamegen/m1.gs", null)], Token));
        workspace.Undo();
        Assert.False(workspace.Exists("data/m1/zrdr/new.zrd", Token)); Assert.False(workspace.IsDirty);
        Assert.Equal("new", workspace.RedoLabel);
    }

    [Fact]
    public void ChangesOnDiskAreFollowedWhenCleanAndRefusedWhenEdited()
    {
        SourceWorkspace workspace = new(root);
        workspace.Apply("Edit m2", [("data/m2/zrdr/puppies.zrd", Bytes("( 1 )\n"))], Token);
        workspace.Undo();
        // Another program edits a file the workspace touched: with history for it, a new edit is refused.
        Write("data/m2/zrdr/puppies.zrd", "( 2 )\n"); File.SetLastWriteTimeUtc(At("data/m2/zrdr/puppies.zrd"), DateTime.UtcNow.AddMinutes(1));
        Assert.Equal(["data/m2/zrdr/puppies.zrd"], workspace.ExternalChanges());
        Assert.Throws<SourceFileChangedException>(() => workspace.Apply("Edit m2 again", [("data/m2/zrdr/puppies.zrd", Bytes("( 3 )\n"))], Token));
        // Reloading takes the file from disk and starts a new history.
        Assert.Equal(["data/m2/zrdr/puppies.zrd"], workspace.Reload());
        Assert.False(workspace.CanUndo); Assert.Empty(workspace.ExternalChanges());
        Assert.Equal("( 2 )\n", Text(workspace.Read("data/m2/zrdr/puppies.zrd", Token)));
        workspace.Apply("Edit m2 again", [("data/m2/zrdr/puppies.zrd", Bytes("( 3 )\n"))], Token);
        // A dirty file changed on disk cannot be reloaded or saved over; discarding drops the edits.
        Write("data/m2/zrdr/puppies.zrd", "( 4 )\n"); File.SetLastWriteTimeUtc(At("data/m2/zrdr/puppies.zrd"), DateTime.UtcNow.AddMinutes(2));
        Assert.Throws<SourceFileChangedException>(() => workspace.Reload());
        Assert.ThrowsAny<IOException>(() => workspace.Save(Token));
        Assert.Equal("( 4 )\n", File.ReadAllText(At("data/m2/zrdr/puppies.zrd")));
        workspace.Discard();
        Assert.False(workspace.IsDirty); Assert.False(workspace.CanUndo);
        Assert.Equal("( 4 )\n", Text(workspace.Read("data/m2/zrdr/puppies.zrd", Token)));
    }

    [Fact]
    public void ACleanFileIsReadFromDiskAgainAfterAnotherProgramChangesIt()
    {
        SourceWorkspace workspace = new(root);
        // Edit pickups, undo it, then edit another file: no history step describes pickups any more, but the workspace still holds its old bytes.
        workspace.Apply("Move A", [("data/m1/zrdr/puppies.zrd", Bytes("( )\n"))], Token);
        workspace.Undo();
        workspace.Apply("Edit script", [("gamegen/m1.gs", Bytes("Quit\r\n"))], Token);
        Write("data/m1/zrdr/puppies.zrd", "# changed elsewhere\n( )\n");
        // An edit computed from what the workspace serves starts from the other program's version, and saving keeps it.
        Assert.StartsWith("# changed elsewhere", Text(workspace.Read("data/m1/zrdr/puppies.zrd", Token)));
        workspace.Apply("Move B", [("data/m1/zrdr/puppies.zrd", Bytes(Text(workspace.Read("data/m1/zrdr/puppies.zrd", Token)) + "# moved\n"))], Token);
        workspace.Save(Token);
        Assert.Equal("# changed elsewhere\n( )\n# moved\n", File.ReadAllText(At("data/m1/zrdr/puppies.zrd"), Encoding.Latin1));
    }

    [Fact]
    public void AWithdrawnChangeLeavesTheHistoryAsItWas()
    {
        SourceWorkspace workspace = new(root);
        // A full history: the next edit pushes out the oldest step, and a withdrawn edit brings it back.
        for (int i = 0; i < SourceWorkspace.MaximumHistory; i++) workspace.Apply($"Edit {i}", [("gamegen/m1.gs", Bytes($"# {i}\r\n"))], Token);
        var last = workspace.Apply("Last", [("gamegen/m1.gs", Bytes("# last\r\n"))], Token)!;
        workspace.Retract(last);
        for (int i = 0; i < SourceWorkspace.MaximumHistory; i++) workspace.Undo();
        Assert.False(workspace.CanUndo); Assert.False(workspace.IsDirty);
        // Undone steps come back too, to be redone.
        var other = workspace.Apply("Other", [("gamegen/m1.gs", Bytes("# other\r\n"))], Token)!;
        Assert.False(workspace.CanRedo);
        workspace.Retract(other);
        Assert.True(workspace.CanRedo); workspace.Redo();
        Assert.Equal("# 0\r\n", Text(workspace.Read("gamegen/m1.gs", Token)));
    }

    [Fact]
    public void RevertedChangesAndGuardedFilesKeepBuildsCurrent()
    {
        SourceWorkspace workspace = new(root);
        long before = workspace.ContentRevision;
        var t = workspace.Apply("Edit", [("gamegen/m1.gs", Bytes("Quit\r\n"))], Token)!;
        workspace.Retract(t);
        Assert.NotEmpty(workspace.ChangedSince(before));
        // The change was taken back: builds made before it are current again.
        workspace.ForgetChangesAfter(before);
        Assert.Empty(workspace.ChangedSince(before));
        // Undo and redo are refused while another editor holds unsaved changes of a file they would change.
        workspace.Apply("Edit again", [("gamegen/m1.gs", Bytes("Quit\r\n"))], Token);
        workspace.Undo();
        workspace.EditGuard = relative => relative == "gamegen/m1.gs" ? "open elsewhere" : null;
        Assert.Equal("open elsewhere", Assert.Throws<InvalidDataException>(() => workspace.Redo()).Message);
        workspace.EditGuard = null;
        workspace.Redo();
        // The history keeps at most its limit of steps.
        for (int i = 0; i < SourceWorkspace.MaximumHistory + 10; i++) workspace.Apply($"Step {i}", [("gamegen/m1.gs", Bytes($"Quit {i}\r\n"))], Token);
        Assert.Equal(SourceWorkspace.MaximumHistory, workspace.History.Count);
        Assert.Equal($"Step {SourceWorkspace.MaximumHistory + 9}", workspace.UndoLabel);
    }

    [Fact]
    public void AFailedSaveKeepsTheEditsAndTheFiles()
    {
        int calls = 0;
        SourceWorkspace workspace = new(root, (writes, description, token) => { calls++; throw new IOException("disk full"); });
        workspace.Apply("Edit", [("gamegen/m1.gs", Bytes("Quit\r\n"))], Token);
        Assert.Throws<IOException>(() => workspace.Save(Token));
        Assert.Equal(1, calls); Assert.True(workspace.IsDirty); Assert.False(workspace.IsSaving);
        Assert.Equal("set worldName world\r\nQuit\r\n", File.ReadAllText(At("gamegen/m1.gs")));
    }
}
