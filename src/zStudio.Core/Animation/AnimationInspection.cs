using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Animation;

/// <summary>Bound before expanding authored collections or payloads. Complete data remains in JSON export.</summary>
public static class AnimationInspection
{
    public static JsonObject ToPreviewJson(this AnimationEntry entry, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return new()
        {
            ["name"] = entry.Name, ["root"] = entry.RootName, ["attachment"] = entry.AttachName, ["index"] = entry.Index,
            ["source_offset"] = entry.SourceOffset, ["source_length"] = entry.SourceLength,
            ["header_hex"] = Convert.ToHexStringLower(entry.Bytes),
            ["reference_counts"] = JsonData.Array(entry.References, r => JsonValue.Create(r.Count), token),
            ["references"] = JsonData.Array(entry.References, r => JsonData.Array(r.Take(4), v => JsonValue.Create(JsonData.Hex(v.Bytes, token)), token), token),
            ["references_truncated"] = entry.References.Any(r => r.Count > 4),
            ["puffer_reference_count"] = entry.Puffers.Count,
            ["puffer_references"] = JsonData.Array(entry.Puffers.Take(4), r => JsonValue.Create(JsonData.Hex(r.Bytes, token)), token),
            ["puffer_references_truncated"] = entry.Puffers.Count > 4,
            ["sequence_count"] = entry.Sequences.Count + 1,
            ["sequences"] = JsonData.Array(entry.AllSequences.Take(16), s => SequenceSummary(s, s == entry.Primary ? "reset_stop" : "runtime", token), token),
            ["sequences_truncated"] = entry.Sequences.Count + 1 > 16,
            ["inspection_note"] = "Bounded inspection. Use animation_records/references or Properties for individual records; JSON export retains complete payloads and keyframes."
        };
    }

    public static JsonObject ToPreviewJson(this AnimationSequence sequence, CancellationToken token = default)
    {
        var result = SequenceSummary(sequence, null, token);
        result["events"] = JsonData.Array(sequence.Events.Take(8), e => e.ToPreviewJson(token), token);
        result["events_truncated"] = sequence.Events.Count > 8;
        return result;
    }

    private static JsonObject SequenceSummary(AnimationSequence sequence, string? phase, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return new()
        {
            ["name"] = sequence.Name, ["id"] = sequence.Id.ToString(), ["phase"] = phase,
            ["reset_state"] = sequence.ResetMode, ["source_offset"] = sequence.SourceOffset,
            ["header_hex"] = Convert.ToHexStringLower(sequence.Bytes), ["event_count"] = sequence.Events.Count,
            ["opaque_tail_size"] = sequence.OpaqueTail.Length,
            ["opaque_tail_hex"] = Convert.ToHexStringLower(sequence.OpaqueTail.AsSpan(0, Math.Min(256, sequence.OpaqueTail.Length))),
            ["opaque_tail_hex_truncated"] = sequence.OpaqueTail.Length > 256
        };
    }
}
