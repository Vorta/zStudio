using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text.Json;

namespace Recoil.Zbd.Core.Export;

public sealed record ImportedMesh(Vector3[] Positions, Vector3[] Normals, Vector2[] Uvs, int[] Triangles)
{
    public Vector3[] Colors { get; init; } = [];
    // A plain loop: Aggregate(Vector3.Min) once returned a wrong minimum under dynamic PGO in a long test run.
    public (Vector3 Min, Vector3 Max) Bounds
    {
        get
        {
            Vector3 min = Positions[0], max = Positions[0];
            foreach (var p in Positions) { min = Vector3.Min(min, p); max = Vector3.Max(max, p); }
            return (min, max);
        }
    }
    public void Validate()
    {
        if (Positions.Length is < 3 or > 65535 || Normals.Length != Positions.Length || Uvs.Length != Positions.Length || Triangles.Length is < 3 or > 60000 || Triangles.Length % 3 != 0)
            throw new InvalidDataException("Mesh requires 3–65,535 vertices with UVs/normals and at most 20,000 triangles.");
        if (Positions.Any(v => !Finite(v) || Math.Max(Math.Abs(v.X), Math.Max(Math.Abs(v.Y), Math.Abs(v.Z))) > 1_000_000) || Normals.Any(v => !Finite(v) || v.LengthSquared() < .5f || v.LengthSquared() > 1.5f) || Uvs.Any(v => !float.IsFinite(v.X) || !float.IsFinite(v.Y) || Math.Abs(v.X) > 1000 || Math.Abs(v.Y) > 1000))
            throw new InvalidDataException("Mesh contains invalid coordinates, UVs or normals.");
        if (Triangles.Any(i => i < 0 || i >= Positions.Length)) throw new InvalidDataException("Triangle index is out of range.");
        if (Colors.Length != 0 && (Colors.Length != Positions.Length || Colors.Any(c => !Finite(c) || c.X < 0 || c.Y < 0 || c.Z < 0 || c.X > 1 || c.Y > 1 || c.Z > 1)))
            throw new InvalidDataException("Optional vertex colors require one RGB value in 0–1 for every vertex.");
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
        => await ReadAsync(manifestPath, token, null);

    internal static async Task<ModelImportBatch> ReadAsync(string manifestPath, CancellationToken token, Action<string>? checkpoint)
    {
        ObjText scan = new(token, checkpoint);
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
            foreach (string library in MaterialLibraries(content, scan))
            {
                string mtlPath = InputPath(Path.GetDirectoryName(obj)!, library);
                string mtl = System.Text.Encoding.UTF8.GetString(await ReadInput(mtlPath, 1024 * 1024));
                CheckMaterials(mtl, Path.GetDirectoryName(mtlPath)!, InputPath(root, manifest.Texture), scan);
            }
            var mesh = ReadObj(content, scan); meshCache.Add(obj, mesh); models.Add(mapping.ModelIndex, mesh);
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
    private static IReadOnlyList<string> MaterialLibraries(string text, ObjText scan)
    {
        // Keep source ranges until the eight-library admission is complete. Distinctness remains
        // ordinal on the whole trimmed directive, including whitespace inside that directive.
        List<Range> found = []; var lines = scan.ReadLines(text, "libraries");
        while (lines.MoveNext(out var range))
        {
            var raw = text.AsSpan()[range]; var trim = scan.Trim(raw, "libraries"); var line = raw[trim];
            if (!line.StartsWith("mtllib ", StringComparison.Ordinal)) continue;
            bool duplicate = false;
            foreach (var previous in found)
                if (scan.Equal(line, text.AsSpan()[previous], "libraries")) { duplicate = true; break; }
            if (duplicate) continue;
            if (found.Count == 8) throw new InvalidDataException("An OBJ may reference at most eight material libraries.");
            found.Add((range.Start.Value + trim.Start.Value)..(range.Start.Value + trim.End.Value));
        }
        List<string> result = [];
        foreach (var range in found)
        {
            var value = text.AsSpan()[range][7..];
            scan.Check("libraries");
            // At most eight disjoint source ranges: all retained path characters together fit the input text.
            result.Add(value[scan.Trim(value, "libraries")].ToString());
        }
        return result;
    }
    private static void CheckMaterials(string text, string root, string texture, ObjText scan)
    {
        var lines = scan.ReadLines(text, "materials");
        while (lines.MoveNext(out var range))
        {
            var raw = text.AsSpan()[range]; var line = raw[scan.Trim(raw, "materials")];
            if (!line.StartsWith("map_Kd ", StringComparison.Ordinal)) continue;
            var value = line[7..]; value = value[scan.Trim(value, "materials")];
            scan.Check("materials");
            if (!InputPath(root, value.ToString()).Equals(texture, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("All OBJ materials must use the diffuse PNG explicitly named by the manifest.");
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
        => ReadObj(text, new ObjText(token));

    internal static ImportedMesh ReadObj(string text, CancellationToken token, Action<string> checkpoint)
        => ReadObj(text, new ObjText(token, checkpoint));

    private static ImportedMesh ReadObj(string text, ObjText scan)
    {
        List<Vector3> vertices = [], normals = [], positions = [], outputNormals = []; List<Vector2> uvs = [], outputUvs = []; List<int> triangles = [];
        List<Vector3> colors = [], outputColors = []; bool colored = false;
        Dictionary<(int V, int T, int N), int> indices = []; int lineNumber = 0;
        var lines = scan.ReadLines(text, "obj"); Span<Range> fields = stackalloc Range[7];
        while (lines.MoveNext(out var range))
        {
            lineNumber++; var raw = text.AsSpan()[range];
            if (scan.Fields(raw, fields[..1]) == 0) continue;
            var directive = raw[fields[0]];
            // Ignored directives may contain any number of authored words. Never tokenize their operands.
            if (directive is "o" or "g" or "s" or "usemtl" or "mtllib") continue;
            try
            {
                if (directive is not ("v" or "vn" or "vt" or "f")) throw Unsupported(directive);
                int count = scan.Fields(raw, fields);
                if (directive is "v" && count is 4 or 7)
                { vertices.Add(new(F(raw[fields[1]]), F(raw[fields[2]]), F(raw[fields[3]]))); colors.Add(count == 7 ? new(F(raw[fields[4]]), F(raw[fields[5]]), F(raw[fields[6]])) : Vector3.One); colored |= count == 7; }
                else if (directive is "vn" && count == 4) { var n = new Vector3(F(raw[fields[1]]), F(raw[fields[2]]), F(raw[fields[3]])); if (n.LengthSquared() < 1e-12) throw new FormatException("Zero normal."); normals.Add(Vector3.Normalize(n)); }
                else if (directive is "vt" && count is 3 or 4) uvs.Add(new(F(raw[fields[1]]), 1 - F(raw[fields[2]])));
                else if (directive is "f")
                {
                    if (count != 4) throw new FormatException("Triangulate faces in Blender before export.");
                    for (int c = 1; c < 4; c++)
                    {
                        var corner = raw[fields[c]]; int first = corner.IndexOf('/');
                        int second = first < 0 ? -1 : corner[(first + 1)..].IndexOf('/');
                        if (second < 0) throw new FormatException("Each face corner requires vertex/UV/normal indices.");
                        second += first + 1;
                        if (corner[(second + 1)..].Contains('/')) throw new FormatException("Each face corner requires vertex/UV/normal indices.");
                        var key = (V: Index(corner[..first], vertices.Count), T: Index(corner[(first + 1)..second], uvs.Count), N: Index(corner[(second + 1)..], normals.Count));
                        if (!indices.TryGetValue(key, out int index))
                        { index = positions.Count; indices.Add(key, index); positions.Add(vertices[key.V]); outputUvs.Add(uvs[key.T]); outputNormals.Add(normals[key.N]); outputColors.Add(colors[key.V]); }
                        triangles.Add(index);
                    }
                }
                else throw Unsupported(directive);
                if (vertices.Count > 65535 || normals.Count > 65535 || uvs.Count > 65535 || positions.Count > 65535 || triangles.Count > 60000) throw new FormatException("Mesh exceeds import limits.");
            }
            catch (Exception ex) when (ex is FormatException or OverflowException) { throw new InvalidDataException($"OBJ line {lineNumber}: {ex.Message}", ex); }
        }
        ImportedMesh mesh = new(positions.ToArray(), outputNormals.ToArray(), outputUvs.ToArray(), triangles.ToArray()) { Colors = colored ? outputColors.ToArray() : [] }; mesh.Validate(); return mesh;
        float F(ReadOnlySpan<char> s) { scan.Check("obj"); if (!float.TryParse(s, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out float f)) throw new FormatException("Invalid numeric value."); if (!float.IsFinite(f)) throw new FormatException("Nonfinite value."); return f; }
        int Index(ReadOnlySpan<char> s, int count) { scan.Check("obj"); if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)) throw new FormatException("Invalid vertex/UV/normal index."); int i = v < 0 ? count + v : v - 1; if (i < 0 || i >= count) throw new FormatException("Missing index."); return i; }
        static FormatException Unsupported(ReadOnlySpan<char> name) => new($"Unsupported OBJ directive {name[..Math.Min(128, name.Length)].ToString()}{(name.Length > 128 ? "…" : "")}.");
    }

    /// <summary>Decodes a PNG to RGBA8 through the shared <see cref="PngDecoder"/>, limiting its dimensions.</summary>
    public static DecodedImage ReadPng(byte[] png, CancellationToken token = default, int maximumDimension = Formats.TexturePackWriter.MaximumDimension)
    {
        if (maximumDimension is < 1 or > 4096) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        return PngDecoder.Decode(png, maximumDimension, token);
    }
}
