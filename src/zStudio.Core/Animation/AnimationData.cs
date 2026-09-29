using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Animation;

/// <summary>Little-endian serialized data. Pointer-shaped fields are never dereferenced.</summary>
public class AnimationRecord(byte[] bytes)
{
    public byte[] Bytes { get; } = bytes;
    protected static byte[] SnapshotBytes(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        byte[] copy = new byte[bytes.Length];
        for (int offset = 0; offset < bytes.Length; offset += 4096)
        {
            token.ThrowIfCancellationRequested();
            bytes.AsSpan(offset, Math.Min(4096, bytes.Length - offset)).CopyTo(copy.AsSpan(offset));
        }
        token.ThrowIfCancellationRequested();
        return copy;
    }
    public int I32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Bytes.AsSpan(offset, 4));
    public uint U32(int offset) => BinaryPrimitives.ReadUInt32LittleEndian(Bytes.AsSpan(offset, 4));
    public short I16(int offset) => BinaryPrimitives.ReadInt16LittleEndian(Bytes.AsSpan(offset, 2));
    public float F32(int offset) => BitConverter.Int32BitsToSingle(I32(offset));
    public Vector3 Vector(int offset) => new(F32(offset), F32(offset + 4), F32(offset + 8));
    public string Text(int offset, int length = 32)
    {
        var span = Bytes.AsSpan(offset, length); int end = span.IndexOf((byte)0);
        return Encoding.Latin1.GetString(end < 0 ? span : span[..end]);
    }
    public void SetInt(int offset, int value) => BinaryPrimitives.WriteInt32LittleEndian(Bytes.AsSpan(offset, 4), value);
    public void SetShort(int offset, short value) => BinaryPrimitives.WriteInt16LittleEndian(Bytes.AsSpan(offset, 2), value);
    public void SetFloat(int offset, float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("Edited numbers must be finite.");
        SetInt(offset, BitConverter.SingleToInt32Bits(value));
    }
    public void SetVector(int offset, Vector3 value) { SetFloat(offset, value.X); SetFloat(offset + 4, value.Y); SetFloat(offset + 8, value.Z); }
    public void SetText(int offset, string value, int length = 32)
    {
        if (value.Contains('\0') || value.Any(c => c > 255) || value.Length >= length)
            throw new InvalidDataException($"Use at most {length - 1} Latin-1 characters without NUL.");
        if (Text(offset, length) == value) return;
        // Preserve unrelated bytes after the new terminator, as the loader uses strcmp.
        Encoding.Latin1.GetBytes(value, Bytes.AsSpan(offset, value.Length)); Bytes[offset + value.Length] = 0;
    }
}

