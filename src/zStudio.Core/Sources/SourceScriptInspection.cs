using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

internal sealed record SourceModelInspection(IReadOnlyList<string> References, IReadOnlyList<string> TerrainRecipes);

/// <summary>One edit's reads and scans of other missions, shared by all of its ownership and transform checks.</summary>
internal sealed class SourceScriptInspection(SourceWorkspace workspace, CancellationToken token,
    long maximumBytes = 64L * 1024 * 1024, long maximumTokens = GameGenScriptText.MaximumTokens,
    long maximumLines = GameGenScriptText.MaximumLines, long maximumWork = 256L * 1024 * 1024,
    long maximumFileBytes = SourceProject.MaximumSourceTextBytes, long maximumModelBytes = ReconstructionBudget.MaximumBytes,
    int maximumModelJsonBytes = Gltf.GltfDocument.MaximumJsonBytes)
{
    private readonly Dictionary<string, GameGenScriptSyntax?> scripts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]?> models = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SourceModelInspection?> modelReferences = new(StringComparer.OrdinalIgnoreCase);
    private string[]? worlds;
    private long bytes, tokens, lines, work;
    internal long RetainedModelBytes { get; private set; }
    internal int ParsedScripts { get; private set; }

    internal IReadOnlyList<string> Worlds()
    {
        token.ThrowIfCancellationRequested();
        workspace.TrackScriptInventory(token);
        // Protection includes wrapper entry points without their own data folder, even though the map picker
        // does not list them. Their executed sources can still consume the edited shared model or script.
        return worlds ??= SourceProject.Files(workspace.Root, SourceProject.GameGenFolder,
            name => name.EndsWith(".gs", StringComparison.OrdinalIgnoreCase), workspace.Overlay().Keys.ToArray(), token)
            .Where(path => path.Count(c => c == '/') == 1 && SourceGameProfile.IsMap(Path.GetFileNameWithoutExtension(path)) && workspace.Exists(path, token)).ToArray();
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

    internal GameGenScriptSyntax ParseEdit(string text)
    {
        if (text.Length > maximumFileBytes) throw Refused("script input");
        ReserveWork(text.Length);
        return Parse(text);
    }

    /// <summary>Physical dependencies, parsed once per inspection; reachability still belongs to each mission's traversal.</summary>
    internal SourceModelInspection? InspectModel(string relative)
    {
        ReserveWork(relative.Length + 1L);
        relative = SourceWorkspace.Normalize(relative);
        if (modelReferences.TryGetValue(relative, out var known)) return known;
        if (ReadModel(relative) is not { } content) return modelReferences[relative] = null;
        ReserveWork(content.LongLength);
        var hierarchy = GltfDocument.ReadHierarchy(content, token);
        string[] references = [.. SourceMissionModels.Dependencies(relative, hierarchy, ReserveWork)];
        List<string> terrain = [];
        foreach (var root in hierarchy.Roots) Terrain(root, 0);
        token.ThrowIfCancellationRequested();
        // Publish only a complete compact result, without retaining the parsed node/extras graph.
        return modelReferences[relative] = new(references, terrain);

        void Terrain(GltfNode node, int depth)
        {
            ReserveWork(1);
            if (depth >= GltfDocument.MaximumDepth) throw new IOException("The other mission's terrain groups are too deep to inspect safely.");
            if (node.Extras?[WorldGltf.Key]?["terrain"] is JsonValue value && value.TryGetValue(out string? uri) && uri != null)
            {
                ReserveWork(4L * (relative.Length + uri.Length + 1));
                terrain.Add(WorldAssembler.Relative(relative, uri));
            }
            else if (WorldGltf.IsTransparentTerrainGroup(node, relative))
                foreach (var child in node.Children) Terrain(child, depth + 1);
        }
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
