using System.Text.Json.Nodes;
using StreamJsonRpc;
using zStudio.Core.Language;
using zStudio.Core.Sources;

namespace zStudio.LanguageServer;

/// <summary>Thin LSP transport. Every language decision belongs to the shared Core analysis.</summary>
public sealed class GameZLanguageServer : IDisposable
{
    private sealed record Buffer(string Uri, string Path, string Text, int Version);
    private readonly Lock gate = new();
    private readonly Dictionary<string, Buffer> buffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Generation, LanguageProject Project)> projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim analysisGate = new(1);
    private readonly List<string> folders = [];
    private readonly HashSet<string> published = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource pending = new();
    private bool trusted, stopped;
    private long generation;
    public JsonRpc? Rpc { get; set; }

    [JsonRpcMethod("initialize", UseSingleObjectParameterDeserialization = true)]
    public object Initialize(JsonObject args)
    {
        lock (gate)
        {
            trusted = args["initializationOptions"]?["trusted"]?.GetValue<bool>() == true;
            if (args["workspaceFolders"] is JsonArray ws) foreach (var folder in ws) AddFolder(folder?["uri"]?.GetValue<string>());
            else AddFolder(args["rootUri"]?.GetValue<string>());
        }
        return new
        {
            capabilities = new
            {
                positionEncoding = "utf-16", textDocumentSync = new { openClose = true, change = 1, save = new { includeText = false } },
                completionProvider = new { resolveProvider = false, triggerCharacters = new[] { "(", " " } },
                hoverProvider = true, definitionProvider = true, documentSymbolProvider = true,
                executeCommandProvider = new { commands = new[] { "gamez.server.validateProject" } },
                workspace = new { workspaceFolders = new { supported = true, changeNotifications = true } }
            },
            serverInfo = new { name = "GameZ Script", version = "0.9.8" }
        };
    }
    [JsonRpcMethod("initialized", UseSingleObjectParameterDeserialization = true)] public void Initialized(JsonObject _) { }
    [JsonRpcMethod("shutdown")] public object? Shutdown() { lock (gate) { stopped = true; pending.Cancel(); } return null; }
    [JsonRpcMethod("exit")] public void Exit() => Rpc?.Dispose();

    [JsonRpcMethod("textDocument/didOpen", UseSingleObjectParameterDeserialization = true)]
    public void Open(JsonObject args)
    {
        var doc = args["textDocument"]!;
        Put(doc["uri"]!.GetValue<string>(), doc["text"]!.GetValue<string>(), doc["version"]!.GetValue<int>());
    }
    [JsonRpcMethod("textDocument/didChange", UseSingleObjectParameterDeserialization = true)]
    public void Change(JsonObject args)
    {
        var doc = args["textDocument"]!;
        if (args["contentChanges"] is JsonArray { Count: > 0 } changes)
            Put(doc["uri"]!.GetValue<string>(), changes[^1]!["text"]!.GetValue<string>(), doc["version"]!.GetValue<int>());
    }
    [JsonRpcMethod("textDocument/didClose", UseSingleObjectParameterDeserialization = true)]
    public async Task Close(JsonObject args)
    {
        string uri = Canonical(args["textDocument"]!["uri"]!.GetValue<string>());
        lock (gate) { buffers.Remove(uri); Invalidate(); }
        if (Rpc != null) await Rpc.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", new { uri, diagnostics = Array.Empty<object>() });
    }
    [JsonRpcMethod("textDocument/didSave", UseSingleObjectParameterDeserialization = true)] public void Save(JsonObject _) { lock (gate) Invalidate(); }
    [JsonRpcMethod("workspace/didChangeWatchedFiles", UseSingleObjectParameterDeserialization = true)] public void FilesChanged(JsonObject _) { lock (gate) Invalidate(); }
    [JsonRpcMethod("workspace/didChangeWorkspaceFolders", UseSingleObjectParameterDeserialization = true)]
    public void FoldersChanged(JsonObject args)
    {
        lock (gate)
        {
            if (args["event"]?["removed"] is JsonArray removed) foreach (var f in removed) if (Local(f?["uri"]?.GetValue<string>()) is { } path) folders.RemoveAll(p => p.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (args["event"]?["added"] is JsonArray added) foreach (var f in added) AddFolder(f?["uri"]?.GetValue<string>());
            Invalidate();
        }
    }
    private void AddFolder(string? uri) { if (Local(uri) is { } path && !folders.Contains(path, StringComparer.OrdinalIgnoreCase)) folders.Add(path); }
    private void Put(string uri, string text, int version)
    {
        string? path = Local(uri);
        if (path == null || !LanguageProject.IsLanguageFile(path)) return;
        uri = new Uri(path).AbsoluteUri;
        lock (gate)
        {
            if (buffers.TryGetValue(uri, out var old) && old.Version >= version) return;
            buffers[uri] = new(uri, path, text, version); Invalidate();
        }
    }
    private void Invalidate()
    {
        // Closed-file diagnostics from a project check belong to its old snapshot too.
        foreach (string uri in published.Where(uri => !buffers.ContainsKey(uri)).ToArray())
        {
            _ = NotifyEmpty(uri); published.Remove(uri);
        }
        generation++; projects.Clear(); pending.Cancel(); pending.Dispose(); pending = new();
        var cancellation = pending.Token; long revision = generation;
        _ = Background(revision, cancellation);
    }
    private async Task NotifyEmpty(string uri)
    {
        try { if (Rpc != null) await Rpc.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", new { uri, diagnostics = Array.Empty<object>() }); }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException or OperationCanceledException) { }
    }
    private async Task Background(long revision, CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            Buffer[] opened; lock (gate) opened = buffers.Values.ToArray();
            foreach (var buffer in opened)
            {
                var local = await Task.Run(() => RecoilLanguage.Analyze(buffer.Path, buffer.Text, token), token);
                await Publish(buffer, local, revision, token);
            }
            foreach (string root in opened.Select(b => Root(b.Path)).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var project = await Project(root, false, revision, token);
                foreach (var buffer in opened.Where(b => Root(b.Path)?.Equals(root, StringComparison.OrdinalIgnoreCase) == true))
                    if (project.Documents.TryGetValue(SourceProject.Relative(root, buffer.Path), out var doc)) await Publish(buffer, doc, revision, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            if (!token.IsCancellationRequested && Rpc != null)
            {
                await Rpc.NotifyWithParameterObjectAsync("window/logMessage", new { type = 2, message = "GameZ Script: project analysis incomplete: " + ex.Message });
                Buffer[] opened; lock (gate) opened = buffers.Values.ToArray();
                foreach (var buffer in opened)
                {
                    if (token.IsCancellationRequested) break;
                    var local = RecoilLanguage.Analyze(buffer.Path, buffer.Text, token);
                    await PublishIncomplete(buffer, local, revision, ex.Message, token);
                }
            }
        }
        catch (ObjectDisposedException) { }
    }
    private async Task PublishIncomplete(Buffer buffer, LanguageDocument doc, long revision, string message, CancellationToken token)
    {
        Task? notification;
        lock (gate)
        {
            if (token.IsCancellationRequested || stopped || generation != revision || !buffers.TryGetValue(buffer.Uri, out var current) || current.Version != buffer.Version) return;
            published.Add(buffer.Uri);
            object issue = new { range = Range(buffer.Text, new(0, 0)), severity = 3, code = "GAMEZ_PROJECT_INCOMPLETE", source = "GameZ Script", message = "Only document-local checks are available: " + (message.Length > 1000 ? message[..1000] : message) };
            notification = Rpc?.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", new { uri = buffer.Uri, version = buffer.Version, diagnostics = Diagnostics(doc).Append(issue).ToArray() });
        }
        if (notification != null) await notification;
    }
    private async Task Publish(Buffer buffer, LanguageDocument doc, long revision, CancellationToken token)
    {
        Task? notification;
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (stopped || generation != revision || !buffers.TryGetValue(buffer.Uri, out var current) || current.Version != buffer.Version) return;
            published.Add(buffer.Uri);
            notification = Rpc?.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", new { uri = buffer.Uri, version = buffer.Version, diagnostics = Diagnostics(doc) });
        }
        if (notification != null) await notification;
    }
    private async Task<LanguageProject> Project(string root, bool deep, long revision, CancellationToken token)
    {
        await analysisGate.WaitAsync(token);
        try
        {
            Dictionary<string, string> overlay;
            lock (gate)
            {
                if (!deep && projects.TryGetValue(root, out var cached) && cached.Generation == revision) return cached.Project;
                overlay = buffers.Values.Where(b => Under(b.Path, root)).ToDictionary(b => SourceProject.Relative(root, b.Path), b => b.Text, StringComparer.OrdinalIgnoreCase);
            }
            var project = await Task.Run(() => LanguageProject.Analyze(root, overlay, deep, token), token);
            lock (gate) if (generation == revision) projects[root] = (revision, project);
            return project;
        }
        finally { analysisGate.Release(); }
    }
    private string? Root(string path)
    {
        lock (gate)
        {
            if (!trusted) return null;
            string? boundary = folders.Where(f => Under(path, f)).MaxBy(f => f.Length);
            if (boundary == null) return null;
            for (string? folder = Path.GetDirectoryName(path); folder != null && Under(folder, boundary); folder = Path.GetDirectoryName(folder))
                if (SourceProject.IsProject(folder)) return folder;
            return null;
        }
    }
    private async Task<(LanguageDocument Document, LanguageProject? Project, int Offset)> Document(JsonObject args, CancellationToken token, bool contextualAnalysis = true)
    {
        string uri = Canonical(args["textDocument"]!["uri"]!.GetValue<string>());
        Buffer buffer; long revision; CancellationToken contextToken;
        lock (gate) { buffer = buffers.GetValueOrDefault(uri) ?? throw new InvalidOperationException("Document is not open."); revision = generation; contextToken = pending.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, contextToken);
        token = linked.Token;
        LanguageProject? project = null;
        string? root = contextualAnalysis ? Root(buffer.Path) : null;
        if (root != null)
        {
            try { project = await Project(root, false, revision, token); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException) { await Console.Error.WriteLineAsync(ex.Message); }
        }
        var doc = project != null && project.Documents.TryGetValue(SourceProject.Relative(root!, buffer.Path), out var contextual) ? contextual : RecoilLanguage.Analyze(buffer.Path, buffer.Text, token);
        token.ThrowIfCancellationRequested();
        lock (gate) if (generation != revision) throw new OperationCanceledException("Source buffer changed during language request.");
        return (doc, project, Offset(buffer.Text, args["position"]));
    }
    [JsonRpcMethod("textDocument/completion", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> Completion(JsonObject args, CancellationToken token)
    {
        var (doc, project, offset) = await Document(args, token);
        var items = project?.Complete(doc.Path, offset) ?? RecoilLanguage.Complete(doc, offset);
        return new { isIncomplete = items.Count >= RecoilLanguage.MaximumResults, items = items.Select(i => new { label = i.Label, detail = i.Detail, documentation = i.Documentation == null ? null : new { kind = "markdown", value = i.Documentation }, insertText = i.InsertText, kind = i.Kind == "reference" ? 18 : i.Kind == "enumMember" ? 20 : 14 }) };
    }
    [JsonRpcMethod("textDocument/hover", UseSingleObjectParameterDeserialization = true)]
    public async Task<object?> Hover(JsonObject args, CancellationToken token)
    {
        var (doc, _, offset) = await Document(args, token, false); var hover = RecoilLanguage.Hover(doc, offset);
        return hover == null ? null : new { contents = new { kind = "markdown", value = hover.Markdown }, range = Range(doc.Text, hover.Span) };
    }
    [JsonRpcMethod("textDocument/documentSymbol", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> Symbols(JsonObject args, CancellationToken token)
    {
        var (doc, _, _) = await Document(args, token, false);
        object Convert(LanguageSymbol symbol) => new { name = symbol.Name, kind = symbol.Kind == "class" ? 5 : 7, range = Range(doc.Text, symbol.Span), selectionRange = Range(doc.Text, symbol.Selection), children = symbol.Children.Select(Convert).ToArray() };
        return doc.Symbols.Select(Convert).ToArray();
    }
    [JsonRpcMethod("textDocument/definition", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> Definition(JsonObject args, CancellationToken token)
    {
        var (doc, project, offset) = await Document(args, token);
        if (project == null) return Array.Empty<object>();
        return project.Definition(doc.Path, offset).Select(location => new
        {
            uri = new Uri(SourceProject.Resolve(project.Root, location.Path)).AbsoluteUri,
            range = Range(project.Documents.TryGetValue(location.Path, out var target) ? target.Text : "", location.Span)
        }).ToArray();
    }
    [JsonRpcMethod("workspace/executeCommand", UseSingleObjectParameterDeserialization = true)]
    public async Task<object> Execute(JsonObject args, CancellationToken token)
    {
        if (args["command"]?.GetValue<string>() != "gamez.server.validateProject") throw new InvalidOperationException("Unknown command.");
        string? path = Local(args["arguments"]?[0]?.GetValue<string>());
        string root = path == null ? throw new InvalidOperationException("Choose an open source document.") : Root(path) ?? throw new InvalidOperationException("Open a trusted source-project workspace containing data/ and gamegen/.");
        long revision; CancellationToken contextToken; lock (gate) { revision = generation; contextToken = pending.Token; }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, contextToken); token = linked.Token;
        var project = await Project(root, true, revision, token);
        foreach (var doc in project.Documents.Values)
        {
            Task? notice;
            lock (gate)
            {
                token.ThrowIfCancellationRequested();
                if (generation != revision) throw new OperationCanceledException("Sources changed during validation.");
                string uri = new Uri(SourceProject.Resolve(root, doc.Path)).AbsoluteUri;
                published.Add(uri);
                int? version = buffers.GetValueOrDefault(uri)?.Version;
                notice = Rpc?.NotifyWithParameterObjectAsync("textDocument/publishDiagnostics", new { uri, version, diagnostics = Diagnostics(doc) });
            }
            if (notice != null) await notice;
        }
        return new { files = project.Documents.Count, errors = project.Documents.Values.Sum(d => d.Diagnostics.Count(x => x.Severity == "error")), warnings = project.Documents.Values.Sum(d => d.Diagnostics.Count(x => x.Severity == "warning")) };
    }
    private static object[] Diagnostics(LanguageDocument doc) => doc.Diagnostics.Select(d => (object)new
    {
        range = Range(doc.Text, d.Span), severity = d.Severity == "error" ? 1 : d.Severity == "warning" ? 2 : 3, code = d.Code, source = "GameZ Script",
        message = (d.Mission == null ? "" : d.Mission + ": ") + d.Message
    }).ToArray();
    public static object Range(string text, TextSpan span) => new { start = Position(text, span.Start), end = Position(text, span.End) };
    private static object Position(string text, int offset)
    {
        offset = Math.Clamp(offset, 0, text.Length); int line = 0, start = 0;
        for (int i = 0; i < offset; i++) if (text[i] == '\n') { line++; start = i + 1; }
        return new { line, character = offset - start };
    }
    private static int Offset(string text, JsonNode? position)
    {
        int line = position?["line"]?.GetValue<int>() ?? 0, column = position?["character"]?.GetValue<int>() ?? 0, start = 0;
        for (int i = 0; i < Math.Max(0, line); i++) { int next = text.IndexOf('\n', start); if (next < 0) return text.Length; start = next + 1; }
        int end = text.IndexOf('\n', start); if (end < 0) end = text.Length;
        return Math.Clamp((long)start + column, start, end) is long offset ? (int)offset : start;
    }
    private static bool Under(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Canonical(string uri) => Local(uri) is { } path ? new Uri(path).AbsoluteUri : uri;
    private static string? Local(string? uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile || parsed.IsUnc) return null;
        string path = parsed.LocalPath;
        // VS Code escapes the drive colon. Uri then retains the URI's leading slash.
        if (path.Length >= 4 && path[0] == '/' && char.IsAsciiLetter(path[1]) && path[2] == ':' && path[3] == '/') path = path[1..];
        return Path.GetFullPath(path);
    }
    public void Dispose() { lock (gate) { stopped = true; pending.Cancel(); } }
}
