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
    public const long MaximumDocumentBytes = 512L * 1024 * 1024;

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
            return new(family, version, version == expected ? Recognition.Supported : Recognition.UnsupportedVersion,
                version == expected ? $"{family} · version {version}" : $"Unsupported {family} version {version} (expected {expected})");
        }
        if (prefix.Length >= 24 && magic == 0 && version == 1)
        {
            uint palettes = BinaryPrimitives.ReadUInt32LittleEndian(prefix[8..]), records = BinaryPrimitives.ReadUInt32LittleEndian(prefix[12..]);
            bool valid = 24L + palettes * 512L + records * 40L <= size;
            return new(FormatFamily.TexturePack, 1, valid ? Recognition.Supported : Recognition.Malformed, valid ? $"Texture pack · {records:N0} textures" : "Texture tables exceed file length");
        }
        if (extension.Equals(".zrd", StringComparison.OrdinalIgnoreCase) && magic is >= 1 and <= 4)
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
        return new(FormatFamily.Unknown, null, Recognition.Unknown, "Unrecognized format · raw inspection available");
    }

    public async Task<ZbdDocument> OpenAsync(string path, CancellationToken token = default)
    {
        path = System.IO.Path.GetFullPath(path);
        FileStamp stamp = FileStamp.Read(path);
        if (stamp.Length > MaximumDocumentBytes) throw new InvalidDataException("Files larger than 512 MiB cannot be opened in this version.");
        byte[] bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        if (FileStamp.Read(path) != stamp) throw new IOException("The file changed while opening. Reload it to read a consistent snapshot.");
        return await Task.Run(() => OpenBytes(path, bytes, stamp, token), token).ConfigureAwait(false);
    }
    public ZbdDocument OpenBytes(string path, byte[] bytes, FileStamp? stamp = null, CancellationToken token = default)
    {
        var probe = Probe(bytes.AsSpan(0, Math.Min(36, bytes.Length)), bytes.AsSpan(Math.Max(0, bytes.Length - 8)), bytes.Length, System.IO.Path.GetExtension(path));
        ZbdDocument doc = new(path, stamp ?? new(bytes.Length, DateTime.MinValue), probe, bytes);
        doc.Metadata["family"] = probe.Family.ToString(); doc.Metadata["version"] = probe.Version; doc.Metadata["file_size"] = bytes.Length;
        if (probe.Recognition is Recognition.UnsupportedVersion or Recognition.Malformed)
        {
            doc.Diagnostics.Add(new("Error", probe.Description)); doc.Add(AssetKind.Raw, 0, System.IO.Path.GetFileName(path), 0, bytes.Length); return doc;
        }
        try
        {
            if (readers.TryGetValue(probe.Family, out var reader)) reader.Read(doc, token);
            else doc.Add(probe.Family switch { FormatFamily.Zrd => AssetKind.Zrd, FormatFamily.Wave => AssetKind.Sound, _ => AssetKind.Raw }, 0, System.IO.Path.GetFileName(path), 0, bytes.Length);
        }
        catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentOutOfRangeException)
        { doc.Diagnostics.Add(new("Error", $"Parsing stopped: {ex.Message}")); }
        return doc;
    }
}