public sealed class AnimationEvent(byte[] bytes, long sourceOffset = -1) : AnimationRecord(bytes)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long SourceOffset { get; } = sourceOffset;
    public uint Version { get; init; } = 28;
    public byte Type => Bytes[0];
    public byte StartMode { get => Bytes[1]; set { if (value is < 1 or > 3) throw new InvalidDataException("Unknown timing mode."); Bytes[1] = value; } }
    public float Threshold { get => F32(8); set => SetFloat(8, value); }
    public AnimationEventSpec? Spec => AnimationCatalog.Find(Type, Version);
    public string Name => Spec?.Name ?? $"Unknown event 0x{Type:X2}";
    public AnimationEvent Clone() => Clone(default);
    public AnimationEvent Clone(CancellationToken token) => new(SnapshotBytes(Bytes, token), SourceOffset) { Id = Id, Version = Version };
    public AnimationEvent Duplicate() => Duplicate(default);
    public AnimationEvent Duplicate(CancellationToken token) => new(SnapshotBytes(Bytes, token)) { Version = Version };
    public IReadOnlyList<AnimationKeyframe> Keyframes(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Type != 12) return [];
        List<AnimationKeyframe> frames = []; int offset = Version == 39 ? 36 : 32;
        BinaryCursor.CheckRange(Bytes.Length, 0, offset);
        int? count = Version == 39 ? I32(16) : null;
        if (count is < 0 || count > (Bytes.Length - offset) / 12)
            throw new InvalidDataException("MW3 keyframe count is negative or exceeds the event payload.");
        while (count is int expected ? frames.Count < expected : offset < Bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            if (count != null && Bytes.Length - offset < 12)
                throw new InvalidDataException("MW3 keyframe count exceeds the available records.");
            BinaryCursor.CheckRange(Bytes.Length, offset, 12);
            int flags = I32(offset), length = 12 + (Version == 39 ? 76 : 28) * System.Numerics.BitOperations.PopCount((uint)flags & 7);
            BinaryCursor.CheckRange(Bytes.Length, offset, length);
            frames.Add(new(Bytes.AsSpan(offset, length).ToArray())); offset += length;
        }
        if (offset != Bytes.Length) throw new InvalidDataException("MW3 keyframe count does not match the event payload; trailing bytes or records remain.");
        return frames;
    }
    public AnimationEvent WithKeyframes(IEnumerable<AnimationKeyframe> frames)
    {
        if (Type != 12) throw new InvalidOperationException("This event has no keyframe stream.");
        using MemoryStream output = new(); output.Write(Bytes.AsSpan(0, Version == 39 ? 36 : 32));
        int count = 0;
        foreach (var frame in frames) { frame.Validate(); output.Write(frame.ForVersion(Version).Bytes); count++; }
        var result = new AnimationEvent(output.ToArray(), SourceOffset) { Id = Id, Version = Version }; result.SetInt(4, result.Bytes.Length); if (Version == 39) result.SetInt(16, count); return result;
    }
    public JsonObject ToPreviewJson(CancellationToken token = default) => ToJson(token, bounded: true);
    public JsonObject ToJson(CancellationToken token = default) => ToJson(token, bounded: false);
    private JsonObject ToJson(CancellationToken token, bool bounded)
    {
        token.ThrowIfCancellationRequested();
        JsonObject value = new() { ["event"] = Name, ["type_id"] = (int)Type, ["start_mode"] = AnimationCatalog.ModeName(StartMode), ["start_threshold"] = JsonData.Number(Threshold), ["record_size"] = Bytes.Length, ["source_offset"] = SourceOffset, ["preview"] = Spec?.Support ?? "Unavailable: unknown event", ["raw_hex"] = bounded ? Convert.ToHexStringLower(Bytes.AsSpan(0, Math.Min(256, Bytes.Length))) : JsonData.Hex(Bytes, token) };
        if (bounded) value["raw_hex_truncated"] = Bytes.Length > 256;
        if (Spec != null) foreach (var field in Spec.Fields.Where(f => f.Offset + f.Size <= Bytes.Length)) value[field.Name] = field.Read(this);
        if (Type == 12 && bounded) value["keyframes_omitted"] = true;
        else if (Type == 12)
        {
            try { value["keyframes"] = JsonData.Array(Keyframes(token), f => f.ToJson(), token); }
            catch (InvalidDataException ex) { value["keyframe_diagnostic"] = ex.Message; }
        }
        return value;
    }
}

public sealed class AnimationKeyframe(byte[] bytes) : AnimationRecord(bytes)
{
    public int Flags => I32(0);
    public float Start { get => F32(4); set => SetFloat(4, value); }
    public float End { get => F32(8); set => SetFloat(8, value); }
    public int ChannelStride => Bytes.Length == 12 + 76 * BitOperations.PopCount((uint)Flags & 7) ? 76 : 28;
    public AnimationKeyframe WithChannels(int flags, uint version)
    {
        if (flags is < 1 or > 7) throw new InvalidDataException("Choose position, rotation and/or scale channels.");
        var original = ForVersion(version); var next = Create(flags).ForVersion(version);
        next.Start = Start; next.End = End;
        for (int channel = 0; channel < 3; channel++)
            if (original.ChannelOffset(channel) is >= 0 and int from && next.ChannelOffset(channel) is >= 0 and int to)
                original.Bytes.AsSpan(from, original.ChannelStride).CopyTo(next.Bytes.AsSpan(to));
        return next;
    }
    public AnimationKeyframe ForVersion(uint version)
    {
        int stride = version == 39 ? 76 : 28; if (stride == ChannelStride) return this;
        var copy = new AnimationKeyframe(new byte[12 + stride * BitOperations.PopCount((uint)Flags & 7)]);
        Bytes.AsSpan(0, 12).CopyTo(copy.Bytes);
        for (int i = 0; i < 3; i++) if (ChannelOffset(i) is int at && at >= 0) Bytes.AsSpan(at, 28).CopyTo(copy.Bytes.AsSpan(copy.ChannelOffset(i)));
        return copy;
    }
    public int ChannelOffset(int channel) => (Flags & (1 << channel)) == 0 ? -1 : 12 + ChannelStride * BitOperations.PopCount((uint)Flags & ((1u << channel) - 1));
    public void Validate()
    {
        if (!float.IsFinite(Start) || !float.IsFinite(End) || Start < 0 || End < Start) throw new InvalidDataException("Keyframe times must be finite, nonnegative, and end at or after the start.");
        if ((Flags & ~7) != 0 || Bytes.Length != 12 + ChannelStride * BitOperations.PopCount((uint)Flags)) throw new InvalidDataException("Invalid keyframe channel layout.");
        for (int channel = 0; channel < 3; channel++)
            if (ChannelOffset(channel) is >= 0 and int start)
                for (int offset = start; offset < start + 28; offset += 4)
                    if (!float.IsFinite(F32(offset))) throw new InvalidDataException("Keyframe channels must be finite.");
    }
    public static AnimationKeyframe Create(int channels = 1)
    {
        var f = new AnimationKeyframe(new byte[12 + 28 * BitOperations.PopCount((uint)channels & 7)]);
        f.SetInt(0, channels & 7); f.End = 1;
        int rotation = f.ChannelOffset(1), scale = f.ChannelOffset(2);
        if (rotation >= 0) f.SetFloat(rotation, 1); // Serialized quaternion is w,x,y,z.
        if (scale >= 0) f.SetVector(scale, Vector3.One);
        return f;
    }
    public JsonObject ToJson()
    {
        JsonObject result = new() { ["flags"] = Flags, ["start_seconds"] = JsonData.Number(Start), ["end_seconds"] = JsonData.Number(End) };
        for (int i = 0; i < 3; i++) if (ChannelOffset(i) is int offset && offset >= 0)
            result[new[] { "position_base_and_rate", "rotation_wxyz_and_rate", "scale_base_and_rate" }[i]] = new JsonArray(Enumerable.Range(0, 7).Select(j => (JsonNode?)JsonData.Number(F32(offset + j * 4))).ToArray());
        return result;
    }
}

