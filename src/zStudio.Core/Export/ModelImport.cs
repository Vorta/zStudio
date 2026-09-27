using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

namespace Recoil.Zbd.Core.Export;

public sealed record ImportedMesh(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, int[] Triangles)
{
    public (Vector3 Min, Vector3 Max) Bounds => (Positions.Aggregate(Vector3.Min), Positions.Aggregate(Vector3.Max));
    public void Validate()
    {
        if (Positions.Length is < 3 or > 65535 || Normals.Length != Positions.Length || Uvs.Length != Positions.Length || Triangles.Length is < 3 or > 60000 || Triangles.Length % 3 != 0)
            throw new InvalidDataException("Mesh requires 3–65,535 vertices with UVs/normals and at most 20,000 triangles.");
        if (Positions.Any(v => !Finite(v) || Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))) > 1_000_000) || Normals.Any(v => !Finite(v) || v.LengthSquared() < .5f || v.LengthSquared() > 1.5f) || Uvs.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || Math.Abs(v.X) > 1000 || Math.Abs(v.Y) > 1000))
            throw new InvalidDataException("Mesh contains invalid coordinates, UVs or normals.");
        if (Triangles.Any(i => i < 0 || i >= Positions.Length)) throw new InvalidDataException("Triangle index is out of range.");
        for (int i = 0; i < Triangles.Length; i += 3)
            if (Vector3.Cross(Positions[Triangles[i + 1]] - Positions[Triangles[i]], Positions[Triangles[i + 2]] - Positions[Triangles[i]]).LengthSquared() < 1e-16)
                throw new InvalidDataException($"Triangle {i / 3} is degenerate.");
    }
    private static bool Finite(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}

public sealed record ModelImportMapping(int ModelIndex, string Obj);
public sealed record ModelImportManifest(int Version, string SourceSha256, string TextureName, string Texture, ModelImportMapping[] Models);
public sealed record ModelImportBatch(string SourceSha256, string TextureName, DecodedImage Texture, IReadOnlyDictionary<int, ImportedMesh> Models);

