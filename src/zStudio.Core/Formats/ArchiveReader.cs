using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Formats;

internal sealed class ArchiveReader(long maximumTypedZrdBytes = ArchiveZrdBudget.MaximumAllocation) : IZbdFormatReader
{
    public FormatFamily Family => FormatFamily.Archive;
    public void Read(ZbdDocument doc, CancellationToken token)
    {
        BinaryCursor c = new(doc.Bytes); c.Seek(doc.Bytes.Length - 4); uint records = c.U32(); FormatRegistry.CheckEntries("Archive member", records);
        long table = doc.Bytes.Length - 8L - records * 148L; BinaryCursor.CheckRange(doc.Bytes.Length, table, records * 148L);
        doc.ArchiveDirectoryOffset = table;
        Dictionary<(uint Offset, uint Size), (ZrdNode? Tree, MotionClip? Motion, bool Limited)> typedRanges = [];
        ArchiveZrdBudget zrdBudget = new(maximumTypedZrdBytes);
        int limitedMembers = 0, omittedDiagnostics = 0; bool omittedError = false;
        long motionSamples = 0;
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
                    bool limited = false; ZrdNode? typed = null; MotionClip? clip = null;
                    try { typed = ZrdDecoder.TryRead(bytes, token, zrdBudget); }
                    catch (ZrdBudgetExceededException) { limited = true; }
                    // Every distinct motion payload materializes dense samples; bound the archive total before decoding.
                    if (typed == null && MotionClip.HeaderSamples(bytes.Span) is long samples)
                    {
                        if (motionSamples + samples > MotionClip.MaximumArchiveSamples)
                            Diagnostic(new("Warning", $"Archive member {i} ({name}) would exceed the supported {MotionClip.MaximumArchiveSamples:N0} decoded motion samples per archive; raw inspection and member replacement remain available.", i, offset));
                        else if ((clip = MotionClip.TryRead(bytes, token)) != null) motionSamples += samples;
                    }
                    typedRanges[(offset, size)] = decoded = (typed, clip, limited && clip == null);
                }
                var (tree, motion, zrdLimited) = decoded;
                if (tree != null) kind = AssetKind.Zrd;
                else if (zrdLimited) limitedMembers++;
                else if (probe.Family == FormatFamily.Zrd && probe.Description != FormatRegistry.SourceZrdDescription) Diagnostic(new("Warning", $"Archive member {i} ({name}) is not a complete ZRD value; raw inspection and member replacement remain available.", i, offset));
                if (motion != null) { kind = AssetKind.Motion; doc.Game = GameVariant.MechWarrior3; }
                var a = doc.Add(kind, i, name, offset, size, new JsonObject { ["source_path"] = source, ["aux_value"] = (long)aux, ["source_filetime"] = time.ToString(System.Globalization.CultureInfo.InvariantCulture), ["record_raw"] = Convert.ToHexStringLower(doc.Bytes.Span.Slice((int)recStart, 148)) }, (object?)motion ?? tree);
                if (motion != null) a.Metadata["motion"] = motion.ToJson(token: token);
                if (zrdLimited) a.Metadata["typed_decode_limited"] = true;
                a.Summary = $"{size:N0} bytes · {kind}";
            }
            catch (InvalidDataException ex) { Diagnostic(new("Error", $"Archive member {i} ({name}): {ex.Message}", i, offset)); }
        }
        if (limitedMembers > 0) doc.Diagnostics.Add(new("Warning", $"Typed ZRD decoding for {limitedMembers:N0} archive members was skipped by the shared allocation budget; raw inspection, exact member export and replacement remain available."));
        if (omittedDiagnostics > 0) doc.Diagnostics.Add(new(omittedError ? "Error" : "Warning", $"{omittedDiagnostics:N0} further archive diagnostics were omitted after the 2,000-message limit."));
        MechLibraryReader.Read(doc, token);
        void Diagnostic(Diagnostic diagnostic)
        {
            if (doc.Diagnostics.Count < 2000) doc.Diagnostics.Add(diagnostic);
            else { omittedDiagnostics++; omittedError |= diagnostic.Severity == "Error"; }
        }
    }
}

/// <summary>
/// Reserves decoded shape before allocation: node/list overhead, UTF-16 text and child arrays including transient
/// list growth. Failed probes spend their reservations too. Exact aliases share at the caller; overlapping ranges
/// pay for each tree. Payload length alone cannot distinguish one long string from many small, expensive nodes.
/// </summary>
internal sealed class ArchiveZrdBudget(long maximumAllocation = ArchiveZrdBudget.MaximumAllocation)
{
    internal const long MaximumAllocation = 64L * 1024 * 1024;
    private long remaining = maximumAllocation;
    internal void Node() => Reserve(192);
    internal void Text(int characters) => Reserve(24L + 2L * characters);
    internal void Children(int count) => Reserve(40L * count);
    internal void Tree(ZrdNode tree, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Node();
        if (tree.Kind == ZrdKind.String) Text(tree.Text.Length);
        if (tree.Kind == ZrdKind.Array)
        {
            Children(tree.Children.Count);
            foreach (var child in tree.Children) Tree(child, token);
        }
    }
    private void Reserve(long bytes)
    {
        if (bytes > remaining) throw new ZrdBudgetExceededException();
        remaining -= bytes;
    }
}

internal sealed class ZrdBudgetExceededException() : IOException("The archive's shared typed ZRD allocation budget was exceeded.");

public static partial class ZrdDecoder
{
    public static JsonObject Decode(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => Read(bytes, token).ToJson(token);
}
