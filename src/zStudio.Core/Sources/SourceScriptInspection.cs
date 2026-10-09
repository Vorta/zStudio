namespace Recoil.Zbd.Core.Sources;

/// <summary>One edit's reads and scans of other missions, shared by all of its ownership and transform checks.</summary>
internal sealed class SourceScriptInspection(SourceWorkspace workspace, CancellationToken token,
    long maximumBytes = 64L * 1024 * 1024, long maximumTokens = GameGenScriptText.MaximumTokens,
    long maximumLines = GameGenScriptText.MaximumLines, long maximumWork = 64L * 1024 * 1024,
    long maximumFileBytes = SourceProject.MaximumSourceTextBytes, long maximumModelBytes = ReconstructionBudget.MaximumBytes,
    int maximumModelJsonBytes = Gltf.GltfDocument.MaximumJsonBytes)
{
    private readonly Dictionary<string, GameGenScriptSyntax?> scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]?> models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceMissionModels> missions = new(StringComparer.OrdinalIgnoreCase);
    private string[]? worlds;
    private long bytes, tokens, lines, work;
    internal long RetainedModelBytes { get; private set; }
    internal int ParsedScripts { get; private set; }

    internal IReadOnlyList<string> Worlds()
    {
        token.ThrowIfCancellationRequested();
        return worlds ??= SourceProject.Files(workspace.Root, SourceProject.GameGenFolder,
            n => System.Text.RegularExpressions.Regex.IsMatch(n, @"\Am\d+\.gs\z", System.Text.RegularExpressions.RegexOptions.IgnoreCase), token: token)
            .Where(p => p.Count(c => c == '/') == 1).ToArray();
    }

    internal GameGenScriptSyntax? Read(string relative)
    {
        ReserveWork(relative.Length + 1L);
        relative = SourceWorkspace.Normalize(relative);
        if (scripts.TryGetValue(relative, out var known)) return known;
        byte[]? content;
        try
        {
            // Workspace enforces this on disk length before reading, and on pending bytes before returning them.
            content = workspace.Read(relative, token, Math.Min(SourceProject.MaximumSourceTextBytes, Math.Min(maximumFileBytes, maximumBytes - bytes)));
        }
        catch (Exception ex) when (ex is not SourceFileChangedException && ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new IOException($"Cannot inspect the other missions' scripts: {JsonData.ShownText(relative)} could not be read within the script input limits. Restore access or reduce the scripts, then retry the edit.", ex);
        }
        token.ThrowIfCancellationRequested();
        if (content == null) return scripts[relative] = null;
        Reserve(ref bytes, content.LongLength, maximumBytes, "script input");
        // Charge all text, including comments, before decoding/counting; repeated graph scans are charged separately.
        ReserveWork(content.LongLength);
        string text = GameGenScriptText.Decode(content, token);
        var parsed = Parse(text);
        ParsedScripts++;
        return scripts[relative] = parsed;
    }

    internal bool ModelExists(string relative)
    {
        try { return workspace.Exists(relative, token); }
        catch (Exception ex) when (ex is not SourceFileChangedException && ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new IOException($"Cannot inspect the other missions' model references: {JsonData.ShownText(relative)} could not be read. Restore access, then retry the edit.", ex); }
    }

    internal byte[]? ReadModel(string relative)
    {
        ReserveWork(relative.Length + 1L);
        relative = SourceWorkspace.Normalize(relative);
        if (models.TryGetValue(relative, out var known)) return known;
        byte[]? content;
        try { content = workspace.ReadModel(relative, token, maximumModelBytes - RetainedModelBytes, maximumModelJsonBytes); }
        catch (Exception ex) when (ex is not SourceFileChangedException && ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            throw new IOException($"Cannot inspect the other missions' model references: {JsonData.ShownText(relative)} could not be read within the remaining model input limit. Restore access or reduce the models, then retry the edit.", ex);
        }
        token.ThrowIfCancellationRequested();
        if (content != null) RetainedModelBytes += content.LongLength;
        return models[relative] = content;
    }

    internal SourceMissionModels MissionModels(string mission)
    {
        ReserveWork(mission.Length + 1L);
        if (missions.TryGetValue(mission, out var known)) return known;
        string path = SourceMapZones.PathForMission(mission);
        try
        {
            byte[]? content = workspace.Read(path, token, Math.Min(SourceMapZones.MaximumBytes, maximumBytes - bytes));
            if (content == null) return missions[mission] = new(null, token);
            Reserve(ref bytes, content.LongLength, maximumBytes, "script and map input");
            ReserveWork(content.LongLength);
            return missions[mission] = new(SourceMapZones.Parse(content, token), token);
        }
        catch (Exception ex) when (ex is not SourceFileChangedException && ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { throw new IOException($"Cannot inspect the other mission's model bindings: {JsonData.ShownText(path)} could not be read. Restore access or repair the map, then retry the edit.", ex); }
    }

    internal GameGenScriptSyntax ParseEdit(string text)
    {
        if (text.Length > maximumFileBytes) throw Refused("script input");
        ReserveWork(text.Length);
        return Parse(text);
    }

    private GameGenScriptSyntax Parse(string text)
    {
        token.ThrowIfCancellationRequested();
        long count = SourceTextScan.Count(text, '\n', text.Length, token) + (text.Length == 0 || text[^1] != '\n' ? 1 : 0);
        Reserve(ref lines, count, maximumLines, "script lines");
        try { count = GameGenScriptText.CountTokens(text, token); }
        catch (InvalidDataException ex) { throw new IOException("Cannot inspect the other missions' scripts: the script token limit was exceeded. Reduce the scripts, then retry the edit.", ex); }
        Reserve(ref tokens, count, maximumTokens, "script tokens");
        return GameGenScriptSyntax.Parse(text, token);
    }

    internal void ReserveWork(long amount) => Reserve(ref work, amount, maximumWork, "script inspection work");

    private void Reserve(ref long used, long amount, long limit, string kind)
    {
        token.ThrowIfCancellationRequested();
        if (amount < 0 || amount > limit - used) throw Refused(kind);
        used += amount;
    }

    private static IOException Refused(string kind) => new($"Checking other missions exceeds the aggregate {kind} limit. Reduce the scripts or edit them directly, then retry.");
}
