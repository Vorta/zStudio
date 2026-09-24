using System.Globalization;
using System.IO;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

/// <summary>Read-only descriptions of authored instructions, not predicted dispatch times or runtime bindings.</summary>
internal sealed record AnimationEventPresentation(string Summary, string Timing, string Description)
{
    public static AnimationEventPresentation Create(AnimationEntry entry, AnimationEvent ev)
    {
        if (ev.Bytes.Length < 12) return new("Truncated event header", "Timing unavailable", "The stored event header is incomplete.");
        string timing = TimingText(ev);
        var spec = ev.Spec;
        string support = spec?.Support ?? "Unsupported: unknown event";
        string description = $"{ev.Name}\n{timing}\nStored start threshold, not duration or observed dispatch time. Events execute in list order; earlier events can delay this event. See Dispatch / Event log for observed times.\nPreview: {support}";
        if (spec == null) return new("Unknown payload · inspect source bytes", timing + " · Unsupported", description);
        if (ev.Bytes.Length < spec.Size) return new($"Incomplete record: {ev.Bytes.Length} of {spec.Size} bytes", timing + " · Unavailable", description);

        // Select the most useful stored operands, keeping the complete catalog in the tooltip.
        // Use the catalog's widths (notably 20 vs 32 byte animation names and I16 references).
        int[] offsets = ev.Type switch
        {
            1 or 3 => [12], 2 => [12, 52], 4 or 5 => [12, 44],
            6 => [16, 12], 7 or 9 => [28, 16], 8 => [24, 12],
            10 or 11 => [16, 12], 12 => [12], 13 => [20, 12], 14 => [12, 20, 24],
            15 or 16 => [12, 14], 17 => [16, 18], 18 => [16],
            19 => [16], 20 or 21 => [16, 12], 22 or 23 or 24 or 25 or 26 or 27 => [12],
            28 => [48, 52], 30 => [], 31 or 33 => [12], 35 or 38 or 39 => [12],
            _ => []
        };
        var parts = offsets.Select(offset => spec.Fields.First(f => f.Offset == offset))
            .Select(field => FormatField(entry, ev, field)).ToList();
        if (ev.Type == 24 && (ev.I16(46) & 16) != 0) parts.Add("Wait for child to finish");
        if (ev.Type == 30) parts.Add(LoopText(ev));
        if (ev.Type is 31 or 33)
        {
            uint flags = ev.U32(12);
            // Match handler precedence; integer effects level shares storage with float thresholds.
            if ((flags & 1) != 0) parts.Add($"Random threshold: {Number(ev.F32(20))}");
            else if ((flags & 2) != 0) parts.Add($"Squared distance ≤ {Number(ev.F32(20))}");
            else if ((flags & 0x18) == 0 && (flags & 4) != 0) parts.Add($"Effects level ≥ {ev.I32(20)}");
        }
        if (spec.DurationOffset >= 0 && (ev.Type != 10 || (ev.U32(12) & 0x400) != 0))
            parts.Add($"Duration: {Number(ev.F32(spec.DurationOffset))} s");
        if (ev.Type == 12)
        {
            try
            {
                var frames = ev.Keyframes();
                parts.Add($"{frames.Count} keyframe segment{(frames.Count == 1 ? "" : "s")}");
                if (frames.Count > 0) parts.Add($"Last segment ends: {Number(frames[^1].End)} s");
            }
            catch (InvalidDataException) { parts.Add("Malformed keyframe data"); }
        }
        description += "\n\nStored parameters:\n" + string.Join("\n", spec.Fields.Select(field => FormatField(entry, ev, field) + (field.ReadOnly ? " (serialized / read-only)" : "")));
        string qualification = support.StartsWith("Approximate:", StringComparison.Ordinal) ? " · Approximate preview"
            : support.StartsWith("Not executed:", StringComparison.Ordinal) ? " · Not executed in preview"
            : support != "Engine-based" ? " · Preview limits" : "";
        return new(string.Join(" · ", parts), timing + qualification, description);
    }

    public static string ResetState(byte state) => state switch { 0 => "Ready", 1 => "Running", 2 => "Stopped", 3 => "Waiting for release", _ => $"Unknown ({state})" };

    private static string TimingText(AnimationEvent ev)
    {
        string clock = ev.StartMode switch { 1 => "Animation time", 2 => "Sequence time", 3 => "After previous event", _ => $"Unknown clock ({ev.StartMode})" };
        return $"{clock} ≥ {Number(ev.Threshold)} s";
    }
    private static string FormatField(AnimationEntry entry, AnimationEvent ev, AnimationField field)
    {
        string value;
        if (field.ReferenceTable >= 0)
            value = Reference(entry, field.ReferenceTable, field.Kind == AnimationFieldKind.Short ? ev.I16(field.Offset) : ev.I32(field.Offset));
        else if (field.Kind == AnimationFieldKind.Text)
            value = Name(ev.Text(field.Offset, field.Size));
        else if (AnimationFieldPresentation.Flags(ev.Type, field.Offset) is { Length: > 0 } flags)
        {
            uint bits = ev.U32(field.Offset);
            var names = flags.Where(f => (bits & f.Bit) != 0).Select(f => f.Label).ToList();
            uint unknown = bits & ~flags.Aggregate(0u, (mask, flag) => mask | flag.Bit);
            if (unknown != 0) names.Add($"unknown 0x{unknown:X}");
            value = names.Count > 0 ? string.Join(", ", names) : "None";
        }
        else value = field.Format(ev);
        return $"{field.Name}: {value}";
    }
    private static string Reference(AnimationEntry entry, int table, int index)
    {
        if (table == 1 && index == -100) return "bound root (−100)";
        if (table == 1 && index == -200) return "activation reference (−200; root in preview)";
        if (index == 0) return "none / reserved (#0)";
        if (index < 0 || index >= entry.References[table].Count) return $"missing reference #{index}";
        var record = entry.References[table][index];
        if (record.Bytes.Length < 32) return $"incomplete reference #{index}";
        return $"{Name(record.Text(0))} (#{index})";
    }
    private static string LoopText(AnimationEvent ev)
    {
        uint mode = ev.U32(12);
        uint count = ev.U32(16) & 65535;
        if ((mode & 1) != 0) return count == 65535 ? "Iterations: unlimited" : $"Iteration limit: {count}";
        if ((mode & 2) != 0) return ev.F32(16) < 0 ? "Elapsed-time limit: unlimited" : $"Elapsed-time limit: {Number(ev.F32(16))} s";
        return $"Stop mode: 0x{mode:X} · Count / time bits: 0x{ev.U32(16):X8}";
    }
    private static string Number(float value) => value.ToString("R", CultureInfo.InvariantCulture);
    private static string Name(string text) => text.Length == 0 ? "(empty)" : string.Concat(text.Select(c => char.IsControl(c) ? $"\\x{(int)c:X2}" : c.ToString()));
}
