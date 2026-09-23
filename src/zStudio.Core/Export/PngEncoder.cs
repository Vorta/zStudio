using System.Buffers.Binary;
using System.IO.Compression;

namespace Recoil.Zbd.Core.Export;

/// <summary>Deterministic, lossless RGBA8 PNG output without a UI dependency.</summary>
public static class PngEncoder
{
    public static byte[] Encode(DecodedImage image, CancellationToken token = default)
    {
        if (image.Width <= 0 || image.Height <= 0 || (long)image.Width * image.Height * 4 != image.Rgba.Length) throw new InvalidDataException("Invalid image buffer.");
        using MemoryStream output = new(); output.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13]; BinaryPrimitives.WriteInt32BigEndian(header, image.Width); BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), image.Height); header[8] = 8; header[9] = 6;
        Chunk(output, "IHDR"u8, header);
        using MemoryStream compressed = new();
        using (ZLibStream z = new(compressed, CompressionLevel.Optimal, true))
            for (int y = 0; y < image.Height; y++) { token.ThrowIfCancellationRequested(); z.WriteByte(0); z.Write(image.Rgba.AsSpan(y * image.Width * 4, image.Width * 4)); }
        Chunk(output, "IDAT"u8, compressed.ToArray()); Chunk(output, "IEND"u8, []); return output.ToArray();
    }
    private static void Chunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> word = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(word, data.Length); stream.Write(word); stream.Write(type); stream.Write(data);
        uint crc = uint.MaxValue; foreach (byte b in type) crc = Crc(crc, b); foreach (byte b in data) crc = Crc(crc, b);
        BinaryPrimitives.WriteUInt32BigEndian(word, ~crc); stream.Write(word);
    }
    private static uint Crc(uint crc, byte b) { crc ^= b; for (int i = 0; i < 8; i++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320U : 0); return crc; }
}
