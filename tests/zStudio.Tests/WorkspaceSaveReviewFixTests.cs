using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Source workspaces and their saves: a project that is a link or lies below one is never edited or saved, a file another
/// program replaces right after a save is reported rather than paired with the saved bytes, and an interrupted save's
/// journal with many new files in many folders is checked without comparing every folder with every file.
/// </summary>
public sealed class WorkspaceSaveReviewFixTests : IDisposable
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private const string Ai = "data/m1/ai.zrd", Script = "gamegen/m1.gs";
    private readonly string folder = Path.Combine(Path.GetTempPath(), "zstudio-workspace-save-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> links = [];

    public void Dispose()
    {
        // Links first, so that deleting the folder never reaches what they lead to.
        foreach (string link in links) try { Directory.Delete(link); } catch (IOException) { }
        try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    private static byte[] Text(string text) => Encoding.ASCII.GetBytes(text);
    private static string At(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
    /// <summary>A small source project with a resource and a script.</summary>
    private string NewProject(string path)
    {
        Directory.CreateDirectory(At(path, "data/m1")); Directory.CreateDirectory(At(path, "gamegen"));
        File.WriteAllBytes(At(path, Ai), Text("GRAVITY ( -9.8 )\n"));
        File.WriteAllBytes(At(path, Script), Text("load m1\n"));
        return path;
    }
    private bool Junction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true });
        process!.WaitForExit();
        bool made = Directory.Exists(link) && new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint);
        if (made) links.Add(link);
        return made;
    }

    // ---- Linked project roots ----

    [Fact]
    public void AProjectThatIsALinkOrLiesBelowOneIsNeverEditedOrSaved()
    {
        string real = Path.Combine(folder, "real"), project = NewProject(Path.Combine(real, "project"));
        string direct = Path.Combine(folder, "direct"), above = Path.Combine(folder, "above");
        if (!Junction(direct, project) || !Junction(above, real)) return; // Junctions unavailable on this file system.
        foreach (string linked in new[] { direct, Path.Combine(above, "project") })
        {
            // The project is found through the link, but nothing may edit it there: every later check starts below the root.
            Assert.True(SourceProject.IsProject(linked));
            Assert.Contains("is a link", Assert.Throws<IOException>(() => new SourceWorkspace(linked)).Message);
            Assert.Contains("is a link", Assert.Throws<IOException>(() => new SourcePublisher(linked)).Message);
        }
        Assert.False(Directory.Exists(At(project, "zstudio")));
        Assert.Equal(Text("GRAVITY ( -9.8 )\n"), File.ReadAllBytes(At(project, Ai)));
        // The folder itself is a project like any other.
        SourceWorkspace workspace = new(project);
        workspace.Apply("Edit", [(Ai, Text("GRAVITY ( -4.9 )\n"))], Token);
        Assert.Equal([Ai], workspace.Save(Token));
        Assert.Equal(Text("GRAVITY ( -4.9 )\n"), File.ReadAllBytes(At(project, Ai)));
    }

    [Fact]
    public void APublisherRefusesAProjectThatBecameALinkSinceItWasMade()
    {
        string moved = Path.Combine(folder, "moved"), root = NewProject(Path.Combine(folder, "project"));
        SourcePublisher publisher = new(root);
        SourceWorkspace workspace = new(root);
        workspace.Apply("Edit", [(Ai, Text("GRAVITY ( -4.9 )\n"))], Token);
        // Another program puts a link where the project was; the project's files are now elsewhere.
        Directory.Move(root, moved);
        if (!Junction(root, moved)) return;
        Assert.Contains("is a link", Assert.Throws<IOException>(() => publisher.Publish([new(Ai, Text("GRAVITY ( -9.8 )\n"), Text("GRAVITY ( -1.0 )\n"))], "Through the link", Token)).Message);
        Assert.Contains("is a link", Assert.Throws<IOException>(() => workspace.Save(Token)).Message);
        Assert.True(workspace.IsDirty);
        Assert.Equal(Text("GRAVITY ( -9.8 )\n"), File.ReadAllBytes(At(moved, Ai)));
        Assert.False(Directory.Exists(At(moved, "zstudio")));
    }

    // ---- The baseline recorded by a save ----

    [Fact]
    public void AFileReplacedRightAfterItsSaveIsReportedAndReadFromTheDisk()
    {
        string root = NewProject(Path.Combine(folder, "project"));
        byte[] saved = Text("GRAVITY ( -4.9 )\n"), replacement = Text("GRAVITY ( -1.0 ) # written by another program\n");
        string? replace = null;
        // Another program replaces the file after the publisher put it in place and before the workspace records it.
        SourceWorkspace workspace = new(root, (writes, description, token) =>
        {
            var written = new SourcePublisher(root).Publish([.. writes.Select(w => new SourceFileWrite(w.Relative, w.Expected, w.Content))], description, token).Written;
            if (replace != null) File.WriteAllBytes(At(root, replace), replacement);
            return written;
        });

        // Unchanged after its save: the workspace serves what it saved and reports nothing.
        workspace.Apply("Edit", [(Ai, saved)], Token);
        workspace.Save(Token);
        Assert.Empty(workspace.ExternalChanges());
        Assert.Equal(saved, workspace.Read(Ai, Token));

        // Replaced: the replacement is a change on disk, and it is what the workspace serves.
        replace = Ai;
        workspace.Apply("Edit again", [(Ai, Text("GRAVITY ( -2.0 )\n"))], Token);
        workspace.Save(Token);
        Assert.False(workspace.IsDirty);
        Assert.Equal([Ai], workspace.ExternalChanges());
        Assert.Equal(replacement, workspace.Read(Ai, Token));
        // An edit is never computed from the saved bytes: the history still describes them, so the file must be reloaded.
        Assert.Throws<SourceFileChangedException>(() => workspace.Apply("Stale", [(Ai, Text("GRAVITY ( -3.0 )\n"))], Token));
        Assert.Equal([Ai], workspace.Reload());
        Assert.Empty(workspace.ExternalChanges());
        Assert.Equal(replacement, workspace.Read(Ai, Token));

        // A file created by a save and removed by another program before it is recorded is reported too.
        replace = null;
        const string Added = "data/m1/added.zrd";
        SourceWorkspace removing = new(root, (writes, description, token) =>
        {
            var written = new SourcePublisher(root).Publish([.. writes.Select(w => new SourceFileWrite(w.Relative, w.Expected, w.Content))], description, token).Written;
            File.Delete(At(root, Added));
            return written;
        });
        removing.Apply("Add", [(Added, Text("ADDED ( 1 )\n"))], Token);
        removing.Save(Token);
        Assert.Equal([Added], removing.ExternalChanges());
        Assert.Null(removing.Read(Added, Token));
    }

    // ---- Journals of interrupted saves ----

    /// <summary>An interrupted save (the process ended while it installed its first file); returns its journal's manifest path.</summary>
    private static string Interrupted(string root)
    {
        SourcePublisher crashing = new(root) { Fault = (step, index) => { if (step == "install" && index == 0) throw new SourcePublisher.Crash(); } };
        Assert.Throws<SourcePublisher.Crash>(() => crashing.Publish([new(Ai, Text("GRAVITY ( -9.8 )\n"), Text("GRAVITY ( -4.9 )\n")), new("data/m1/new/added.zrd", null, Text("ADDED ( 2 )\n"))], "interrupted", Token));
        string recovery = At(root, SourcePublisher.RecoveryFolder);
        return Path.Combine(Directory.GetDirectories(recovery).Single(d => Path.GetFileName(d) != "abandoned"), "manifest.json");
    }
    private static JsonObject NewFile(string relative, bool created = true) => new()
    {
        ["relative"] = relative,
        ["expected"] = created ? null : new JsonObject { ["length"] = 1, ["sha256"] = new string('1', 64) },
        ["content"] = created ? new JsonObject { ["length"] = 1, ["sha256"] = new string('0', 64) } : null,
    };

    [Fact]
    public async Task AJournalWithManyNewFilesInManyFoldersIsCheckedInBoundedWork()
    {
        string root = NewProject(Path.Combine(folder, "project")), path = Interrupted(root);
        var manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        string id = manifest["saveId"]!.GetValue<string>();
        var files = manifest["files"]!.AsArray(); var folders = manifest["folders"]!.AsArray();
        // A save of as many files as a save may hold, each in its own new folders, listed in the order that makes a scan of
        // the files for each folder go furthest: about 150,000 folders × 25,000 files compared before.
        int count = SourceProject.MaximumFiles - files.Count;
        for (int i = 0; i < count; i++) files.Add(NewFile($"data/s{i:D5}/a/b/new.zrd"));
        for (int i = count - 1; i >= 0; i--) { folders.Add($"data/s{i:D5}"); folders.Add($"data/s{i:D5}/a"); folders.Add($"data/s{i:D5}/a/b"); }
        File.WriteAllText(path, manifest.ToJsonString());

        // A save reads every journal twice (cleaning up, then looking for one that blocks it): this one is valid and blocks it.
        var saving = Task.Run(() => Assert.Throws<SourceRecoveryRequiredException>(() => new SourcePublisher(root).Publish([new(Script, Text("load m1\n"), Text("load m2\n"))], "blocked", Token)), Token);
        SourceRecoveryRequiredException blocked;
        // Comparing every folder with every file took minutes; the check takes well under a second.
        try { blocked = await saving.WaitAsync(TimeSpan.FromSeconds(60), Token); }
        catch (TimeoutException) { Assert.Fail("Checking the journal's folders did not finish within a minute."); throw; }
        Assert.Equal(id, blocked.SaveId);
        Assert.Contains("was interrupted", blocked.Message);
        Assert.Equal(SourceProject.MaximumFiles, blocked.Files.Count);
    }

    [Fact]
    public void AJournalFolderMustStillHoldOneOfItsNewFiles()
    {
        string root = NewProject(Path.Combine(folder, "project")), path = Interrupted(root);
        string original = File.ReadAllText(path);
        // New files beside the save's folder whose names sort just before and after it ('-' < '/' < 'x'), and a deleted one.
        string[] extra = ["data/m1/new-other/x.zrd", "data/m1/newx/y.zrd"];
        foreach (var (listed, accepted) in new (string Folder, bool Accepted)[]
        {
            ("data/m1/new", true), ("DATA/M1/NEW", true), ("data/m1", true), ("data/m1/new-other", true), ("data/m1/newx", true),
            ("data/m1/ne", false), ("data/m1/new/added.zrd", false), ("data/m2", false), ("data/m1/gone", false), ("data/m1/newy", false),
        })
        {
            var manifest = JsonNode.Parse(original)!.AsObject();
            foreach (string file in extra) manifest["files"]!.AsArray().Add(NewFile(file));
            manifest["files"]!.AsArray().Add(NewFile("data/m1/gone/old.zrd", created: false));
            manifest["folders"] = new JsonArray(listed);
            File.WriteAllText(path, manifest.ToJsonString());
            if (accepted) Assert.Single(new SourcePublisher(root).FindInterrupted(Token));
            else Assert.Contains($"it lists the folder {listed}, which holds none of its new files", Assert.Throws<SourceRecoveryRequiredException>(() => new SourcePublisher(root).FindInterrupted(Token)).Message);
        }
    }
}
