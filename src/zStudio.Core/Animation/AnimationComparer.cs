namespace zStudio.Core.Animation;

/// <summary>
/// Compares animation entries by their authored content: names as text (the original compiler left residue after
/// terminators), header settings, reference tables, sequences and events. Stored pointers, runtime state and the
/// unused pad floats of keyframes are not compared.
/// </summary>
public static class AnimationComparer
{
    // 176 is the countdown the game starts from (it equals the health at 172 in every shipped entry), not runtime state.
    private static readonly (int From, int To)[] HeaderRanges = [(148, 180), (260, 272)];
    // Bytes compared per table after the name: light/sound attachment, prerequisite kind, child cleanup.
    private static readonly int[][] TableRanges = [[], [], [40, 44], [40, 44], [], [], [0, 8], [64, 68]];
    private static readonly int[] NameSizes = [36, 36, 36, 36, 32, 32, 0, 32];

    /// <summary>The first authored difference between <paramref name="expected"/> and <paramref name="actual"/>, or null.</summary>
    public static string? Difference(AnimationEntry expected, AnimationEntry actual, CancellationToken token = default, bool allowMw3ArithmeticRounding = false)
    {
        token.ThrowIfCancellationRequested();
        var a = expected; var b = actual;
        if (a.Version != b.Version) return $"version {a.Version}|{b.Version}";
        if (a.Name != b.Name) return $"name {a.Name}|{b.Name}";
        if (a.RootName != b.RootName || a.AttachName != b.AttachName) return $"root {a.RootName}/{a.AttachName}|{b.RootName}/{b.AttachName}";
        // A format-200 entry keeps format 28's settings and counts, the counts 32-bit: they compare as numbers, so an entry
        // compares with its twin of the other format too.
        bool wide = a.Format == AnimationPackage.WideFormat || b.Format == AnimationPackage.WideFormat;
        if (wide && (a.Version != 28 || b.Version != 28)) return $"format {a.Format}|{b.Format}";
        var headerRanges = a.Version == 39 ? [(148, 180), (264, 276)] : wide ? HeaderRanges[..1] : HeaderRanges;
        foreach (var (from, to) in headerRanges)
            for (int o = from; o < to; o++) if (a.Bytes[o] != b.Bytes[o]) return $"header@{o} {Hex(a.Bytes, o)}|{Hex(b.Bytes, o)}";
        if (wide)
            for (int lane = 0; lane < 10; lane++)
                if (a.StoredCount(lane) != b.StoredCount(lane)) return $"header count {lane} {a.StoredCount(lane)}|{b.StoredCount(lane)}";
        for (int t = 0; t < 8; t++)
        {
            if (a.References[t].Count != b.References[t].Count) return $"T{t}.count {a.References[t].Count}|{b.References[t].Count}";
            for (int r = 0; r < a.References[t].Count; r++)
            {
                token.ThrowIfCancellationRequested();
                var ra = a.References[t][r]; var rb = b.References[t][r];
                if (NameSizes[t] > 0 && ra.Text(0, NameSizes[t]) != rb.Text(0, NameSizes[t])) return $"T{t}[{r}].name {ra.Text(0, NameSizes[t])}|{rb.Text(0, NameSizes[t])}";
                if (t == 6)
                {
                    var (at, size) = ra.Bytes[4] == 1 ? (8, 32) : (12, 28);
                    if (ra.Text(at, size) != rb.Text(at, size)) return $"T6[{r}].target {ra.Text(at, size)}|{rb.Text(at, size)}";
                }
                if (t == 7 && ra.Text(32, 32) != rb.Text(32, 32)) return $"T7[{r}].local {ra.Text(32, 32)}|{rb.Text(32, 32)}";
                var ranges = TableRanges[t];
                for (int k = 0; k < ranges.Length; k += 2)
                    for (int o = ranges[k]; o < ranges[k + 1]; o++)
                        if (ra.Bytes[o] != rb.Bytes[o]) return $"T{t}[{r}]@{o} {Hex(ra.Bytes, o)}|{Hex(rb.Bytes, o)}";
                // Mode 2's expected active bit is authored data, not a pointer or unused name prefix.
                // Modes 1/3 use this area for an animation name / unused traversal operand respectively.
                if (t == 6 && ra.Bytes[4] == 2 && ra.I32(8) != rb.I32(8))
                    return $"T6[{r}].expected state {ra.I32(8)}|{rb.I32(8)}";
            }
        }
        if (a.Puffers.Count != b.Puffers.Count) return $"puffer count {a.Puffers.Count}|{b.Puffers.Count}";
        for (int i = 0; i < a.Puffers.Count; i++)
            if (a.Puffers[i].Text(0, 36) != b.Puffers[i].Text(0, 36) || a.Puffers[i].I32(40) != b.Puffers[i].I32(40))
                return $"puffer {i}";
        var sa = a.AllSequences.ToList(); var sb = b.AllSequences.ToList();
        if (sa.Count != sb.Count) return $"sequences {sa.Count}|{sb.Count}";
        for (int s = 0; s < sa.Count; s++)
        {
            token.ThrowIfCancellationRequested();
            if (sa[s].Name != sb[s].Name) return $"sequence name {sa[s].Name}|{sb[s].Name}";
            for (int o = 32; o < 56; o++) if (sa[s].Bytes[o] != sb[s].Bytes[o]) return $"sequence {sa[s].Name} header@{o}";
            if (sa[s].Events.Count != sb[s].Events.Count) return $"sequence {sa[s].Name} event count {sa[s].Events.Count}|{sb[s].Events.Count}";
            for (int e = 0; e < sa[s].Events.Count; e++)
            {
                token.ThrowIfCancellationRequested();
                if (Event(sa[s].Events[e], sb[s].Events[e], token, allowMw3ArithmeticRounding) is { } diff) return $"sequence {sa[s].Name} event {e}: {diff}";
            }
        }
        return null;
    }

