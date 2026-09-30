namespace Recoil.Zbd.Core.Animation;

/// <summary>
/// Compares animation entries by their authored content: names as text (the original compiler left residue after
/// terminators), header settings, reference tables, sequences and events. Stored pointers, runtime state and the
/// unused pad floats of keyframes are not compared.
/// </summary>
public static class AnimationComparer
{
    private static readonly (int From, int To)[] HeaderRanges = [(148, 176), (260, 272)];
    // Bytes compared per table after the name: light/sound attachment, prerequisite kind, child cleanup.
    private static readonly int[][] TableRanges = [[], [], [40, 44], [40, 44], [], [], [0, 8], [64, 68]];
    private static readonly int[] NameSizes = [36, 36, 36, 36, 32, 32, 0, 32];

    /// <summary>The first authored difference between <paramref name="expected"/> and <paramref name="actual"/>, or null.</summary>
    public static string? Difference(AnimationEntry expected, AnimationEntry actual)
    {
        var a = expected; var b = actual;
        if (a.Name != b.Name) return $"name {a.Name}|{b.Name}";
        if (a.RootName != b.RootName || a.AttachName != b.AttachName) return $"root {a.RootName}/{a.AttachName}|{b.RootName}/{b.AttachName}";
        foreach (var (from, to) in HeaderRanges)
            for (int o = from; o < to; o++) if (a.Bytes[o] != b.Bytes[o]) return $"header@{o} {Hex(a.Bytes, o)}|{Hex(b.Bytes, o)}";
        for (int t = 0; t < 8; t++)
        {
            if (a.References[t].Count != b.References[t].Count) return $"T{t}.count {a.References[t].Count}|{b.References[t].Count}";
            for (int r = 0; r < a.References[t].Count; r++)
            {
                var ra = a.References[t][r]; var rb = b.References[t][r];
                if (NameSizes[t] > 0 && ra.Text(0, NameSizes[t]) != rb.Text(0, NameSizes[t])) return $"T{t}[{r}].name {ra.Text(0, NameSizes[t])}|{rb.Text(0, NameSizes[t])}";
                if (t == 6 && (ra.Bytes[4] == 1 ? ra.Text(8, 32) != rb.Text(8, 32) : ra.Text(12, 28) != rb.Text(12, 28))) return $"T6[{r}].target {ra.Text(12, 28)}|{rb.Text(12, 28)}";
                if (t == 7 && ra.Text(32, 32) != rb.Text(32, 32)) return $"T7[{r}].local {ra.Text(32, 32)}|{rb.Text(32, 32)}";
                var ranges = TableRanges[t];
                for (int k = 0; k < ranges.Length; k += 2)
                    for (int o = ranges[k]; o < ranges[k + 1]; o++)
                        if (ra.Bytes[o] != rb.Bytes[o]) return $"T{t}[{r}]@{o} {Hex(ra.Bytes, o)}|{Hex(rb.Bytes, o)}";
            }
        }
        var sa = a.AllSequences.ToList(); var sb = b.AllSequences.ToList();
        if (sa.Count != sb.Count) return $"sequences {sa.Count}|{sb.Count}";
        for (int s = 0; s < sa.Count; s++)
        {
            if (sa[s].Name != sb[s].Name) return $"sequence name {sa[s].Name}|{sb[s].Name}";
            for (int o = 32; o < 56; o++) if (sa[s].Bytes[o] != sb[s].Bytes[o]) return $"sequence {sa[s].Name} header@{o}";
            if (sa[s].Events.Count != sb[s].Events.Count) return $"sequence {sa[s].Name} events {string.Join(",", sa[s].Events.Select(e => e.Type))}|{string.Join(",", sb[s].Events.Select(e => e.Type))}";
            for (int e = 0; e < sa[s].Events.Count; e++)
                if (Event(sa[s].Events[e], sb[s].Events[e]) is { } diff) return $"sequence {sa[s].Name} event {e}: {diff}";
        }
        return null;
    }

    private static string? Event(AnimationEvent a, AnimationEvent b)
    {
        if (a.Type != b.Type) return $"type {a.Type}|{b.Type}";
        if (a.Bytes.Length != b.Bytes.Length) return $"size {a.Bytes.Length}|{b.Bytes.Length}";
        HashSet<int> skip = [];
        foreach (var f in a.Spec?.Fields ?? [])
        {
            if (f.Offset + f.Size > a.Bytes.Length) continue;
            if (f.Kind == AnimationFieldKind.Text && a.Text(f.Offset, f.Size) != b.Text(f.Offset, f.Size)) return $"{f.Name} {a.Text(f.Offset, f.Size)}|{b.Text(f.Offset, f.Size)}";
            if (f.ReadOnly || f.Kind == AnimationFieldKind.Text) for (int o = f.Offset; o < f.Offset + f.Size; o++) skip.Add(o);
        }
        if (a.Type == 12)
        {
            for (int o = 20; o < 32; o++) skip.Add(o);
            // Position and scale pad floats hold leftover memory.
            for (int at = 32; at + 12 <= a.Bytes.Length;)
            {
                int flags = BitConverter.ToInt32(a.Bytes, at); int offset = at + 12;
                if ((flags & ~7) != 0) break;
                for (int c = 0; c < 3; c++) if ((flags & (1 << c)) != 0) { if (c != 1) for (int o = offset + 12; o < offset + 16; o++) skip.Add(o); offset += 28; }
                at = offset;
            }
        }
        for (int o = 0; o < a.Bytes.Length; o++)
            if (!skip.Contains(o) && a.Bytes[o] != b.Bytes[o])
                return $"{a.Spec?.Fields.FirstOrDefault(f => o >= f.Offset && o < f.Offset + f.Size)?.Name ?? (o < 12 ? "timing" : $"byte {o}")} {Hex(a.Bytes, o)}|{Hex(b.Bytes, o)}";
        return null;
    }

    private static string Hex(byte[] bytes, int offset) { int at = offset & ~3; return Convert.ToHexString(bytes.AsSpan(at, Math.Min(4, bytes.Length - at))); }
}
