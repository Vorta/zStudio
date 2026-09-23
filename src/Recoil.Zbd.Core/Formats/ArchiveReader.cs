using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Formats;

internal sealed class ArchiveReader : IZbdFormatReader
{
    public FormatFamily Family => FormatFamily.Archive;
    public void Read(ZbdDocument doc, CancellationToken token)
    {
        BinaryCursor c = new(doc.Bytes); c.Seek(doc.Bytes.Length - 4); uint records = c.U32();
        long table = doc.Bytes.Length - 8L - records * 148L; BinaryCursor.CheckRange(doc.Bytes.Length, table, records * 148L);
        c.Seek((int)table);
        for (int i = 0; i < records; i++)
        {
            token.ThrowIfCancellationRequested();
            long recStart = c.Position; uint offset = c.U32(), size = c.U32(); string name = c.String(64); uint aux = c.U32(); string source = c.String(64); ulong time = c.U64();
            try
            {
                BinaryCursor.CheckRange(table, offset, size);
                var bytes = doc.Slice(offset, size); var probe = FormatRegistry.Probe(bytes.Span[..Math.Min(36, bytes.Length)], bytes.Span[Math.Max(0, bytes.Length - 8)..], bytes.Length, Path.GetExtension(name));
                AssetKind kind = probe.Family switch { FormatFamily.Wave => AssetKind.Sound, FormatFamily.Zrd => AssetKind.Zrd, _ => AssetKind.Raw };
                var a = doc.Add(kind, i, name, offset, size, new JsonObject { ["source_path"] = source, ["aux_value"] = (long)aux, ["source_filetime"] = time.ToString(System.Globalization.CultureInfo.InvariantCulture), ["record_raw"] = Convert.ToHexStringLower(doc.Bytes.Span.Slice((int)recStart, 148)) });
                a.Summary = $"{size:N0} bytes · {kind}";
            }
            catch (InvalidDataException ex) { doc.Diagnostics.Add(new("Error", $"Archive member {i} ({name}): {ex.Message}", i, offset)); }
        }
    }
}

public static class ZrdDecoder
{
    public static JsonObject Decode(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        BinaryCursor c = new(bytes); int remainingNodes = 2_000_000;
        JsonObject result = Read(c, token, 0, ref remainingNodes);
        if (c.Remaining != 0) throw new InvalidDataException($"Trailing ZRD bytes at 0x{c.Position:X}.");
        return result;
    }
    private static JsonObject Read(BinaryCursor c, CancellationToken token, int depth, ref int budget)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 128 || --budget < 0) throw new InvalidDataException("ZRD nesting or node limit exceeded.");
        long offset = c.AbsolutePosition; uint type = c.U32();
        JsonObject node = new() { ["offset"] = $"0x{offset:X}" };
        switch (type)
        {
            case 1: node["type"] = "int"; node["value"] = (long)c.I32(); break;
            case 2: node["type"] = "float"; float f = c.F32(); node["value"] = JsonData.Number(f); node["raw_bits"] = $"0x{BitConverter.SingleToUInt32Bits(f):X8}"; break;
            case 3: node["type"] = "string"; int length = c.Count(c.U32()); node["value"] = System.Text.Encoding.Latin1.GetString(c.Take(length).Span); break;
            case 4:
                node["type"] = "array"; int count = c.I32(); if (count < 1) throw new InvalidDataException("Invalid ZRD array count."); c.Count((uint)(count - 1), 4);
                JsonArray children = []; for (int i = 1; i < count; i++) children.Add(Read(c, token, depth + 1, ref budget)); node["children"] = children; break;
            default: throw new InvalidDataException($"Unknown ZRD type {type} at 0x{offset:X}.");
        }
        return node;
    }
}
