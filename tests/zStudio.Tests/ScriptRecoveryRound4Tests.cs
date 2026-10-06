using System.Text;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;
using State = Recoil.Zbd.Core.Sources.SourceRecoveryFileState;

namespace Recoil.Zbd.Tests;

/// <summary>
/// Gamegen scripts bounded before their lines and tokens are made (and each read once per build), and interrupted saves
/// whose resolution stops between two files when it is canceled, leaving a journal that still resolves.
/// </summary>
public sealed class ScriptRecoveryRound4Tests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    /// <summary>Far below what making the refused script's lines or tokens would take (tens of MiB).</summary>
    private const long Refusal = 256 * 1024;

    private static long Allocated(Action action)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
    private static long Refused(Action action, string message)
    {
        InvalidDataException? error = null;
        long allocated = Allocated(() => error = Assert.Throws<InvalidDataException>(action));
        Assert.Contains(message, error!.Message);
        return allocated;
    }

    // ------------------------------------------------------------ bounded scripts

    [Fact]
    public void AScriptOfTooManyLinesIsRefusedBeforeItsLinesAreMade()
    {
        // A million and one short lines fit in a few MiB of a permitted 16 MiB file.
        string blank = new('\n', GameGenScriptText.MaximumLines + 1), short_ = string.Concat(Enumerable.Repeat("a\n", GameGenScriptText.MaximumLines + 1));
        foreach (string text in new[] { blank, short_ })
        {
            Assert.InRange(Refused(() => GameGenScriptText.Tokenize(text), "1,000,001 lines"), 0, Refusal);
            Assert.InRange(Refused(() => GameGenScriptSyntax.Parse(text), "1,000,001 lines"), 0, Refusal);
            // From bytes, only the decoded text is made.
            byte[] bytes = Encoding.Latin1.GetBytes(text);
            Assert.InRange(Refused(() => GameGenScriptSyntax.Parse(bytes), "1,000,001 lines"), 0, Refusal + 2L * text.Length);
        }
        // A world script is refused before the model lines are inserted, and a build before it parses the script.
        Refused(() => SourceWorlds.InsertIntoScript(Encoding.Latin1.GetBytes(short_), [new("data/m1/models/rock.gltf", "rock")]), "1,000,001 lines");
        var files = new CountingFiles(new() { ["gamegen/m1.gs"] = Encoding.Latin1.GetBytes(short_) });
        Refused(() => new WorldAssembler(files, Token).Assemble("m1.gs"), "1,000,001 lines");
        // As many lines as a build can run instructions are read (a final newline starts no further line).
        Assert.Empty(GameGenScriptText.Tokenize(new string('\n', GameGenScriptText.MaximumLines)));
        Assert.Equal(GameGenScriptText.MaximumLines, GameGenScriptSyntax.Parse(new string('\n', GameGenScriptText.MaximumLines)).Lines.Count);
    }

    [Fact]
    public void AScriptOfTooManyTokensIsRefusedBeforeItsTokensAreMade()
    {
        // One line of empty tokens (each comma ends one), and fewer lines than the limit with five tokens each.
        string commas = new(',', GameGenScriptText.MaximumTokens + 1);
        string lines = string.Concat(Enumerable.Repeat("a,b c\td,e\r\n", GameGenScriptText.MaximumTokens / 5 + 1));
        foreach (string text in new[] { commas, lines })
        {
            Assert.InRange(Refused(() => GameGenScriptText.Tokenize(text), "4,000,000 tokens"), 0, Refusal);
            Assert.InRange(Refused(() => GameGenScriptSyntax.Parse(text), "4,000,000 tokens"), 0, Refusal);
        }
        // Exactly as many tokens are read.
        var line = Assert.Single(GameGenScriptText.Tokenize(new string(',', GameGenScriptText.MaximumTokens)));
        Assert.Equal(GameGenScriptText.MaximumTokens, line.Count);
        // A comment holds no tokens, however long.
        Assert.Equal([["a"]], GameGenScriptText.Tokenize("a #" + commas));
    }

    [Fact]
    public void ScriptsTokenizeAsTheyDidBeforeTheyWereBounded()
    {
        // The engine's tokenizer as zStudio implemented it before: CRLF becomes LF, then every line is split.
        static List<IReadOnlyList<string>> Reference(string text)
        {
            List<IReadOnlyList<string>> lines = [];
            foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
            {
                string line = raw; int comment = line.IndexOf('#');
                if (comment >= 0) line = line[..comment];
                int cursor = 0; List<string> tokens = [];
                while (cursor < line.Length && line[cursor] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r') cursor++;
                while (true)
                {
                    int separator = line.IndexOfAny([',', ' ', '\t', '\n'], cursor);
                    if (separator < 0) break;
                    tokens.Add(line[cursor..separator]);
                    cursor = separator + 1;
                    while (cursor < line.Length && line[cursor] is ' ' or '\t' or '\n' or '\v' or '\f' or '\r') cursor++;
                }
                if (cursor < line.Length) tokens.Add(line[cursor..]);
                if (tokens.Count > 0) lines.Add(tokens);
            }
            return lines;
        }
        Random random = new(4);
        const string alphabet = "ab ,\t\r\n#\v\f%";
        List<string> texts = ["", "\n", "\r\n", "\r", "a\r", "a\r\r\n", "\r\r\n\r", "a,,", ",a", " , ", "a#b\r\nc", "LoadGameGen m1.flt m1.flt\r\nQuit"];
        for (int i = 0; i < 2000; i++) texts.Add(new string([.. Enumerable.Range(0, random.Next(0, 40)).Select(_ => alphabet[random.Next(alphabet.Length)])]));
        foreach (string text in texts)
        {
            var expected = Reference(text);
            Assert.Equal(expected, GameGenScriptText.Tokenize(text));
            var syntax = GameGenScriptSyntax.Parse(text);
            Assert.Equal(expected, syntax.Lines.Where(l => l.IsInstruction).Select(l => l.Tokens));
            // Each token's place in the text is the token.
            foreach (var l in syntax.Lines)
                for (int t = 0; t < l.Tokens.Count; t++) Assert.Equal(l.Tokens[t], text.Substring(l.Spans[t].Start, l.Spans[t].Length));
        }
    }

    [Fact]
    public void AScriptSourcedManyTimesIsReadOncePerBuild()
    {
        var files = new CountingFiles(new()
        {
            ["gamegen/m1.gs"] = Encoding.ASCII.GetBytes("source sub.gs\r\nsource SUB.gs\r\nsource sub.gs\r\nNewWorld world\r\nWorldOrigin 0.0 512.0\r\nWorldExtents 512.0 -512.0\r\nWorldPartition 256 -256\r\nGameZWriteZBDFile ..\\m1\\gamez.zbd\r\n"),
            ["gamegen/sub.gs"] = Encoding.ASCII.GetBytes("# a comment, a macro and a folder\r\nset x 1\r\nSetTextureDirectory ..\\data\\m1\\textures\r\n"),
        });
        var assembler = new WorldAssembler(files, Token);
        assembler.Assemble("m1.gs");
        Assert.Equal(1, files.Reads["gamegen/sub.gs"]);
        Assert.Equal(1, files.Reads["gamegen/m1.gs"]);
        // Each run still counts as one execution of its line.
        Assert.Equal(3, assembler.Executions.Where(e => e.Key.Script.Equals("gamegen/sub.gs", StringComparison.OrdinalIgnoreCase)).Sum(e => e.Value));
    }

    private sealed class CountingFiles(Dictionary<string, byte[]> files) : IProjectFiles
    {
        public Dictionary<string, int> Reads { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool Exists(string relative) => files.Keys.Any(k => k.Equals(relative, StringComparison.OrdinalIgnoreCase));
        public byte[] Read(string relative, CancellationToken token)
        {
            Reads[relative] = Reads.GetValueOrDefault(relative) + 1;
            return files.First(f => f.Key.Equals(relative, StringComparison.OrdinalIgnoreCase)).Value;
        }
    }

    // ------------------------------------------------------------ canceled recovery

    private const string Ai = "data/m1/ai.zrd", Added = "data/m1/new/added.zrd", Old = "data/m1/old.zrd";

    /// <summary>A source project whose save of three files (modify, create, delete) stopped at <paramref name="step"/>.</summary>
    private sealed class Interrupted : IDisposable
    {
        public Interrupted(string step, int index)
        {
            Directory.CreateDirectory(Full("data/m1")); Directory.CreateDirectory(Full("gamegen"));
            File.WriteAllText(Full(Ai), "GRAVITY ( -9.8 )\n"); File.WriteAllText(Full(Old), "OLD ( 1 )\n"); File.WriteAllText(Full("gamegen/m1.gs"), "Quit\n");
            Before = Sources();
            SourceFileWrite[] writes = [new(Ai, Read(Ai), Encoding.ASCII.GetBytes("GRAVITY ( -4.9 )\n")), new(Added, null, Encoding.ASCII.GetBytes("ADDED ( 2 )\n")), new(Old, Read(Old), null)];
            SourcePublisher crashing = new(Root) { Fault = (s, i) => { if (s == step && i == index) throw new SourcePublisher.Crash(); } };
            Assert.Throws<SourcePublisher.Crash>(() => crashing.Publish(writes, "interrupted", Token));
            SaveId = Assert.Single(new SourcePublisher(Root).FindInterrupted(Token)).SaveId;
        }
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-recovery4-" + Guid.NewGuid().ToString("N"));
        public string SaveId { get; }
        public SortedDictionary<string, string?> Before { get; }
        public SortedDictionary<string, string?> Saved
        {
            get
            {
                SortedDictionary<string, string?> after = new(Before, StringComparer.Ordinal) { [Ai] = "GRAVITY ( -4.9 )\n", ["data/m1/new"] = null, [Added] = "ADDED ( 2 )\n" };
                after.Remove(Old); return after;
            }
        }
        public string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        public byte[] Read(string relative) => File.ReadAllBytes(Full(relative));
        public byte[] Events => Read($"zstudio/recovery/{SaveId}/events.log");
        public SortedDictionary<string, string?> Sources()
        {
            SortedDictionary<string, string?> entries = new(StringComparer.Ordinal);
            foreach (string path in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
            {
                string relative = Path.GetRelativePath(Root, path).Replace('\\', '/');
                if (relative == "zstudio" || relative.StartsWith("zstudio/", StringComparison.Ordinal)) continue;
                entries[relative] = File.Exists(path) ? File.ReadAllText(path) : null;
            }
            return entries;
        }
        public SourceRecoveryFile[] Files() => Assert.Single(new SourcePublisher(Root).FindInterrupted(Token)).Files.ToArray();
        public void Dispose()
        {
            try { Directory.Delete(Root, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void ARollBackCanceledBetweenFilesLeavesASaveThatStillResolves()
    {
        // Stopped before committing: every file has the content the save wrote.
        using Interrupted project = new("commit", -1);
        var crashed = project.Sources();
        // Canceled before it starts: nothing changes, the journal included.
        byte[] events = project.Events;
        using (CancellationTokenSource canceled = new())
        {
            canceled.Cancel();
            var nothing = Assert.ThrowsAny<OperationCanceledException>(() => new SourcePublisher(project.Root).Resolve(project.SaveId, SourceRecoveryAction.RollBack, canceled.Token));
            Assert.Null(nothing.InnerException);
        }
        Assert.Equal(crashed, project.Sources()); Assert.Equal(events, project.Events);

        // Canceled while the last file (rolled back first) is restored: that file finishes, the others are not touched.
        using CancellationTokenSource cancellation = new();
        SourcePublisher publisher = new(project.Root) { Fault = (step, index) => { if (step == "undo" && index == 2) cancellation.Cancel(); } };
        var stopped = Assert.ThrowsAny<OperationCanceledException>(() => publisher.Resolve(project.SaveId, SourceRecoveryAction.RollBack, cancellation.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(stopped.InnerException);
        Assert.Contains("changed 1 file (data/m1/old.zrd)", stopped.Message);
        Assert.Contains("still needs a decision", stopped.Message);
        var partial = new SortedDictionary<string, string?>(crashed, StringComparer.Ordinal) { [Old] = "OLD ( 1 )\n" };
        Assert.Equal(partial, project.Sources());
        // The save is still interrupted, and its journal says which file went back.
        Assert.Equal([new(Ai, State.After, true), new(Added, State.After, false), new(Old, State.Before, false)], project.Files());
        Assert.Throws<SourceRecoveryRequiredException>(() => new SourcePublisher(project.Root).Publish([new("gamegen/m1.gs", project.Read("gamegen/m1.gs"), Encoding.ASCII.GetBytes("Quit\r\n"))], "blocked", Token));

        // Any action resolves it from there: rolling back again restores the rest.
        var result = new SourcePublisher(project.Root).Resolve(project.SaveId, SourceRecoveryAction.RollBack, Token);
        Assert.True(result.Resolved); Assert.Equal([Ai, Added], result.Changed); Assert.Empty(result.Conflicts);
        Assert.Equal(project.Before, project.Sources());
        Assert.Empty(new SourcePublisher(project.Root).FindInterrupted(Token));
    }

    [Fact]
    public void ACompletionCanceledBetweenFilesLeavesASaveThatStillResolves()
    {
        // Stopped before the first file was touched: every file has its content from before the save.
        using Interrupted project = new("intent", 0);
        Assert.Equal(project.Before, project.Sources());
        using CancellationTokenSource cancellation = new();
        SourcePublisher publisher = new(project.Root) { Fault = (step, index) => { if (step == "complete" && index == 0) cancellation.Cancel(); } };
        var stopped = Assert.ThrowsAny<OperationCanceledException>(() => publisher.Resolve(project.SaveId, SourceRecoveryAction.Complete, cancellation.Token));
        Assert.IsAssignableFrom<OperationCanceledException>(stopped.InnerException);
        Assert.Contains("changed 1 file (data/m1/ai.zrd)", stopped.Message);
        var partial = new SortedDictionary<string, string?>(project.Before, StringComparer.Ordinal) { [Ai] = "GRAVITY ( -4.9 )\n" };
        Assert.Equal(partial, project.Sources());
        Assert.Equal([new(Ai, State.After, true), new(Added, State.Before, false), new(Old, State.Before, false)], project.Files());

        // Rolled back from there, the completed file gets its original back.
        var rolledBack = new SourcePublisher(project.Root).Resolve(project.SaveId, SourceRecoveryAction.RollBack, Token);
        Assert.True(rolledBack.Resolved); Assert.Equal([Ai], rolledBack.Changed);
        Assert.Equal(project.Before, project.Sources());
    }

    [Fact]
    public void ACancellationNamesTheFilesItCouldNotResolve()
    {
        // Another program changes the first file as the completion reaches it, and the cancellation comes then too: the
        // conflict is reported with the cancellation rather than lost behind "nothing changed".
        using Interrupted project = new("intent", 0);
        using CancellationTokenSource cancellation = new();
        SourcePublisher publisher = new(project.Root)
        {
            Fault = (step, index) => { if (step == "complete" && index == 0) { File.WriteAllText(project.Full(Ai), "GRAVITY ( 1 )\n"); cancellation.Cancel(); } },
        };
        var stopped = Assert.ThrowsAny<OperationCanceledException>(() => publisher.Resolve(project.SaveId, SourceRecoveryAction.Complete, cancellation.Token));
        Assert.Contains("1 file could not be resolved (data/m1/ai.zrd changed while the save was being completed", stopped.Message);
        Assert.DoesNotContain("after it changed", stopped.Message);
    }

    [Fact]
    public void ACompletionCanceledThenCompletedFinishesTheSave()
    {
        using Interrupted project = new("intent", 0);
        using CancellationTokenSource cancellation = new();
        SourcePublisher publisher = new(project.Root) { Fault = (step, index) => { if (step == "complete" && index == 1) cancellation.Cancel(); } };
        var stopped = Assert.ThrowsAny<OperationCanceledException>(() => publisher.Resolve(project.SaveId, SourceRecoveryAction.Complete, cancellation.Token));
        Assert.Contains("changed 2 files (data/m1/ai.zrd, data/m1/new/added.zrd)", stopped.Message);
        Assert.Equal([new(Ai, State.After, true), new(Added, State.After, false), new(Old, State.Before, false)], project.Files());
        var result = new SourcePublisher(project.Root).Resolve(project.SaveId, SourceRecoveryAction.Complete, Token);
        Assert.True(result.Resolved); Assert.Equal([Old], result.Changed);
        Assert.Equal(project.Saved, project.Sources());
        Assert.Empty(new SourcePublisher(project.Root).FindInterrupted(Token));
    }

    [Fact]
    public void AnInterruptedSavesFilesAreListedWithoutReadingThem()
    {
        using Interrupted project = new("commit", -1);
        // Even while another program holds one of them.
        using (new FileStream(project.Full(Ai), FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Equal([Ai, Added, Old], new SourcePublisher(project.Root).SaveFiles(project.SaveId));
        Assert.Empty(new SourcePublisher(project.Root).SaveFiles("20990101T000000000Z-00000000"));
        Assert.Empty(new SourcePublisher(project.Root).SaveFiles("not a save"));
        // Rolled back, but its journal could not be removed: resolving it again changes no file of the project.
        using (new FileStream(project.Full($"zstudio/recovery/{project.SaveId}/manifest.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Assert.False(new SourcePublisher(project.Root).Resolve(project.SaveId, SourceRecoveryAction.RollBack, Token).Resolved);
        Assert.Equal(project.Before, project.Sources());
        Assert.True(File.Exists(project.Full($"zstudio/recovery/{project.SaveId}/manifest.json")));
        Assert.Empty(new SourcePublisher(project.Root).SaveFiles(project.SaveId));
        Assert.True(new SourcePublisher(project.Root).Resolve(project.SaveId, SourceRecoveryAction.Complete, Token).Resolved);
        Assert.Empty(new SourcePublisher(project.Root).SaveFiles(project.SaveId));
    }
}