/// <summary>One bounded, explicit local-coordinate interchange. OBJ uses +Y up/-Z forward, bottom-origin UVs.</summary>
public static class ModelImport
{
    public static async Task<ModelImportBatch> ReadAsync(string manifestPath, CancellationToken token = default)
    {
        manifestPath = Path.GetFullPath(manifestPath); string root = Path.GetDirectoryName(manifestPath)!;
        byte[] json = await ReadBoundedAsync(manifestPath, 1024 * 1024, token);
        var manifest = JsonSerializer.Deserialize<ModelImportManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow }) ?? throw new InvalidDataException("Missing replacement manifest.");
        if (manifest.Version != 1 || manifest.Models is not { Length: > 0 and <= 128 } || manifest.Models.Select(m => m.ModelIndex).Distinct().Count() != manifest.Models.Length || manifest.SourceSha256 is not { Length: 64 } || !manifest.SourceSha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Manifest requires version 1, a source SHA-256 and 1–128 unique model indices.");
        var image = ReadPng(await ReadBoundedAsync(InputPath(root, manifest.Texture), 4 * 1024 * 1024, token), token);
        Dictionary<int, ImportedMesh> models = [];
        Dictionary<string, ImportedMesh> meshCache = new(StringComparer.OrdinalIgnoreCase);
        long totalInput = json.Length;
        foreach (var mapping in manifest.Models)
        {
            token.ThrowIfCancellationRequested(); string obj = InputPath(root, mapping.Obj);
            if (meshCache.TryGetValue(obj, out var reused)) { models.Add(mapping.ModelIndex, reused); continue; }
            string content = System.Text.Encoding.UTF8.GetString(await ReadInput(obj, 16 * 1024 * 1024));
            // MTL is optional for interchange. If present, it must reference exactly the manifest's diffuse texture.
            var libraries = content.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("mtllib ", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
            if (libraries.Length > 8) throw new InvalidDataException("An OBJ may reference at most eight material libraries.");
            foreach (string line in libraries)
            {
                string mtlPath = InputPath(Path.GetDirectoryName(obj)!, line[7..].Trim());
                string mtl = System.Text.Encoding.UTF8.GetString(await ReadInput(mtlPath, 1024 * 1024));
                foreach (var map in mtl.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("map_Kd ", StringComparison.Ordinal)))
                    if (!InputPath(Path.GetDirectoryName(mtlPath)!, map[7..].Trim()).Equals(InputPath(root, manifest.Texture), StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("All OBJ materials must use the diffuse PNG explicitly named by the manifest.");
            }
            var mesh = ReadObj(content, token); meshCache.Add(obj, mesh); models.Add(mapping.ModelIndex, mesh);
        }
        return new(manifest.SourceSha256, manifest.TextureName, image, models);
        async Task<byte[]> ReadInput(string path, int limit)
        {
            byte[] bytes = await ReadBoundedAsync(path, limit, token);
            totalInput += bytes.Length;
            if (totalInput > 64 * 1024 * 1024) throw new InvalidDataException("The import batch exceeds 64 MiB of mesh/material input.");
            return bytes;
        }
    }
    private static string InputPath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) throw new InvalidDataException("A relative input filename is required.");
        string path = ExportService.DestinationPath(root, relative);
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Input file links are not supported.");
        return path;
    }
    private static async Task<byte[]> ReadBoundedAsync(string path, int limit, CancellationToken token)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > limit) throw new InvalidDataException($"Input exceeds {limit:N0} bytes: {path}");
        byte[] bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, token); return bytes;
    }
    public static ImportedMesh ReadObj(string text, CancellationToken token = default)
    {
        List<Vector3> vertices = [], normals = [], positions = [], outputNormals = []; List<Vector2> uvs = [], outputUvs = []; List<int> triangles = [];
        Dictionary<(int V, int T, int N), int> indices = []; int lineNumber = 0;
        foreach (string raw in text.Split('\n'))
        {
            token.ThrowIfCancellationRequested(); lineNumber++;
            string[] p = raw.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) continue;
            try
            {
                if (p[0] == "v" && p.Length == 4) vertices.Add(new(F(p[1]), F(p[2]), F(p[3])));
                else if (p[0] == "vn" && p.Length == 4) { var n = new Vector3(F(p[1]), F(p[2]), F(p[3])); if (n.LengthSquared() < 1e-12) throw new FormatException("Zero normal."); normals.Add(Vector3.Normalize(n)); }
                else if (p[0] == "vt" && p.Length is 3 or 4) uvs.Add(new(F(p[1]), 1 - F(p[2])));
                else if (p[0] == "f")
                {
                    if (p.Length != 4) throw new FormatException("Triangulate faces in Blender before export.");
                    foreach (string corner in p.Skip(1))
                    {
                        var fields = corner.Split('/'); if (fields.Length != 3) throw new FormatException("Each face corner requires vertex/UV/normal indices.");
                        var key = (V: Index(fields[0], vertices.Count), T: Index(fields[1], uvs.Count), N: Index(fields[2], normals.Count));
                        if (!indices.TryGetValue(key, out int index))
                        { index = positions.Count; indices.Add(key, index); positions.Add(vertices[key.V]); outputUvs.Add(uvs[key.T]); outputNormals.Add(normals[key.N]); }
                        triangles.Add(index);
                    }
                }
                else if (p[0] is not ("o" or "g" or "s" or "usemtl" or "mtllib" or "#")) throw new FormatException($"Unsupported OBJ directive {p[0]}.");
                if (vertices.Count > 65535 || normals.Count > 65535 || uvs.Count > 65535 || positions.Count > 65535 || triangles.Count > 60000) throw new FormatException("Mesh exceeds import limits.");
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { throw new InvalidDataException($"OBJ line {lineNumber}: {ex.Message}", ex); }
        }
        ImportedMesh mesh = new(positions.ToArray(), outputNormals.ToArray(), outputUvs.ToArray(), triangles.ToArray()); mesh.Validate(); return mesh;
        static float F(string s) { float f = float.Parse(s, CultureInfo.InvariantCulture); if (!float.IsFinite(f)) throw new FormatException("Nonfinite value."); return f; }
        static int Index(string s, int count) { int v = int.Parse(s, CultureInfo.InvariantCulture); int i = v < 0 ? count + v : v - 1; if (i < 0 || i >= count) throw new FormatException("Missing index."); return i; }
    }

    /// <summary>Strict 8-bit RGB/RGBA PNG decoder for Blender diffuse exports; limits decoded allocations.</summary>
    public static DecodedImage ReadPng(byte[] png, CancellationToken token = default, int maximumDimension = Formats.TexturePackWriter.MaximumDimension)
    {
        if (maximumDimension is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        if (png.Length < 33 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137,80,78,71,13,10,26,10 })) throw new InvalidDataException("Expected PNG.");
        int width = 0, height = 0, channels = 0, offset = 8; bool ended = false, header = false;
        using MemoryStream compressed = new();
        while (offset < png.Length)
        {
            token.ThrowIfCancellationRequested();
            if (offset > png.Length - 12) throw new InvalidDataException("Truncated PNG chunk.");
            int length = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(offset));
            if (length < 0 || length > png.Length - offset - 12) throw new InvalidDataException("Invalid PNG chunk length.");
            var type = png.AsSpan(offset + 4, 4); var data = png.AsSpan(offset + 8, length);
            uint crc = uint.MaxValue;
            foreach (byte b in png.AsSpan(offset + 4, length + 4)) { crc ^= b; for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xedb88320u : 0); }
            if (~crc != BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(offset + 8 + length))) throw new InvalidDataException("PNG checksum mismatch.");
            if (type.SequenceEqual("IHDR"u8))
            {
                if (header || offset != 8 || length != 13) throw new InvalidDataException("Invalid PNG header.");
                width = BinaryPrimitives.ReadInt32BigEndian(data); height = BinaryPrimitives.ReadInt32BigEndian(data[4..]);
                int limit = maximumDimension;
                if (width < 1 || width > limit || height < 1 || height > limit || data[8] != 8 || data[9] is not (2 or 6) || data[10] != 0 || data[11] != 0 || data[12] != 0)
                    throw new InvalidDataException($"Export a non-interlaced 8-bit RGB/RGBA PNG up to {limit} × {limit}.");
                channels = data[9] == 6 ? 4 : 3; header = true;
            }
            else if (type.SequenceEqual("IDAT"u8)) { if (!header) throw new InvalidDataException("Missing PNG header."); compressed.Write(data); }
            else if (type.SequenceEqual("IEND"u8)) { if (length != 0 || offset + 12 != png.Length) throw new InvalidDataException("Invalid PNG end."); ended = true; break; }
            else if (type.SequenceEqual("tRNS"u8) || (type[0] & 32) == 0) throw new InvalidDataException("Unsupported PNG chunk; export opaque RGB/RGBA.");
            offset += length + 12;
        }
        if (!header || !ended) throw new InvalidDataException("Incomplete PNG.");
        int stride = width * channels; byte[] raw = new byte[(stride + 1) * height]; compressed.Position = 0;
        using (ZLibStream z = new(compressed, CompressionMode.Decompress)) { try { z.ReadExactly(raw); } catch (EndOfStreamException ex) { throw new InvalidDataException("Truncated PNG pixels.", ex); } if (z.ReadByte() != -1) throw new InvalidDataException("Excess PNG pixels."); }
        byte[] pixels = new byte[stride * height], rgba = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested(); int filter = raw[y * (stride + 1)]; if (filter > 4) throw new InvalidDataException("Invalid PNG filter.");
            for (int x = 0; x < stride; x++)
            {
                int a = x >= channels ? pixels[y * stride + x - channels] : 0, b = y > 0 ? pixels[(y - 1) * stride + x] : 0, c = y > 0 && x >= channels ? pixels[(y - 1) * stride + x - channels] : 0;
                int pa = Math.Abs(b - c), pb = Math.Abs(a - c), pc = Math.Abs(a + b - 2 * c);
                int predict = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => pa <= pb && pa <= pc ? a : pb <= pc ? b : c };
                pixels[y * stride + x] = unchecked((byte)(raw[y * (stride + 1) + x + 1] + predict));
            }
        }
        for (int i = 0; i < width * height; i++) { pixels.AsSpan(i * channels, 3).CopyTo(rgba.AsSpan(i * 4)); rgba[i * 4 + 3] = channels == 4 ? pixels[i * 4 + 3] : (byte)255; }
        return new(width, height, rgba);
    }
}
