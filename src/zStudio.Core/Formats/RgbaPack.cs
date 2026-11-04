using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace zStudio.Core.Formats;

/// <summary>What a 32-bit pack holds (its header's kind word).</summary>
public enum RgbaPackKind : uint
{
    /// <summary><c>rgbatexture&lt;N&gt;.zbd</c>: a mission's textures, tiered by texture memory.</summary>
    MissionTextures = 1,
    /// <summary><c>rgbaimage.zbd</c>: the interface images, at their authored size.</summary>
    InterfaceImages = 2,
}

/// <summary>
/// The 32-bit texture and image packs zStudio writes for reRECOIL v2 beside the 16-bit packs (specification:
/// docs/rgba-packs.md). Little-endian throughout, without padding: a 24-byte header (magic <c>RCLRGBA</c> and NUL, version,
/// kind, budget in MB, record count), a record table (name length, Latin-1 name, width, height, addressing word, data offset
/// and size), then each record's RGBA8 pixels, rows top to bottom, in record order, the file ending at the last pixel. The
/// retail game opens no file of these names, so it never reads them.
/// </summary>
public static class RgbaPack
{
    public const uint Version = 1;
    /// <summary>The header: magic (8 bytes), version, kind, budget and record count (four bytes each).</summary>
    public const int HeaderBytes = 24;
    /// <summary>A record without its name: name length (2), width, height, addressing (4 each), data offset and size (8 each).</summary>
    public const int RecordFixedBytes = 30;
    public const int MaximumNameBytes = 255, MaximumSide = 16384;
    /// <summary>The addressing bits a record may set: bit 0 clamps U, bit 1 clamps V (the 16-bit image header's word).</summary>
    public const uint AddressingBits = 3;
    private const string Format = "32-bit pack";

    /// <summary>The first eight bytes of every 32-bit pack.</summary>
    public static ReadOnlySpan<byte> Magic => "RCLRGBA\0"u8;
    /// <summary>Whether <paramref name="bytes"/> starts with the 32-bit packs' magic.</summary>
    public static bool HasMagic(ReadOnlySpan<byte> bytes) => bytes.Length >= Magic.Length && bytes[..Magic.Length].SequenceEqual(Magic);
    /// <summary>What a texture stored at <paramref name="width"/> × <paramref name="height"/> takes of a 32-bit pack and its budget: four bytes a texel.</summary>
    public static long TexelBytes(int width, int height) => 4L * width * height;

    /// <summary>Refuses a name the format cannot store: 1–255 Latin-1 characters without NUL.</summary>
    public static void ValidateName(string name)
    {
        if (name.Length is < 1 or > MaximumNameBytes || name.Any(c => c == 0 || c > 255))
            throw new InvalidDataException($"{JsonData.ShownText(name, 64)}: a {Format} names a texture in 1–{MaximumNameBytes} Latin-1 characters without NUL.");
    }

    /// <summary>
    /// The probe of a file starting with the 32-bit packs' magic: supported for version 1 of a known kind when its record table
    /// can fit the file, an unsupported version or malformed otherwise (its bytes stay inspectable).
    /// </summary>
    internal static FormatProbe Probe(ReadOnlySpan<byte> prefix, long size)
    {
        if (prefix.Length < HeaderBytes || size < HeaderBytes) return new(FormatFamily.TexturePack, null, Recognition.Malformed, $"Truncated {Format} header");
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(prefix[8..]);
        if (version != Version) return new(FormatFamily.TexturePack, version, Recognition.UnsupportedVersion, $"Unsupported {Format} version {version} (expected {Version})");
        uint kind = BinaryPrimitives.ReadUInt32LittleEndian(prefix[12..]), records = BinaryPrimitives.ReadUInt32LittleEndian(prefix[20..]);
        bool valid = kind is (uint)RgbaPackKind.MissionTextures or (uint)RgbaPackKind.InterfaceImages && HeaderBytes + (long)records * (RecordFixedBytes + 1) <= size;
        string what = kind == (uint)RgbaPackKind.InterfaceImages ? $"32-bit image pack · {records:N0} images" : $"32-bit texture pack · {records:N0} textures";
        return new(FormatFamily.TexturePack, version, valid ? Recognition.Supported : Recognition.Malformed,
            valid ? what : kind is not ((uint)RgbaPackKind.MissionTextures or (uint)RgbaPackKind.InterfaceImages) ? $"Unknown {Format} kind {kind}" : $"The {Format}'s record table exceeds the file length");
    }

