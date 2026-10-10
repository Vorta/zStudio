using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core;

/// <summary>One mission construction's work, cloned metadata and identity/display copies, admitted before allocation.</summary>
internal sealed class MissionPlacementBudget
{
    internal const long MaximumWork = 64L * 1024 * 1024, MaximumCloneBytes = 256L * 1024 * 1024;
    private readonly CancellationToken token;
    private readonly long maximumWork, maximumCloneBytes;
    private readonly Action? reserved;
    internal long Work { get; private set; }
    internal long CloneBytes { get; private set; } // Includes retained source identities and formatted placement labels.
    internal bool Exhausted { get; private set; }
    internal int RootPublications { get; set; }

    internal MissionPlacementBudget(CancellationToken token, long maximumWork = MaximumWork,
        long maximumCloneBytes = MaximumCloneBytes, Action? reserved = null)
    {
        if (maximumWork is < 0 or > MaximumWork) throw new ArgumentOutOfRangeException(nameof(maximumWork));
        if (maximumCloneBytes is < 0 or > MaximumCloneBytes) throw new ArgumentOutOfRangeException(nameof(maximumCloneBytes));
        this.token = token; this.maximumWork = maximumWork; this.maximumCloneBytes = maximumCloneBytes; this.reserved = reserved;
    }
    internal void Take(long count = 1)
    {
        token.ThrowIfCancellationRequested();
        if (Exhausted || count < 0 || count > maximumWork - Work) throw Refuse();
        Work += count; reserved?.Invoke(); token.ThrowIfCancellationRequested();
    }
    internal void Name(string name) => Take(1L + name.Length);
    internal void TextCopy(long characters) { Take(characters); Retain(32L + 2L * characters); }
    internal void Storage(long bytes) => Retain(bytes);
    internal void JsonCopy(JsonNode? node) => Json(node, 0);
    internal string SourceIdentity(string value, bool fullPath = false)
    {
        Name(value);
        // Relative Windows paths can expand by the current (possibly drive-specific) directory. Admit that
        // bounded prefix before GetFullPath allocates it, preserving its existing drive-relative semantics.
        long maximumCharacters = value.Length + (fullPath && !Path.IsPathFullyQualified(value) ? 32768L : 8L);
        TextCopy(maximumCharacters); if (fullPath) TextCopy(maximumCharacters);
        string normalized = fullPath ? Path.GetFullPath(value) : value;
        if (normalized.Length > maximumCharacters) throw Refuse();
        return normalized.ToUpperInvariant();
    }
    private void Retain(long count)
    {
        Take();
        if (count < 0 || count > maximumCloneBytes - CloneBytes) throw Refuse();
        CloneBytes += count;
    }
    private InvalidDataException Refuse()
    {
        Exhausted = true;
        return new("Mission placement exceeds its construction work or metadata budget. Reduce repeated placements or simplify their templates.");
    }
    internal void Clone(GameNode node, int? childCount = null)
    {
        long edges = node.Parents.Length + (long)(childCount ?? node.Children.Length);
        Take(1L + edges);
        Retain(256L + 4L * edges);
        Json(node.Data, 0); Json(node.Metadata, 0);
    }
    private void Json(JsonNode? node, int depth)
    {
        Take();
        if (depth > 256) throw Refuse();
        Retain(128);
        if (node == null) return;
        // Count cold containers through their raw, already-owned element; enumeration of JsonObject/JsonArray
        // would first materialize every immediate child. Never serialize a graph just to estimate its clone.
        if (node is JsonObject or JsonArray)
        {
            if (JsonData.UnderlyingElement == null) throw Refuse();
            if (JsonData.UnderlyingElement(node) is { } raw) { Raw(raw, depth); return; }
        }
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj) { Name(key); Retain(2L * key.Length + 64); Json(value, depth + 1); }
                break;
            case JsonArray array:
                foreach (var value in array) Json(value, depth + 1);
                break;
            case JsonValue value when value.TryGetValue(out JsonElement raw): Raw(raw, depth); break;
            case JsonValue value when value.TryGetValue(out string? text):
                Take(text?.Length ?? 0); Retain(2L * (text?.Length ?? 0)); break;
            case JsonValue value when KnownScalar(value): break;
            default: throw Refuse(); // Customized JsonValue serializers can retain arbitrary object graphs.
        }
    }
    private void Raw(JsonElement element, int depth)
    {
        ReadOnlySpan<byte> bytes = JsonMarshal.GetRawUtf8Value(element);
        Take(bytes.Length); // Before scanning a long scalar/property token.
        Retain(bytes.Length); // DeepClone may retain a new backing JsonDocument.
        Utf8JsonReader reader = new(bytes, new JsonReaderOptions { MaxDepth = 257 });
        while (reader.Read())
        {
            Take();
            if (depth + reader.CurrentDepth > 256) throw Refuse();
            Retain(128L + 2L * reader.ValueSpan.Length);
        }
    }
    private static bool KnownScalar(JsonValue value) => value.TryGetValue(out bool _) || value.TryGetValue(out byte _) ||
        value.TryGetValue(out sbyte _) || value.TryGetValue(out short _) || value.TryGetValue(out ushort _) ||
        value.TryGetValue(out int _) || value.TryGetValue(out uint _) || value.TryGetValue(out long _) ||
        value.TryGetValue(out ulong _) || value.TryGetValue(out float _) || value.TryGetValue(out double _) ||
        value.TryGetValue(out decimal _) || value.TryGetValue(out char _) || value.TryGetValue(out Guid _) ||
        value.TryGetValue(out DateTime _) || value.TryGetValue(out DateTimeOffset _);
}

