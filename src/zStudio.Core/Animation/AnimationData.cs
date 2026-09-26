using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Animation;

/// <summary>Little-endian serialized data. Pointer-shaped fields are never dereferenced.</summary>
public class AnimationRecord(byte[] bytes)
{
    public byte[] Bytes { get; } = bytes;
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
    public byte Type => Bytes[0];
    public byte StartMode { get => Bytes[1]; set { if (value is < 1 or > 3) throw new InvalidDataException("Unknown timing mode."); Bytes[1] = value; } }
    public float Threshold { get => F32(8); set => SetFloat(8, value); }
    public AnimationEventSpec? Spec => AnimationCatalog.Find(Type);
    public string Name => Spec?.Name ?? $"Unknown event 0x{Type:X2}";
    public AnimationEvent Clone() => new((byte[])Bytes.Clone(), SourceOffset) { Id = Id };
    public AnimationEvent Duplicate() => new((byte[])Bytes.Clone());
    public IReadOnlyList<AnimationKeyframe> Keyframes(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Type != 12) return [];
        List<AnimationKeyframe> frames = []; int offset = 32;
        while (offset < Bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            BinaryCursor.CheckRange(Bytes.Length, offset, 12);
            int flags = I32(offset), length = 12 + 28 * System.Numerics.BitOperations.PopCount((uint)flags & 7);
            BinaryCursor.CheckRange(Bytes.Length, offset, length);
            frames.Add(new(Bytes.AsSpan(offset, length).ToArray())); offset += length;
        }
        return frames;
    }
    public AnimationEvent WithKeyframes(IEnumerable<AnimationKeyframe> frames)
    {
        if (Type != 12) throw new InvalidOperationException("This event has no keyframe stream.");
        using MemoryStream output = new(); output.Write(Bytes.AsSpan(0, 32));
        foreach (var frame in frames) { frame.Validate(); output.Write(frame.Bytes); }
        var result = new AnimationEvent(output.ToArray(), SourceOffset) { Id = Id }; result.SetInt(4, result.Bytes.Length); return result;
    }
    public JsonObject ToJson(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        JsonObject value = new() { ["event"] = Name, ["type_id"] = (int)Type, ["start_mode"] = AnimationCatalog.ModeName(StartMode), ["start_threshold"] = JsonData.Number(Threshold), ["record_size"] = Bytes.Length, ["source_offset"] = SourceOffset, ["preview"] = Spec?.Support ?? "Unavailable: unknown event", ["raw_hex"] = JsonData.Hex(Bytes, token) };
        if (Spec != null) foreach (var field in Spec.Fields.Where(f => f.Offset + f.Size <= Bytes.Length)) value[field.Name] = field.Read(this);
        if (Type == 12)
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
    public int ChannelOffset(int channel) => (Flags & (1 << channel)) == 0 ? -1 : 12 + 28 * BitOperations.PopCount((uint)Flags & ((1u << channel) - 1));
    public void Validate()
    {
        if (!float.IsFinite(Start) || !float.IsFinite(End) || Start < 0 || End < Start) throw new InvalidDataException("Keyframe times must be finite, nonnegative, and end at or after the start.");
        for (int offset = 12; offset < Bytes.Length; offset += 4) if (!float.IsFinite(F32(offset))) throw new InvalidDataException("Keyframe channels must be finite.");
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
    public AnimationSequence Clone(bool newIdentity = false)
    {
        var copy = new AnimationSequence((byte[])Bytes.Clone(), SourceOffset) { Id = newIdentity ? Guid.NewGuid() : Id, OpaqueTail = (byte[])OpaqueTail.Clone() };
        copy.Events.AddRange(Events.Select(e => newIdentity ? e.Duplicate() : e.Clone())); return copy;
    }
    public override string ToString() => $"{Name} · {Events.Count} events";
}

public sealed class AnimationEntry(byte[] header, int index, long offset) : AnimationRecord(header)
{
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
    public AnimationEntry Clone()
    {
        var copy = new AnimationEntry((byte[])Bytes.Clone(), Index, SourceOffset) { Primary = Primary.Clone(), SourceLength = SourceLength, OriginalPrimaryHeader = OriginalPrimaryHeader };
        for (int i = 0; i < 8; i++) copy.References[i].AddRange(References[i].Select(r => new AnimationRecord((byte[])r.Bytes.Clone())));
        copy.Sequences.AddRange(Sequences.Select(s => s.Clone())); return copy;
    }
    public JsonObject ToJson(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        return new()
        {
            ["name"] = Name, ["root"] = RootName, ["attachment"] = AttachName, ["index"] = Index,
            ["source_offset"] = SourceOffset, ["source_length"] = SourceLength, ["header_hex"] = Convert.ToHexStringLower(Bytes),
            ["references"] = JsonData.Array(References, table => JsonData.Array(table, r => JsonValue.Create(JsonData.Hex(r.Bytes, token)), token), token),
            ["sequences"] = JsonData.Array(AllSequences, s => new JsonObject { ["name"] = s.Name, ["phase"] = s == Primary ? "reset_stop" : "runtime", ["reset_state"] = s.ResetMode,
                ["header_hex"] = Convert.ToHexStringLower(s.Bytes), ["events"] = JsonData.Array(s.Events, e => e.ToJson(token), token), ["opaque_tail_hex"] = JsonData.Hex(s.OpaqueTail, token) }, token)
        };
    }
}

public sealed class AnimationPackage
{
    internal static readonly int[] ReferenceLanes = [1, 2, 3, 4, 5, 6, 7, 9];
    internal static readonly int[] ReferenceSizes = [96, 40, 44, 44, 36, 36, 48, 72];
    public required byte[] Prefix { get; init; }
    public required byte[] Tail { get; init; }
    public List<AnimationEntry> Entries { get; } = [];
    public List<Diagnostic> Diagnostics { get; } = [];
    public static AnimationPackage Read(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        BinaryCursor c = new(bytes);
        if (c.U32() != 0x08170616 || c.U32() != 28) throw new InvalidDataException("Only animation version 28 is editable.");
        int stamps = c.Count(c.U32(), 84); c.Skip(checked(stamps * 84)); var globals = new AnimationRecord(c.Take(60).ToArray());
        int entryCount = (int)(globals.U32(8) >> 16); c.Count((uint)entryCount, 308);
        byte[] prefix = bytes[..c.Position].ToArray(); List<AnimationEntry> entries = []; List<Diagnostic> diagnostics = [];
        for (int i = 0; i < entryCount; i++)
        {
            token.ThrowIfCancellationRequested(); int start = c.Position; AnimationEntry entry = new(c.Take(308).ToArray(), i, start);
            for (int table = 0; table < 8; table++)
            {
                int count = entry.Bytes[260 + ReferenceLanes[table]], size = ReferenceSizes[table]; c.Count((uint)count, size);
                for (int j = 0; j < count; j++) entry.References[table].Add(new(c.Take(size).ToArray()));
            }
            entry.Primary = ReadSequence(c); entry.OriginalPrimaryHeader = (byte[])entry.Primary.Bytes.Clone();
            for (int j = 0; j < entry.Bytes[260]; j++) entry.Sequences.Add(ReadSequence(c));
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
                seq.Events.Add(new(payload.Slice(at, length).ToArray(), offset + 64 + at)); at += length;
            }
            return seq;
            void Preserve(string message)
            {
                seq.OpaqueTail = payload[at..].ToArray(); diagnostics.Add(new("Warning", $"{seq.Name}: {message} Preserved bytes; sequence is read-only.", Offset: offset + 64 + at));
            }
        }
    }
}