    private static string? Event(AnimationEvent a, AnimationEvent b, CancellationToken token, bool allowMw3ArithmeticRounding)
    {
        if (a.Type != b.Type) return $"type {a.Type}|{b.Type}";
        if (a.Bytes.Length != b.Bytes.Length) return $"size {a.Bytes.Length}|{b.Bytes.Length}";
        HashSet<int> skip = [];
        foreach (var f in a.Spec?.Fields ?? [])
        {
            if (f.Offset + f.Size > a.Bytes.Length) continue;
            if (f.Kind == AnimationFieldKind.Text && a.Text(f.Offset, f.Size) != b.Text(f.Offset, f.Size)) return $"{f.Name} {a.Text(f.Offset, f.Size)}|{b.Text(f.Offset, f.Size)}";
            if (f.ReadOnly || f.Kind == AnimationFieldKind.Text) for (int o = f.Offset; o < f.Offset + f.Size; o++) skip.Add(o);
            if (allowMw3ArithmeticRounding && a.Version == 39 && f.Kind is AnimationFieldKind.Float or AnimationFieldKind.Vector)
                for (int o = f.Offset; o + 4 <= f.Offset + f.Size; o += 4)
                    if (Close(a.F32(o), b.F32(o))) for (int j = 0; j < 4; j++) skip.Add(o + j);
        }
        if (a.Type == 12)
        {
            if (a.Version == 39 && (a.I32(16) != b.I32(16) || a.I32(20) != b.I32(20))) return "keyframe count or interpolation mode";
            for (int o = a.Version == 39 ? 24 : 20; o < (a.Version == 39 ? 36 : 32); o++) skip.Add(o);
            // Position and scale pad floats hold leftover memory.
            for (int at = a.Version == 39 ? 36 : 32; at + 12 <= a.Bytes.Length;)
            {
                token.ThrowIfCancellationRequested();
                int flags = BitConverter.ToInt32(a.Bytes, at); int offset = at + 12;
                if ((flags & ~7) != 0) break;
                for (int c = 0; c < 3; c++) if ((flags & (1 << c)) != 0)
                {
                    if (c != 1) for (int o = offset + 12; o < offset + 16; o++) skip.Add(o);
                    if (a.Version == 39 && a.I32(20) == 0) for (int o = offset + 28; o < offset + 76; o++) skip.Add(o);
                    offset += a.Version == 39 ? 76 : 28;
                }
                at = offset;
            }
        }
        for (int o = 0; o < a.Bytes.Length; o++)
        {
            if ((o & 4095) == 0) token.ThrowIfCancellationRequested();
            if (!skip.Contains(o) && a.Bytes[o] != b.Bytes[o])
                return $"{a.Spec?.Fields.FirstOrDefault(f => o >= f.Offset && o < f.Offset + f.Size)?.Name ?? (o < 12 ? "timing" : $"byte {o}")} {Hex(a.Bytes, o)}|{Hex(b.Bytes, o)}";
        }
        return null;
    }

    private static string Hex(byte[] bytes, int offset) { int at = offset & ~3; return Convert.ToHexString(bytes.AsSpan(at, Math.Min(4, bytes.Length - at))); }
    private static bool Close(float a, float b)
    {
        if (a == b) return true;
        if (float.IsFinite(a) && float.IsFinite(b) && Math.Abs((double)a - b) <= 2e-6 * Math.Max(1, Math.Max(Math.Abs(a), Math.Abs(b)))) return true;
        if (!float.IsFinite(a) || !float.IsFinite(b) || MathF.Sign(a) != MathF.Sign(b)) return false;
        return Math.Abs((long)BitConverter.SingleToInt32Bits(a) - BitConverter.SingleToInt32Bits(b)) <= 4;
    }
}
