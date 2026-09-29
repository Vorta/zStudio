using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Formats;

internal sealed class ArchiveReader : IZbdFormatReader
{
    public FormatFamily Family => FormatFamily.Archive;
    public void Read(ZbdDocument doc, CancellationToken token)
    {
        BinaryCursor c = new(doc.Bytes); c.Seek(doc.Bytes.Length - 4); uint records = c.U32(); FormatRegistry.CheckEntries("Archive member", records);
        long table = doc.Bytes.Length - 8L - records * 148L; BinaryCursor.CheckRange(doc.Bytes.Length, table, records * 148L);
        doc.ArchiveDirectoryOffset = table;
        Dictionary<(uint Offset, uint Size), (ZrdNode? Tree, MotionClip? Motion)> typedRanges = [];
        c.Seek((int)table);
        for (int i = 0; i < records; i++)
        {
            token.ThrowIfCancellationRequested();
            long recStart = c.Position; uint offset = c.U32(), size = c.U32(); string name = c.String(64); uint aux = c.U32(); string source = c.String(64); ulong time = c.U64();
            try
            {
                BinaryCursor.CheckRange(table, offset, size);
                var bytes = doc.Slice(offset, size); var probe = FormatRegistry.Probe(bytes.Span[..Math.Min(36, bytes.Length)], bytes.Span[Math.Max(0, bytes.Length - 8)..], bytes.Length, Path.GetExtension(name));
                AssetKind kind = probe.Family == FormatFamily.Wave ? AssetKind.Sound : AssetKind.Raw;
                // ZRD has no unique magic. Require a complete bounded decode, not a filename or first word,
                // so renamed typed members remain editable after saving and reopening the archive.
                // Members may alias the same payload. Decode that immutable range only once.
                if (!typedRanges.TryGetValue((offset, size), out var decoded))
                {
                    var typed = ZrdDecoder.TryRead(bytes, token);
                    typedRanges[(offset, size)] = decoded = (typed, typed == null ? MotionClip.TryRead(bytes, token) : null);
                }
                var (tree, motion) = decoded;
                if (tree != null) kind = AssetKind.Zrd;
                else if (probe.Family == FormatFamily.Zrd) doc.Diagnostics.Add(new("Warning", $"Archive member {i} ({name}) is not a complete ZRD value; raw inspection and member replacement remain available.", i, offset));
                if (motion != null) { kind = AssetKind.Motion; doc.Game = GameVariant.MechWarrior3; }
                var a = doc.Add(kind, i, name, offset, size, new JsonObject { ["source_path"] = source, ["aux_value"] = (long)aux, ["source_filetime"] = time.ToString(System.Globalization.CultureInfo.InvariantCulture), ["record_raw"] = Convert.ToHexStringLower(doc.Bytes.Span.Slice((int)recStart, 148)) }, (object?)motion ?? tree);
                if (motion != null) a.Metadata["motion"] = motion.ToJson(token: token);
                a.Summary = $"{size:N0} bytes · {kind}";
            }
            catch (InvalidDataException ex) { doc.Diagnostics.Add(new("Error", $"Archive member {i} ({name}): {ex.Message}", i, offset)); }
        }
        MechLibraryReader.Read(doc, token);
    }
}

public static partial class ZrdDecoder
{
    public static JsonObject Decode(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => Read(bytes, token).ToJson(token);
}