/// <summary>Indexes only the owned scene. Authored rows and duplicate reciprocal edges are never deduplicated.</summary>
internal sealed class MissionPlacementState(GameScene scene, int originalCount, int worldRoot, MissionPlacementBudget budget)
{
    private readonly Dictionary<string, int> live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (int Root, int Count)> templates = new(StringComparer.Ordinal);
    private readonly HashSet<string> usedNames = new(StringComparer.Ordinal);
    private readonly HashSet<int> actorRoots = [];
    private readonly List<int> children = [];
    private readonly HashSet<int> attached = [];
    private bool deduplicated, changed;

    internal void Initialize()
    {
        for (int i = 0; i < scene.Nodes.Count; i++)
        {
            var node = scene.Nodes[i]; Index(node);
            if (i >= originalCount || node.Class != "object3d") continue;
            budget.Name(node.Name);
            templates.TryGetValue(node.Name, out var prior);
            templates[node.Name] = (node.Index, prior.Count + 1);
        }
        if (worldRoot < 0) return;
        budget.Take(scene.Nodes[worldRoot].Children.Length);
        foreach (int child in scene.Nodes[worldRoot].Children) { children.Add(child); attached.Add(child); }
    }
    private void Index(GameNode node)
    {
        budget.Name(node.Name); usedNames.Add(node.Name);
        if (node.Class != "none") { budget.Name(node.Name); live[node.Name] = node.Index; }
    }
    internal void Cloned(int start)
    { for (int i = start; i < scene.Nodes.Count; i++) Index(scene.Nodes[i]); }
    internal int Live(string name) { budget.Name(name); return live.GetValueOrDefault(name, -1); }
    internal (int Root, int Count) Template(string name) { budget.Name(name); return templates.GetValueOrDefault(name, (-1, 0)); }
    internal bool UseName(string name) { budget.Name(name); return usedNames.Add(name); }
    internal void Actor(int root) { budget.Take(); actorRoots.Add(root); }
    internal bool HasActor(int root) { budget.Take(); return actorRoots.Contains(root); }
    internal IReadOnlyList<int> Children(GameNode node) => changed && node.Index == worldRoot ? children : node.Children;
    internal void Attach(int root, bool aiv)
    {
        budget.Take();
        if (aiv && !deduplicated)
        {
            budget.Take(children.Count); attached.Clear();
            int kept = 0;
            for (int i = 0; i < children.Count; i++) { int child = children[i]; if (attached.Add(child)) children[kept++] = child; }
            children.RemoveRange(kept, children.Count - kept); deduplicated = true;
        }
        if (!aiv || attached.Add(root)) children.Add(root);
        changed = true;
    }
    internal void PublishChildren()
    {
        if (!changed || worldRoot < 0) return;
        budget.Take(children.Count);
        scene.Nodes[worldRoot] = scene.Nodes[worldRoot] with { Children = children.ToArray() };
        budget.RootPublications++;
    }
}
