using Recoil.Zbd.Core.Formats;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core;

/// <summary>Explicit retained format families shared by preview loading and compiled edit history.</summary>
internal sealed class RetainedDocumentBudget(long maximumBytes)
{
    private readonly HashSet<object> decoded = new(ReferenceEqualityComparer.Instance);
    private readonly List<object> admitted = [];
    private readonly long capacity = maximumBytes;
    private long remaining = maximumBytes;
    internal long UsedBytes => capacity - remaining;
    internal bool Exhausted => remaining < 0;
    internal (long Remaining, int Count) Checkpoint() => (remaining, admitted.Count);
    internal void Restore((long Remaining, int Count) checkpoint)
    {
        for (int i = admitted.Count - 1; i >= checkpoint.Count; i--) decoded.Remove(admitted[i]);
        admitted.RemoveRange(checkpoint.Count, admitted.Count - checkpoint.Count);
        remaining = checkpoint.Remaining;
    }
    private void Remember(object value) { decoded.Add(value); admitted.Add(value); }
    internal void Reserve(long bytes, CancellationToken token)
    {
        Check(token);
        if (bytes < 0 || bytes > remaining) { remaining = -1; Check(token); }
        remaining -= bytes;
    }
    internal void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (remaining < 0) throw new InvalidDataException("Retained decoded content exceeds its allowance.");
    }
    internal void Bytes(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        if (bytes.IsEmpty) return;
        if (!MemoryMarshal.TryGetArray(bytes, out var segment) || segment.Array == null)
            throw new InvalidDataException("The edited content has an unsupported backing buffer.");
        First(segment.Array, 32L + segment.Array.LongLength, token);
    }
    internal void Document(ZbdDocument document, CancellationToken token)
    {
        if (!First(document, 256L + 2L * document.Path.Length, token)) return;
        Json(document.Metadata, token);
        foreach (var asset in document.Assets)
        {
            Reserve(256, token); Text(asset.Name, token); Text(asset.Summary, token); Json(asset.Metadata, token);
            if (asset.Content is ZrdNode tree) Tree(tree, token);
            else if (asset.Content is MotionClip motion && First(motion, 128L + 16L * motion.Parts.Count, token))
            {
                foreach (var part in motion.Parts)
                    if (First(part, 128, token))
                    { Text(part.Name, token); First(part.Frames, 32L + 28L * part.Frames.Count, token); }
            }
            else if (asset.Content is ScriptContent script && !decoded.Contains(script))
            {
                Reserve(128L + 8L * script.Instructions.Count + 2L * script.PreviewText.Length, token);
                Remember(script);
                foreach (var instruction in script.Instructions)
                {
                    Check(token);
                    if (decoded.Contains(instruction)) continue;
                    Reserve(32L + 8L * instruction.Length, token);
                    Remember(instruction);
                    foreach (string text in instruction) Text(text, token);
                }
            }
        }
        if (document.Scene is { } scene) Scene(scene, token);
        if (document.Animations is { } animation && First(animation, 128L + 16L * animation.Entries.Count, token))
        {
            First(animation.Prefix, 32L + animation.Prefix.Length, token); First(animation.Tail, 32L + animation.Tail.Length, token);
            foreach (var entry in animation.Entries)
            {
                Record(entry);
                First(entry.OriginalPrimaryHeader, 32L + entry.OriginalPrimaryHeader.Length, token);
                Reserve(16L * (entry.Sequences.Count + entry.Puffers.Count + entry.References.Length), token);
                foreach (var lane in entry.References)
                { Reserve(16L * lane.Count, token); foreach (var record in lane) Record(record); }
                foreach (var record in entry.Puffers) Record(record);
                Sequence(entry.Primary);
                foreach (var sequence in entry.Sequences) Sequence(sequence);
            }
        }
        if (document.Scripts is { } package) Script(package, token);
        foreach (var diagnostic in document.Diagnostics) Reserve(128L + 2L * diagnostic.Message.Length, token);
        // Individual readers bound a cold parse (64 MiB ZRD / 128 MiB script). Admit its actual retained
        // decoded content before accumulating it beside prior documents; never clone or format it to count it.
        void Record(Animation.AnimationRecord record)
        {
            if (!First(record, 128, token)) return;
            // Keyframe events can also retain frame wrappers/copies. Their smallest serialized record is
            // twelve bytes; this conservative allowance includes those caches without forcing their creation.
            First(record.Bytes, 32L + (record is Animation.AnimationEvent ? 16L : 1L) * record.Bytes.Length, token);
        }
        void Sequence(Animation.AnimationSequence sequence)
        {
            Record(sequence); Reserve(16L * sequence.Events.Count, token);
            First(sequence.OpaqueTail, 32L + sequence.OpaqueTail.Length, token);
            foreach (var ev in sequence.Events) Record(ev);
        }
    }

    internal void Script(PreparedScriptPackage package, CancellationToken token)
    {
        if (First(package, 128L + 16L * package.Entries.Count, token))
            foreach (var entry in package.Entries)
                if (First(entry, 192L + 16L * entry.Instructions.Count, token))
                {
                    Text(entry.Name, token);
                    foreach (var instruction in entry.Instructions)
                        if (First(instruction, 128L + 8L * instruction.Tokens.Count, token))
                            foreach (string text in instruction.Tokens) Text(text, token);
                }
    }
    /// <summary>Animation editor ownership: opaque payloads are bytes, not hypothetical decoded keyframes.</summary>
    internal void AnimationEntry(Animation.AnimationEntry entry, CancellationToken token)
    {
        if (!First(entry, 256L + 16L * (entry.Sequences.Count + entry.Puffers.Count + entry.References.Length), token)) return;
        Bytes(entry.Bytes, token); Bytes(entry.OriginalPrimaryHeader, token);
        foreach (var lane in entry.References)
        {
            if (!First(lane, 32L + 16L * lane.Count, token)) continue;
            foreach (var record in lane) Record(record);
        }
        foreach (var record in entry.Puffers) Record(record);
        Sequence(entry.Primary);
        foreach (var sequence in entry.Sequences) Sequence(sequence);

        void Record(Animation.AnimationRecord record)
        { if (First(record, 128, token)) Bytes(record.Bytes, token); }
        void Sequence(Animation.AnimationSequence sequence)
        {
            if (!First(sequence, 192L + 16L * sequence.Events.Count, token)) return;
            Bytes(sequence.Bytes, token); Bytes(sequence.OpaqueTail, token);
            foreach (var ev in sequence.Events)
                if (First(ev, 192, token)) { Bytes(ev.Bytes, token); ev.RetainedCaches(this, token); }
        }
    }
    internal bool Object(object value, long bytes, CancellationToken token) => First(value, bytes, token);
    private bool First(object value, long bytes, CancellationToken token)
    {
        Check(token);
        if (decoded.Contains(value)) return false;
        Reserve(bytes, token); Remember(value); return true;
    }
    private void Scene(GameScene scene, CancellationToken token)
    {
        if (!First(scene, 128L + 16L * (scene.Nodes.Count + (long)scene.Models.Count + scene.Materials.Count + scene.Textures.Count), token)) return;
        foreach (var item in scene.Materials) Json(item, token);
        foreach (var item in scene.Textures) Json(item, token);
        foreach (var node in scene.Nodes)
            if (First(node, 128, token))
            {
                Text(node.Name, token); Text(node.Class, token); Json(node.Metadata, token); Json(node.Data, token);
                First(node.Parents, 32L + 4L * node.Parents.Length, token); First(node.Children, 32L + 4L * node.Children.Length, token);
            }
        foreach (var model in scene.Models)
            if (First(model, 128, token))
            {
                Json(model.Metadata, token);
                First(model.Vertices, 32L + 12L * model.Vertices.Length, token);
                First(model.Normals, 32L + 12L * model.Normals.Length, token);
                First(model.Morphs, 32L + 12L * model.Morphs.Length, token);
                if (!First(model.Polygons, 32L + 8L * model.Polygons.Length, token)) continue;
                foreach (var polygon in model.Polygons)
                    if (First(polygon, 128, token))
                    {
                        Json(polygon.Metadata, token);
                        First(polygon.Vertices, 32L + 4L * polygon.Vertices.Length, token);
                        First(polygon.Normals, 32L + 4L * polygon.Normals.Length, token);
                        First(polygon.Uvs, 32L + 8L * polygon.Uvs.Length, token);
                        First(polygon.Colors, 32L + 12L * polygon.Colors.Length, token);
                    }
            }
    }
    private void Json(JsonNode? node, CancellationToken token, int depth = 0)
    {
        Check(token);
        if (node == null || !First(node, 192, token)) return;
        if (depth > 512) { remaining = -1; Check(token); }
        if (node is JsonObject or JsonArray)
        {
            // Inspect a cold container's encoded tokens before enumeration could hydrate its children.
            if (JsonData.UnderlyingElement == null) { remaining = -1; Check(token); }
            if (JsonData.UnderlyingElement!(node) is { } raw) { Raw(raw, token, depth); return; }
        }
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj) { Reserve(64, token); Text(key, token); Json(value, token, depth + 1); }
                break;
            case JsonArray array:
                Reserve(16L * array.Count, token);
                foreach (var value in array) Json(value, token, depth + 1);
                break;
            case JsonValue value when value.TryGetValue(out JsonElement raw): Raw(raw, token, depth); break;
            case JsonValue value when value.TryGetValue(out string? text): if (text != null) Text(text, token); break;
            case JsonValue value when KnownScalar(value): break;
            default: remaining = -1; Check(token); break;
        }
    }
    private void Raw(JsonElement element, CancellationToken token, int depth)
    {
        ReadOnlySpan<byte> bytes = JsonMarshal.GetRawUtf8Value(element);
        Reserve(bytes.Length, token);
        Utf8JsonReader reader = new(bytes, new JsonReaderOptions { MaxDepth = 512 });
        while (reader.Read())
        {
            if (depth + reader.CurrentDepth > 512) { remaining = -1; Check(token); }
            Reserve(256L + 2L * reader.ValueSpan.Length, token);
        }
    }
    private static bool KnownScalar(JsonValue value) => value.TryGetValue(out bool _) || value.TryGetValue(out byte _) ||
        value.TryGetValue(out sbyte _) || value.TryGetValue(out short _) || value.TryGetValue(out ushort _) ||
        value.TryGetValue(out int _) || value.TryGetValue(out uint _) || value.TryGetValue(out long _) ||
        value.TryGetValue(out ulong _) || value.TryGetValue(out float _) || value.TryGetValue(out double _) ||
        value.TryGetValue(out decimal _) || value.TryGetValue(out char _) || value.TryGetValue(out Guid _) ||
        value.TryGetValue(out DateTime _) || value.TryGetValue(out DateTimeOffset _);

    internal void Tree(ZrdNode node, CancellationToken token, int depth = 0)
    {
        Check(token);
        if (decoded.Contains(node)) return;
        if (depth > 256) { remaining = -1; Check(token); }
        Reserve(256L + 8L * node.Children.Count, token);
        Remember(node);
        Text(node.Text, token);
        foreach (var child in node.Children) Tree(child, token, depth + 1);
    }
    internal void Text(string text, CancellationToken token)
    {
        Check(token);
        if (decoded.Contains(text)) return;
        Reserve(64L + 2L * text.Length, token); Remember(text);
    }
}
