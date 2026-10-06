using System.Buffers.Binary;

namespace Recoil.Zbd.Core.Formats;

public interface IZbdFormatReader
{
    FormatFamily Family { get; }
    void Read(ZbdDocument document, CancellationToken cancellationToken);
}

public sealed class FormatRegistry
{
    private readonly Dictionary<FormatFamily, IZbdFormatReader> readers = new IZbdFormatReader[]
    { new TextureReader(), new ArchiveReader(), new ScriptReader(), new AnimationReader(), new GameZReader() }.ToDictionary(r => r.Family);
    public static FormatRegistry Default { get; } = new();
    internal const string SourceZrdDescription = "zReader source text", SourceScriptDescription = "Gamegen script source";
    public const long MaximumDocumentBytes = 512L * 1024 * 1024;
    /// <summary>
    /// Supported entries per stored directory/table (archive members, texture/script records, GameZ tables), far above
    /// retail data (at most 20,000). Each entry becomes metadata, so the count is checked before anything is materialized.
    /// </summary>
    public const int MaximumDirectoryEntries = 65_536;
    internal static void CheckEntries(string kind, long count, long maximum = MaximumDirectoryEntries)
    { if (count > maximum) throw new InvalidDataException($"{kind} count {count:N0} exceeds the supported {maximum:N0}; source bytes are retained."); }
    internal static void ValidateDocumentSize(long size)
    {
        if (size < 0 || size > MaximumDocumentBytes) throw new InvalidDataException("Files larger than 512 MiB cannot be opened or saved in this version.");
    }

    public static FormatProbe Probe(string path)
    {
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            byte[] prefix = new byte[(int)Math.Min(36, stream.Length)]; stream.ReadExactly(prefix);
            byte[] trailer = new byte[(int)Math.Min(8, stream.Length)]; stream.Seek(-trailer.Length, SeekOrigin.End); stream.ReadExactly(trailer);
            return Probe(prefix, trailer, stream.Length, System.IO.Path.GetExtension(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return new(FormatFamily.Unknown, null, Recognition.Malformed, ex.Message); }
    }
    /// <summary>zReader data: resources (<c>.zrd</c>) and source projects' animation definitions (<c>.zad</c>), the same syntax.</summary>
    private static bool IsZrdExtension(string extension) =>
        extension.Equals(".zrd", StringComparison.OrdinalIgnoreCase) || extension.Equals(Animation.AnimationDefinitionSet.Extension, StringComparison.OrdinalIgnoreCase);
    public static FormatProbe Probe(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> trailer, long size, string extension = "")
    {
        uint magic = prefix.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(prefix) : 0;
        uint version = prefix.Length >= 8 ? BinaryPrimitives.ReadUInt32LittleEndian(prefix[4..]) : 0;
        FormatFamily family = magic switch { 0x08971119 => FormatFamily.Scripts, 0x08170616 => FormatFamily.Animation, 0x02971222 => FormatFamily.GameZ, _ => FormatFamily.Unknown };
        if (family != FormatFamily.Unknown)
        {
            uint expected = family switch { FormatFamily.Scripts => 7, FormatFamily.Animation => 28, _ => 15 };
            int minimum = family == FormatFamily.GameZ ? 36 : 12;
            if (prefix.Length < minimum) return new(family, version, Recognition.Malformed, "Truncated header");
            bool supported = version == expected || family == FormatFamily.GameZ && version is 13 or 27 || family == FormatFamily.Animation && version == 39;
            string versions = family switch { FormatFamily.GameZ => "13, 15 or 27", FormatFamily.Animation => "28 or 39", _ => expected.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            // Version 13 is the RECOIL demos' world format (July and August 1998): read-only.
            string description = family == FormatFamily.GameZ && version == 13 ? $"{family} · version 13 (1998 demo, read-only)" : $"{family} · version {version}";
            return new(family, version, supported ? Recognition.Supported : Recognition.UnsupportedVersion,
                supported ? description : $"Unsupported {family} version {version} (expected {versions})");
        }
        if (prefix.Length >= 24 && magic == 0 && version == 1)
        {
            uint palettes = BinaryPrimitives.ReadUInt32LittleEndian(prefix[8..]), records = BinaryPrimitives.ReadUInt32LittleEndian(prefix[12..]);
            bool valid = 24L + palettes * 512L + records * 40L <= size;
            return new(FormatFamily.TexturePack, 1, valid ? Recognition.Supported : Recognition.Malformed, valid ? $"Texture pack · {records:N0} textures" : "Texture tables exceed file length");
        }
        if (IsZrdExtension(extension) && magic is >= 1 and <= 4)
            return new(FormatFamily.Zrd, null, Recognition.Supported, "zReader typed data");
        if (trailer.Length == 8 && BinaryPrimitives.ReadUInt32LittleEndian(trailer) == 1)
        {
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]);
            bool valid = 8L + count * 148L <= size;
            return new(FormatFamily.Archive, 1, valid ? Recognition.Supported : Recognition.Malformed, valid ? $"ZAR archive · {count:N0} members" : "Archive index exceeds file length");
        }
        // Sound ZARs start directly with their first RIFF member. Their valid
        // trailing archive index takes precedence over a standalone WAV header.
        if (prefix.Length >= 12 && prefix[..4].SequenceEqual("RIFF"u8) && prefix.Slice(8, 4).SequenceEqual("WAVE"u8))
            return new(FormatFamily.Wave, null, Recognition.Supported, "RIFF / WAVE audio");
        // Reconstructed source text is recognized by name only after every structural format has been ruled out.
        if (IsZrdExtension(extension) && Sources.ZrdText.LooksLikeText(prefix))
            return new(FormatFamily.Zrd, null, Recognition.Supported, SourceZrdDescription);
        if (extension.Equals(".gw", StringComparison.OrdinalIgnoreCase) || extension.Equals(".gs", StringComparison.OrdinalIgnoreCase))
            return new(FormatFamily.Scripts, null, Recognition.Supported, SourceScriptDescription);
        return new(FormatFamily.Unknown, null, Recognition.Unknown, "Unrecognized format · raw inspection available");
    }

