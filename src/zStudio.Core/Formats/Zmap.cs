using System.Collections.Immutable;
using System.Numerics;
using zStudio.Core.Sources;

namespace zStudio.Core.Formats;

public readonly record struct ZmapColor(byte Red, byte Green, byte Blue)
{
    public static ZmapColor White => new(255, 255, 255);
    public override string ToString() => $"#{Red:X2}{Green:X2}{Blue:X2}";
}

public readonly record struct ZmapVertex(Guid Id, Vector3 Position);
public sealed record ZmapOutline(Guid Id, ZmapColor Color, int ObjectiveIndex, ImmutableArray<ZmapVertex> Vertices);
public sealed record Zmap(uint HeaderWord, Vector3 Minimum, Vector3 Maximum, ImmutableArray<ZmapOutline> Outlines);

/// <summary>RECOIL's loose version-5 HUD map. Record identities belong to the editor and are never serialized.</summary>
public static class ZmapCodec
{
    public const int MaximumBytes = 1024 * 1024;
    public const int MaximumVertices = 16_384;
    private const int HeaderSize = 32, OutlineBytes = 11, VertexBytes = 12;

    public static Zmap Read(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("The ZMAP is larger than 1 MiB.");
        BinaryCursor reader = new(bytes);
        reader.Require(HeaderSize);
        int version = reader.I32();
        if (version != 5) throw new InvalidDataException($"ZMAP version {version} is unsupported. Use version 5.");
        uint header = reader.U32();
        var min = ReadPosition(reader);
        var max = ReadPosition(reader);
        var outlines = ImmutableArray.CreateBuilder<ZmapOutline>();
        int total = 0;
        while (reader.Remaining != 0)
        {
            token.ThrowIfCancellationRequested();
            reader.Require(OutlineBytes);
            ZmapColor rgb = new(reader.U8(), reader.U8(), reader.U8());
            int count = reader.I32();
            if (count <= 0 || count > (reader.Remaining - 4) / VertexBytes)
                throw new InvalidDataException($"ZMAP outline {outlines.Count + 1} has an invalid vertex count ({count}).");
            if (count > MaximumVertices - total) throw VertexLimit();
            total += count;
            var vertices = ImmutableArray.CreateBuilder<ZmapVertex>(count);
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                vertices.Add(new(Guid.NewGuid(), ReadPosition(reader)));
            }
            outlines.Add(new(Guid.NewGuid(), rgb, reader.I32(), vertices.MoveToImmutable()));
        }
        Zmap map = new(header, min, max, outlines.ToImmutable());
        Validate(map, token);
        return map;
    }

    public static byte[] Write(Zmap map, CancellationToken token = default)
    {
        int count = Validate(map, token);
        using MemoryStream stream = new(HeaderSize + OutlineBytes * map.Outlines.Length + VertexBytes * count);
        using BinaryWriter writer = new(stream);
        writer.Write(5);
        writer.Write(map.HeaderWord);
        WritePosition(map.Minimum);
        WritePosition(map.Maximum);
        foreach (var outline in map.Outlines)
        {
            writer.Write(outline.Color.Red);
            writer.Write(outline.Color.Green);
            writer.Write(outline.Color.Blue);
            writer.Write(outline.Vertices.Length);
            foreach (var vertex in outline.Vertices)
            {
                token.ThrowIfCancellationRequested();
                WritePosition(vertex.Position);
            }
            writer.Write(outline.ObjectiveIndex);
        }
        return stream.ToArray();

        void WritePosition(Vector3 p) { writer.Write(p.X); writer.Write(p.Y); writer.Write(p.Z); }
    }

    public static int Validate(Zmap map, CancellationToken token = default)
    {
        Vector3 min = map.Minimum, max = map.Maximum;
        RequireFinite(min);
        RequireFinite(max);
        int count = 0;
        foreach (var outline in map.Outlines)
        {
            if (outline.Vertices.IsDefaultOrEmpty) throw new InvalidDataException("A ZMAP outline needs at least one vertex.");
            if (outline.Vertices.Length > MaximumVertices - count) throw VertexLimit();
            count += outline.Vertices.Length;
            foreach (var vertex in outline.Vertices)
            {
                token.ThrowIfCancellationRequested();
                RequireFinite(vertex.Position);
                min = Vector3.Min(min, vertex.Position);
                max = Vector3.Max(max, vertex.Position);
            }
        }
        float width = max.X - min.X, depth = max.Z - min.Z;
        if (!(width > 0 && depth > 0 && float.IsFinite(width) && float.IsFinite(depth)))
            throw new InvalidDataException("ZMAP bounds must span finite, positive widths on X and Z.");
        return count;
    }

    private static Vector3 ReadPosition(BinaryCursor reader) => new(reader.F32(), reader.F32(), reader.F32());
    private static void RequireFinite(Vector3 p)
    {
        if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z))
            throw new InvalidDataException("ZMAP bounds and vertex coordinates must be finite.");
    }
    private static InvalidDataException VertexLimit() => new($"A ZMAP can contain at most {MaximumVertices:N0} vertices in zStudio.");
}

public static class ZmapFile
{
    public sealed record Snapshot(string Path, Zmap Map, byte[] Bytes);

    public static Zmap Open(string path, CancellationToken token = default) => OpenSnapshot(path, token).Map;

    public static Snapshot OpenSnapshot(string path, CancellationToken token = default)
    {
        path = FullPath(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > ZmapCodec.MaximumBytes) throw new InvalidDataException("The ZMAP is larger than 1 MiB.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return new(path, ZmapCodec.Read(bytes, token), bytes);
    }

    /// <summary>Publish a fresh file, or replace an existing file whose contents match the session's baseline.</summary>
    public static string? Save(string path, byte[] bytes, byte[]? expected, CancellationToken token = default)
    {
        path = FullPath(path);
        if (ProtectedPaths.IsProtected(path)) throw new IOException("Choose a destination outside the read-only game corpus.");
        using DirectoryLease directories = new();
        directories.Parent(path);
        string temporary = Path.Combine(Path.GetDirectoryName(path)!, $".zstudio-{Guid.NewGuid():N}.tmp");
        using var staged = SealedFile.Create(temporary, bytes, directories);
        using var current = expected == null ? null : SealedFile.Open(path, JournalDigest.OfContent(expected), directories);
        if (current?.Attributes.HasFlag(FileAttributes.ReadOnly) == true)
            throw new UnauthorizedAccessException($"'{path}' is read-only. Choose a new path with Save As.");
        token.ThrowIfCancellationRequested();
        if (current == null)
        {
            staged.MoveTo(path);
            return null;
        }
        string backup = Path.Combine(Path.GetDirectoryName(path)!, $".zstudio-previous-{Guid.NewGuid():N}.zmap");
        current.MoveTo(backup);
        try { staged.MoveTo(path); }
        catch (IOException failure)
        {
            try { current.MoveTo(path); }
            catch (IOException restore) { throw new IOException($"Could not save '{path}'. The previous ZMAP is at '{backup}'.", new AggregateException(failure, restore)); }
            throw;
        }
        try { current.DeleteHeld(); return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return backup; }
    }

    public static string FullPath(string path)
    {
        string full = Path.GetFullPath(path);
        if (!Path.GetExtension(full).Equals(".zmap", StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(full).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Choose a .zmap file.");
        return full;
    }
}
