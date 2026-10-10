using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core;

/// <summary>
/// One mission construction's work and retained copies, admitted before allocation. The allowances bound what
/// placements add (vehicle and pickup copies, their identities and labels, startup lists), which repeated records
/// can multiply. The copy of the world's own nodes is measured apart (<see cref="Original"/>): it is the size of the
/// source scene, which the reader (at most 65,536 nodes) and a preview load's resource allowance have already
/// admitted, so every accepted world builds its starting scene.
/// </summary>
internal sealed class MissionPlacementBudget
{
    internal const long MaximumWork = 64L * 1024 * 1024, MaximumCloneBytes = 256L * 1024 * 1024;
    // What a copy retains (JsonNode.DeepClone, .NET 10 x64, measured on reader-built nodes): a scalar value 47 bytes
    // (a string keeps the source's characters), an object about 265 bytes with one property and 33-39 bytes per further
    // one, an array about 125 bytes with one element and 17 per further one. On the 1998 and 1999 m1, m2 and m6 starting
    // scenes, the world copy and the placements retain about 14.3 KB per node and are charged 1.45 times what they retain.
    private const long ScalarBytes = 64, ObjectBytes = 320, PropertyBytes = 48, ArrayBytes = 160, ElementBytes = 24;
    private readonly CancellationToken token;
    private readonly long maximumWork, maximumCloneBytes;
    private readonly Action? reserved;
    private bool original;
    private string? refusal;
    internal long Work { get; private set; }
    internal long CloneBytes { get; private set; } // Includes retained source identities and formatted placement labels.
    /// <summary>The copy of the world's own nodes, outside the allowances.</summary>
    internal long OriginalWork { get; private set; }
    internal long OriginalBytes { get; private set; }
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
        if (Exhausted) throw new InvalidDataException(refusal);
        if (count < 0 || !original && count > maximumWork - Work)
            throw Refuse($"Mission placements need more than {maximumWork:N0} units of construction work. Reduce repeated vehicle and pickup placements or simplify their templates.");
        if (original) OriginalWork += count; else Work += count;
        reserved?.Invoke(); token.ThrowIfCancellationRequested();
    }
    /// <summary>Admit the preview's copy of one of the world's own nodes, measured apart from the allowances.</summary>
    internal void Original(GameNode node)
    {
        original = true;
        try { Clone(node); } finally { original = false; }
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
        if (normalized.Length > maximumCharacters) throw Refuse("A mission resource path expands beyond the length admitted for it.");
        return normalized.ToUpperInvariant();
    }
    private void Retain(long count)
    {
        Take();
        if (count < 0 || !original && count > maximumCloneBytes - CloneBytes)
            throw Refuse($"Mission placements copy more than {Size(maximumCloneBytes)} of node data and labels. Reduce repeated vehicle and pickup placements or simplify their templates.");
        if (original) OriginalBytes += count; else CloneBytes += count;
    }
    private static string Size(long bytes) => bytes >= 1024 * 1024 && bytes % (1024 * 1024) == 0 ? $"{bytes / (1024 * 1024):N0} MiB" : $"{bytes:N0} bytes";
    private InvalidDataException Refuse(string cause)
    {
        Exhausted = true; refusal ??= cause;
        return new(refusal);
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
        if (depth > 256) throw Refuse("Mission node data nests deeper than 256 levels.");
        if (node == null) return; // Only its property or element slot.
        // Count cold containers through their raw, already-owned element; enumeration of JsonObject/JsonArray
        // would first materialize every immediate child. Never serialize a graph just to estimate its clone.
        if (node is JsonObject or JsonArray)
        {
            if (JsonData.UnderlyingElement == null) throw Refuse(Unsupported);
            if (JsonData.UnderlyingElement(node) is { } raw) { Retain(node is JsonObject ? ObjectBytes : ArrayBytes); Raw(raw, depth); return; }
        }
        switch (node)
        {
            case JsonObject obj:
                Retain(ObjectBytes);
                foreach (var (key, value) in obj) { Name(key); Retain(PropertyBytes); Json(value, depth + 1); }
                break;
            case JsonArray array:
                Retain(ArrayBytes);
                foreach (var value in array) { Retain(ElementBytes); Json(value, depth + 1); }
                break;
            case JsonValue value when value.TryGetValue(out JsonElement raw): Retain(ScalarBytes); Raw(raw, depth); break;
            case JsonValue value when value.TryGetValue(out string? text):
                Take(text?.Length ?? 0); Retain(ScalarBytes + 2L * (text?.Length ?? 0)); break;
            case JsonValue value when KnownScalar(value): Retain(ScalarBytes); break;
            default: throw Refuse(Unsupported); // Customized JsonValue serializers can retain arbitrary object graphs.
        }
    }
    private const string Unsupported = "Mission node data holds a value the preview cannot copy.";
    private void Raw(JsonElement element, int depth)
    {
        ReadOnlySpan<byte> bytes = JsonMarshal.GetRawUtf8Value(element);
        Take(bytes.Length); // Before scanning a long scalar/property token.
        Retain(bytes.Length); // DeepClone may retain a new backing JsonDocument.
        Utf8JsonReader reader = new(bytes, new JsonReaderOptions { MaxDepth = 257 });
        while (reader.Read())
        {
            Take();
            if (depth + reader.CurrentDepth > 256) throw Refuse("Mission node data nests deeper than 256 levels.");
            // Reading a cold copy materializes its values, so each token pays at least what a built value retains.
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
    /// <summary>A placement renamed an existing node: lookups of its old name find the next highest slot of that name.</summary>
    internal void Renamed(int index, string old)
    {
        var node = scene.Nodes[index];
        budget.Name(node.Name); usedNames.Add(node.Name);
        if (node.Class != "none" && (!live.TryGetValue(node.Name, out int prior) || prior < index)) live[node.Name] = index;
        budget.Name(old);
        if (live.GetValueOrDefault(old, -1) != index) return;
        budget.Take(scene.Nodes.Count); live.Remove(old);
        for (int i = scene.Nodes.Count - 1; i >= 0; i--)
            if (scene.Nodes[i].Class != "none" && scene.Nodes[i].Name == old) { live[old] = i; break; }
    }
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