    public async Task<ZbdDocument> OpenAsync(string path, CancellationToken token = default)
    {
        path = System.IO.Path.GetFullPath(path);
        FileStamp stamp = FileStamp.Read(path);
        ValidateDocumentSize(stamp.Length);
        byte[] bytes = await Sources.SourceRead.AllAsync(path, MaximumDocumentBytes, token).ConfigureAwait(false);
        if (FileStamp.Read(path) != stamp) throw new IOException("The file changed while opening. Reload it to read a consistent snapshot.");
        return await Task.Run(() => OpenBytes(path, bytes, stamp, token), token).ConfigureAwait(false);
    }
    public ZbdDocument OpenBytes(string path, byte[] bytes, FileStamp? stamp = null, CancellationToken token = default)
    {
        ValidateDocumentSize(bytes.LongLength);
        var probe = Probe(bytes.AsSpan(0, Math.Min(36, bytes.Length)), bytes.AsSpan(Math.Max(0, bytes.Length - 8)), bytes.Length, System.IO.Path.GetExtension(path));
        ZbdDocument doc = new(path, stamp ?? new(bytes.Length, DateTime.MinValue), probe, bytes);
        doc.Metadata["family"] = probe.Family.ToString(); doc.Metadata["version"] = probe.Version; doc.Metadata["file_size"] = bytes.Length;
        if (probe.Recognition is Recognition.UnsupportedVersion or Recognition.Malformed)
        {
            doc.Diagnostics.Add(new("Error", probe.Description)); doc.Add(AssetKind.Raw, 0, System.IO.Path.GetFileName(path), 0, bytes.Length); return doc;
        }
        try
        {
            // Reconstructed sources open with the same asset model as their compiled forms.
            if (probe.Description == SourceZrdDescription)
            {
                doc.SourceSyntax = "zrd-text";
                doc.Add(AssetKind.Zrd, 0, System.IO.Path.GetFileName(path), 0, bytes.Length, content: Sources.ZrdText.Parse(bytes, token));
            }
            else if (probe.Description == SourceScriptDescription)
            {
                doc.SourceSyntax = "gamegen-script"; string text = Sources.GameGenScriptText.Decode(bytes);
                var lines = Sources.GameGenScriptText.Tokenize(text);
                doc.Add(AssetKind.Script, 0, System.IO.Path.GetFileName(path), 0, bytes.Length, new System.Text.Json.Nodes.JsonObject { ["instructions"] = lines.Count },
                    new ScriptContent(lines.Select(l => l.ToArray()).ToArray(), text)).Summary = $"{lines.Count:N0} instructions";
            }
            else if (readers.TryGetValue(probe.Family, out var reader)) reader.Read(doc, token);
            else if (probe.Family == FormatFamily.Zrd)
                doc.Add(AssetKind.Zrd, 0, System.IO.Path.GetFileName(path), 0, bytes.Length, content: ZrdDecoder.Read(bytes, token));
            else doc.Add(probe.Family switch { FormatFamily.Zrd => AssetKind.Zrd, FormatFamily.Wave => AssetKind.Sound, _ => AssetKind.Raw }, 0, System.IO.Path.GetFileName(path), 0, bytes.Length);
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        {
            doc.Diagnostics.Add(new("Error", $"Parsing stopped: {ex.Message}"));
            if (probe.Family == FormatFamily.Zrd) doc.Add(AssetKind.Raw, 0, System.IO.Path.GetFileName(path), 0, bytes.Length);
        }
        doc.Metadata["game"] = doc.Game.ToString();
        return doc;
    }
}