public sealed class AnimationSequence(byte[] header, long offset = -1) : AnimationRecord(header)
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public long SourceOffset { get; } = offset;
    public string Name { get => Text(0); set => SetText(0, value); }
    public byte ResetMode { get => Bytes[33]; set { if (value > 3) throw new InvalidDataException("Invalid reset mode."); Bytes[33] = value; } }
    public List<AnimationEvent> Events { get; } = [];
    public byte[] OpaqueTail { get; internal set; } = [];
    public bool IsEditable => OpaqueTail.Length == 0;
    public AnimationSequence Clone(bool newIdentity = false) => Clone(newIdentity, default);
    public AnimationSequence Clone(bool newIdentity, CancellationToken token)
    {
        var copy = new AnimationSequence(SnapshotBytes(Bytes, token), SourceOffset) { Id = newIdentity ? Guid.NewGuid() : Id, OpaqueTail = SnapshotBytes(OpaqueTail, token) };
        foreach (var ev in Events) copy.Events.Add(newIdentity ? ev.Duplicate(token) : ev.Clone(token));
        token.ThrowIfCancellationRequested();
        return copy;
    }
    public override string ToString() => $"{Name} · {Events.Count} events";
}

public sealed class AnimationEntry(byte[] header, int index, long offset) : AnimationRecord(header)
{
    public uint Version => Bytes.Length == 316 ? 39u : 28u;
    public int CountsOffset => Version == 39 ? 264 : 260;
    internal int[] ReferenceLanes => Version == 39 ? [1, 2, 3, 5, 6, 7, 8, 10] : AnimationPackage.ReferenceLanes;
    public List<AnimationRecord> Puffers { get; } = [];
    public int Index { get; } = index;
    public long SourceOffset { get; } = offset;
    public long SourceLength { get; internal set; }
    public string Name => Text(0);
    public string RootName => Text(32);
    public string AttachName => Text(68);
    public List<AnimationRecord>[] References { get; } = Enumerable.Range(0, 8).Select(_ => new List<AnimationRecord>()).ToArray();
    public AnimationSequence Primary { get; internal set; } = new(new byte[64]);
    public byte[] OriginalPrimaryHeader { get; internal set; } = [];
    public List<AnimationSequence> Sequences { get; } = [];
    public IEnumerable<AnimationSequence> AllSequences => new[] { Primary }.Concat(Sequences);
    public AnimationEntry Clone() => Clone(default);
    public AnimationEntry Clone(CancellationToken token)
    {
        var copy = new AnimationEntry(SnapshotBytes(Bytes, token), Index, SourceOffset) { Primary = Primary.Clone(false, token), SourceLength = SourceLength, OriginalPrimaryHeader = SnapshotBytes(OriginalPrimaryHeader, token) };
        for (int i = 0; i < 8; i++)
            foreach (var record in References[i]) copy.References[i].Add(new(SnapshotBytes(record.Bytes, token)));
        foreach (var r in Puffers) copy.Puffers.Add(new(SnapshotBytes(r.Bytes, token)));
        foreach (var sequence in Sequences) copy.Sequences.Add(sequence.Clone(false, token));
        token.ThrowIfCancellationRequested();
        return copy;
    }
    public JsonObject ToJson(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return new()
        {
            ["name"] = Name, ["root"] = RootName, ["attachment"] = AttachName, ["index"] = Index,
            ["source_offset"] = SourceOffset, ["source_length"] = SourceLength, ["header_hex"] = Convert.ToHexStringLower(Bytes),
            ["references"] = JsonData.Array(References, table => JsonData.Array(table, r => JsonValue.Create(JsonData.Hex(r.Bytes, token)), token), token),
            ["puffer_references"] = JsonData.Array(Puffers, r => JsonValue.Create(JsonData.Hex(r.Bytes, token)), token),
            ["sequences"] = JsonData.Array(AllSequences, s => new JsonObject { ["name"] = s.Name, ["phase"] = s == Primary ? "reset_stop" : "runtime", ["reset_state"] = s.ResetMode,
                ["header_hex"] = Convert.ToHexStringLower(s.Bytes), ["events"] = JsonData.Array(s.Events, e => e.ToJson(token), token), ["opaque_tail_hex"] = JsonData.Hex(s.OpaqueTail, token) }, token)
        };
    }
}

