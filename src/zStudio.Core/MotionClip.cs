using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json.Nodes;
using zStudio.Core.Formats;

namespace zStudio.Core;

public readonly record struct MotionFrame(Vector3 Translation, Quaternion Rotation);
public sealed record MotionPart(string Name, uint Flags, IReadOnlyList<MotionFrame> Frames, ReadOnlyMemory<byte> NameBytes);

/// <summary>Version 4 motion tracks. The authored closing sample is retained separately from the playback frame count.</summary>
public sealed class MotionClip
{
    public required ReadOnlyMemory<byte> Header { get; init; }
    public required float LoopTime { get; init; }
    public required int FrameCount { get; init; }
    public required IReadOnlyList<MotionPart> Parts { get; init; }
    /// <summary>Decoded samples one archive may materialize (two dense arrays per sample); retail motion.zbd holds about 200,000.</summary>
    public const long MaximumArchiveSamples = 2_097_152;
    internal const int MaximumPreviewParts = 32;
    /// <summary>The samples a version 4 header would materialize, or null when the header is not a supported motion header.</summary>
    internal static long? HeaderSamples(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 24 || BinaryPrimitives.ReadUInt32LittleEndian(bytes) != 4) return null;
        int frames = BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]), parts = BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]);
        return frames is >= 1 and <= 100_000 && parts is >= 1 and <= 4096 ? (long)parts * (frames + 1) : null;
    }
    public static MotionClip Read(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        BinaryCursor c = new(bytes); var header = c.Take(24); BinaryCursor h = new(header);
        if (h.U32() != 4) throw new InvalidDataException("Only version 4 motion clips are supported.");
        float duration = h.F32(); int frames = h.I32(), parts = h.I32();
        if (!float.IsFinite(duration) || duration <= 0 || frames < 1 || frames > 100_000 || parts < 1 || parts > 4096 || h.F32() != -1 || h.F32() != 1)
            throw new InvalidDataException("Invalid motion duration, frame count, part count, or header.");
        FormatRegistry.CheckEntries("Motion sample", (long)parts * (frames + 1), MaximumArchiveSamples);
        c.Count((uint)parts, checked(8 + (frames + 1) * 28));
        List<MotionPart> tracks = [];
        for (int p = 0; p < parts; p++)
        {
            token.ThrowIfCancellationRequested(); int start = c.Position;
            string name = MechLibraryReader.SizedString(c); var nameBytes = bytes.Slice(start, c.Position - start);
            uint flags = c.U32();
            if (flags != 12) throw new InvalidDataException($"Motion part {p} has unsupported channel flags 0x{flags:X}.");
            var translations = c.Vectors(frames + 1); c.Count((uint)(frames + 1), 16);
            MotionFrame[] samples = new MotionFrame[frames + 1];
            for (int f = 0; f < samples.Length; f++)
            {
                if ((f & 4095) == 0) token.ThrowIfCancellationRequested();
                // On disk quaternions are w,x,y,z; System.Numerics uses x,y,z,w.
                float w = c.F32(); var q = new Quaternion(c.F32(), c.F32(), c.F32(), w);
                if (!float.IsFinite(translations[f].LengthSquared()) || !float.IsFinite(q.LengthSquared()) || q.LengthSquared() < 1e-12f)
                    throw new InvalidDataException($"Motion part {p}, frame {f} has an invalid transform.");
                samples[f] = new(translations[f], q);
            }
            tracks.Add(new(name, flags, samples, nameBytes));
        }
        if (c.Remaining != 0) throw new InvalidDataException("Trailing motion data is unsupported; preserve this member as raw data.");
        return new() { Header = header, LoopTime = duration, FrameCount = frames, Parts = tracks };
    }
    internal static MotionClip? TryRead(ReadOnlyMemory<byte> bytes, CancellationToken token)
    {
        if (bytes.Length < 24 || BinaryPrimitives.ReadUInt32LittleEndian(bytes.Span) != 4) return null;
        try { return Read(bytes, token); } catch (Exception ex) when (ex is InvalidDataException or OverflowException or ArgumentOutOfRangeException) { return null; }
    }
    public MotionFrame Sample(int part, double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) throw new InvalidDataException("Motion time must be finite and nonnegative.");
        if (part < 0 || part >= Parts.Count) throw new InvalidDataException("Missing motion part.");
        double frame = seconds % LoopTime / LoopTime * FrameCount;
        int index = Math.Min((int)frame, FrameCount - 1); float blend = (float)(frame - index);
        var a = Parts[part].Frames[index]; var b = Parts[part].Frames[index + 1];
        return new(Vector3.Lerp(a.Translation, b.Translation, blend), Quaternion.Slerp(Quaternion.Normalize(a.Rotation), Quaternion.Normalize(b.Rotation), blend));
    }
    public JsonObject ToJson(bool bounded = true, CancellationToken token = default) =>
        ToJson(bounded ? Math.Min(MaximumPreviewParts, Parts.Count) : Parts.Count, bounded, token);

    /// <summary>Construct only the rows admitted by the archive's shared ordinary-inspection allowance.</summary>
    internal JsonObject ToJsonPreview(int maximumParts, CancellationToken token)
    {
        if (maximumParts is < 0 or > MaximumPreviewParts) throw new ArgumentOutOfRangeException(nameof(maximumParts));
        return ToJson(Math.Min(maximumParts, Parts.Count), bounded: true, token);
    }

    private JsonObject ToJson(int partCount, bool bounded, CancellationToken token) => new()
    {
        ["version"] = 4, ["loop_seconds"] = LoopTime, ["frame_count"] = FrameCount, ["part_count"] = Parts.Count,
        ["parts_truncated"] = Parts.Count > partCount,
        ["parts"] = JsonData.Array(Parts.Take(partCount).Select((p, i) => (Part: p, Index: i)), row => new JsonObject
        {
            ["index"] = row.Index, ["name"] = bounded ? row.Part.Name[..Math.Min(128, row.Part.Name.Length)] : row.Part.Name,
            ["name_characters"] = row.Part.Name.Length, ["name_truncated"] = bounded && row.Part.Name.Length > 128, ["flags"] = row.Part.Flags,
            ["closing_sample_matches"] = row.Part.Frames[0] == row.Part.Frames[^1]
        }, token)
    };
}