    /// <summary>One record of a pack being written: its name, addressing word and pixels at the size stored.</summary>
    internal sealed record Entry(string Name, uint Addressing, int Width, int Height);

    /// <summary>The file's size for these records: the header, the record table and four bytes a texel.</summary>
    internal static long FileBytes(IReadOnlyList<Entry> entries)
    {
        long total = HeaderBytes;
        foreach (var entry in entries) total += RecordFixedBytes + entry.Name.Length + TexelBytes(entry.Width, entry.Height);
        return total;
    }

    /// <summary>
    /// Writes the header and record table of a pack holding <paramref name="entries"/> into <paramref name="file"/> (exactly
    /// <see cref="FileBytes"/> long) and returns where each record's pixels start; the caller fills them in record order.
    /// </summary>
    internal static long[] WriteTables(byte[] file, RgbaPackKind kind, uint budgetMegabytes, IReadOnlyList<Entry> entries)
    {
        Span<byte> span = file;
        Magic.CopyTo(span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Version);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], (uint)kind);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], budgetMegabytes);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], checked((uint)entries.Count));
        long table = HeaderBytes;
        foreach (var entry in entries) table += RecordFixedBytes + entry.Name.Length;
        long[] offsets = new long[entries.Count];
        long data = table; int at = HeaderBytes;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if ((entry.Addressing & ~AddressingBits) != 0 || kind == RgbaPackKind.InterfaceImages && entry.Addressing != 0)
                throw new InvalidDataException($"{entry.Name}: addressing word 0x{entry.Addressing:X} is not one a {Format} of this kind stores.");
            long size = TexelBytes(entry.Width, entry.Height);
            BinaryPrimitives.WriteUInt16LittleEndian(span[at..], (ushort)entry.Name.Length); at += 2;
            at += Encoding.Latin1.GetBytes(entry.Name, span[at..]);
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)entry.Width); at += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], (uint)entry.Height); at += 4;
            BinaryPrimitives.WriteUInt32LittleEndian(span[at..], entry.Addressing); at += 4;
            BinaryPrimitives.WriteUInt64LittleEndian(span[at..], (ulong)data); at += 8;
            BinaryPrimitives.WriteUInt64LittleEndian(span[at..], (ulong)size); at += 8;
            offsets[i] = data; data += size;
        }
        if (at != table || data != file.LongLength) throw new InvalidOperationException("The 32-bit pack's layout does not match its size.");
        return offsets;
    }

    /// <summary>
    /// Reads a 32-bit pack into <paramref name="doc"/>: header metadata, then one texture record per record whose pixels lie
    /// in the file. Every read is bounded by the file: the record count by <see cref="FormatRegistry.MaximumDirectoryEntries"/>
    /// and the table's least size before anything is materialized, each name by its stored length. A table that cannot be
    /// parsed stops the read (the document reports it); a record breaking the format (sizes, addressing bits, a name repeated
    /// ignoring case, pixels not following the table and each other without gaps, or the file not ending at the last pixel)
    /// is reported as an error, and records whose pixels lie in the file stay inspectable.
    /// </summary>
    internal static void Read(AssetDocument doc, CancellationToken token)
    {
        var bytes = doc.Bytes;
        BinaryCursor c = new(bytes);
        c.Skip(Magic.Length);
        uint version = c.U32(), kindWord = c.U32(), budget = c.U32(), count = c.U32();
        if (version != Version) throw new InvalidDataException($"Unsupported {Format} version {version}.");
        doc.Metadata["header_raw"] = Convert.ToHexStringLower(bytes.Span[..HeaderBytes]);
        doc.Metadata["pack_kind"] = kindWord switch { 1 => "mission textures", 2 => "interface images", _ => $"unknown ({kindWord})" };
        doc.Metadata["budget_mb"] = budget; doc.Metadata["records"] = count;
        if (kindWord is not ((uint)RgbaPackKind.MissionTextures or (uint)RgbaPackKind.InterfaceImages)) throw new InvalidDataException($"Unknown {Format} kind {kindWord}.");
        var kind = (RgbaPackKind)kindWord;
        if (kind == RgbaPackKind.MissionTextures && budget == 0) doc.Diagnostics.Add(new("Error", $"A mission texture {Format} names its budget in MB; this one stores 0."));
        if (kind == RgbaPackKind.InterfaceImages && budget != 0) doc.Diagnostics.Add(new("Error", $"An interface image {Format} has no budget; this one stores {budget}."));
        FormatRegistry.CheckEntries("Texture record", count);
        int records = c.Count(count, RecordFixedBytes + 1);
        var table = new (string Name, bool Nul, uint Width, uint Height, uint Addressing, ulong Offset, ulong Size, long At)[records];
        for (int i = 0; i < records; i++)
        {
            token.ThrowIfCancellationRequested();
            long at = c.Position;
            int length = c.U16();
            if (length is < 1 or > MaximumNameBytes) throw new InvalidDataException($"Record {i} at 0x{at:X} has a name of {length} bytes (1–{MaximumNameBytes}); the record table ends there.");
            var name = c.Take(length).Span;
            table[i] = (Encoding.Latin1.GetString(name), name.Contains((byte)0), c.U32(), c.U32(), c.U32(), c.U64(), c.U64(), at);
        }
        ulong expected = (ulong)c.Position, end = (ulong)bytes.Length;
        HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < records; i++)
        {
            token.ThrowIfCancellationRequested();
            var (name, nul, width, height, addressing, offset, size, at) = table[i];
            List<string> problems = [];
            bool sides = width is >= 1 and <= MaximumSide && height is >= 1 and <= MaximumSide;
            if (!sides) problems.Add($"size {width} × {height} is outside 1–{MaximumSide:N0} texels a side");
            if (nul) problems.Add("its name holds a NUL byte");
            if (!names.Add(name)) problems.Add("another record has its name, ignoring case");
            if ((addressing & ~AddressingBits) != 0) problems.Add($"addressing word 0x{addressing:X8} sets bits other than clamp U and clamp V");
            else if (kind == RgbaPackKind.InterfaceImages && addressing != 0) problems.Add($"an interface image stores addressing 0, not 0x{addressing:X}");
            if (sides && size != 4UL * width * height) problems.Add($"its data size {size:N0} is not {4UL * width * height:N0} (four bytes a texel)");
            if (offset != expected) problems.Add($"its pixels start at 0x{offset:X}, not at 0x{expected:X} straight after the {(i == 0 ? "record table" : "previous record's")}");
            bool inFile = offset <= end && size <= end - offset;
            if (!inFile) problems.Add($"its pixels 0x{offset:X}+0x{size:X} lie beyond the file's 0x{end:X} bytes");
            foreach (string problem in problems) doc.Diagnostics.Add(new("Error", $"Texture {i} ({JsonData.ShownText(name, 64)}): {problem}.", i, at));
            // A wrong offset is reported once; the records after it are checked against where its pixels end.
            expected = inFile ? offset + size : expected + (sides ? 4UL * width * height : 0);
            if (!inFile || !sides || size != 4UL * width * height) continue;
            JsonObject meta = new() { ["width"] = (int)width, ["height"] = (int)height, ["format"] = "RGBA8", ["addressing"] = (long)addressing, ["clamp_u"] = (addressing & 1) != 0, ["clamp_v"] = (addressing & 2) != 0,
                ["data_offset"] = (long)offset, ["data_size"] = (long)size };
            TextureInfo info = new((int)width, (int)height, 0, 0, -1, (int)offset, (int)size, -1, -1, 0) { Rgba = true };
            doc.Add(AssetKind.Texture, i, name, (long)offset, (long)size, meta, info).Summary = $"{width} × {height} · RGBA8";
        }
        if (expected != end) doc.Diagnostics.Add(new("Error", $"The {Format} ends at 0x{end:X}, not at its last pixel (0x{expected:X})."));
    }
}
