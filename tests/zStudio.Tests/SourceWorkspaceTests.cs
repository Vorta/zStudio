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
        // A change of nothing is not a step.
        Assert.Null(workspace.Apply("same", [("gamegen/m1.gs", workspace.Read("gamegen/m1.gs", Token))], Token));
        // New files can be created; deleting a file the disk holds is not supported yet.
        Assert.NotNull(workspace.Apply("new", [("data/m1/zrdr/new.zrd", Bytes("( 1 )\n"))], Token));
        Assert.True(workspace.Exists("data/m1/zrdr/new.zrd"));
        Assert.Throws<NotSupportedException>(() => workspace.Apply("delete", [("gamegen/m1.gs", null)], Token));
        workspace.Undo();
        Assert.False(workspace.Exists("data/m1/zrdr/new.zrd")); Assert.False(workspace.IsDirty);
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