public sealed class AnimationPackage
{
    internal static readonly int[] ReferenceLanes = [1, 2, 3, 4, 5, 6, 7, 9];
    internal static readonly int[] ReferenceSizes = [96, 40, 44, 44, 36, 36, 48, 72];
    public uint Version => Prefix.Length >= 8 && BinaryPrimitives.ReadUInt32LittleEndian(Prefix.AsSpan(4)) == 39 ? 39u : 28u;
    public required byte[] Prefix { get; init; }
    public required byte[] Tail { get; init; }
    public List<AnimationEntry> Entries { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
    public static AnimationPackage Read(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        BinaryCursor c = new(bytes);
        if (c.U32() != 0x08170616) throw new InvalidDataException("Invalid animation signature.");
        uint version = c.U32(); if (version is not (28 or 39)) throw new InvalidDataException("Supported animation versions are 28 and 39.");
        int headerSize = version == 39 ? 316 : 308;
        int stamps = c.Count(c.U32(), 84); c.Skip(checked(stamps * 84)); var globals = new AnimationRecord(c.Take(version == 39 ? 68 : 60).ToArray());
        int entryCount = (int)(globals.U32(8) >> 16); c.Count((uint)entryCount, headerSize);
        byte[] prefix = bytes[..c.Position].ToArray(); List<AnimationEntry> entries = []; List<Diagnostic> diagnostics = [];
        for (int i = 0; i < entryCount; i++)
        {
            token.ThrowIfCancellationRequested(); int start = c.Position; AnimationEntry entry = new(c.Take(headerSize).ToArray(), i, start);
            for (int table = 0; table < 8; table++)
            {
                if (version == 39 && table == 3)
                {
                    int puffers = c.Count(entry.Bytes[268], 44);
                    for (int j = 0; j < puffers; j++) entry.Puffers.Add(new(c.Take(44).ToArray()));
                }
                int count = entry.Bytes[entry.CountsOffset + entry.ReferenceLanes[table]], size = ReferenceSizes[table]; c.Count((uint)count, size);
                for (int j = 0; j < count; j++) entry.References[table].Add(new(c.Take(size).ToArray()));
            }
            entry.Primary = ReadSequence(c); entry.OriginalPrimaryHeader = (byte[])entry.Primary.Bytes.Clone();
            for (int j = 0; j < entry.Bytes[entry.CountsOffset]; j++) entry.Sequences.Add(ReadSequence(c));
            entry.SourceLength = c.Position - start; entries.Add(entry);
        }
        AnimationPackage package = new() { Prefix = prefix, Tail = c.Take(c.Remaining).ToArray() }; package.Entries.AddRange(entries); package.Diagnostics.AddRange(diagnostics); return package;

        AnimationSequence ReadSequence(BinaryCursor input)
        {
            long offset = input.AbsolutePosition; var seq = new AnimationSequence(input.Take(64).ToArray(), offset);
            var payload = input.Take(seq.I32(60)); int at = 0;
            while (at < payload.Length)
            {
                token.ThrowIfCancellationRequested();
                if (payload.Length - at < 12) { Preserve("Truncated event header."); break; }
                int length = BinaryPrimitives.ReadInt32LittleEndian(payload.Span.Slice(at + 4, 4));
                if (length < 12 || length > payload.Length - at) { Preserve("Invalid event size."); break; }
                seq.Events.Add(new(payload.Slice(at, length).ToArray(), offset + 64 + at) { Version = version }); at += length;
            }
            return seq;
            void Preserve(string message)
            {
                seq.OpaqueTail = payload[at..].ToArray(); diagnostics.Add(new("Warning", $"{seq.Name}: {message} Preserved bytes; sequence is read-only.", Offset: offset + 64 + at));
            }
        }
    }
}
