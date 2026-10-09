using System.Buffers.Binary;
using System.Collections;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core.Animation;

/// <summary>Frame edits belong to this view, never the event's validated source or another caller.</summary>
internal sealed class EditableKeyframeView(IReadOnlyList<AnimationKeyframe> source) : IReadOnlyList<AnimationKeyframe>
{
    private readonly Dictionary<int, AnimationKeyframe> changed = [];
    public int Count => source.Count;
    public AnimationKeyframe this[int index]
    {
        get
        {
            if (changed.TryGetValue(index, out var edited)) return edited;
            var frame = source[index]; frame.Modified = () => changed[index] = frame; return frame;
        }
    }
    public IEnumerator<AnimationKeyframe> GetEnumerator()
    {
        int index = 0;
        foreach (var frame in source)
        {
            int current = index++;
            if (changed.TryGetValue(current, out var edited)) yield return edited;
            else { frame.Modified = () => changed[current] = frame; yield return frame; }
        }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Validate every record, but allocate only a sparse offset index and explicitly accessed frame copies.</summary>
internal sealed class KeyframeStream : IReadOnlyList<AnimationKeyframe>
{
    private const int Stride = 256;
    private readonly byte[] bytes;
    private readonly int channelSize;
    private readonly int[] blocks;
    public int Count { get; }
    /// <summary>First reversed or negative-time record, or -1. Its playback meaning depends on the game version.</summary>
    public int FirstUnordered { get; } = -1;
    private KeyframeStream(byte[] bytes, int channelSize, int[] blocks, int count, int firstUnordered)
    { this.bytes = bytes; this.channelSize = channelSize; this.blocks = blocks; Count = count; FirstUnordered = firstUnordered; }
    internal KeyframeStream ForSnapshot(byte[] snapshot) => new(snapshot, channelSize, blocks, Count, FirstUnordered);
    internal void Retained(RetainedDocumentBudget budget, CancellationToken token)
    {
        if (!budget.Object(this, 128, token)) return;
        budget.Bytes(bytes, token);
        budget.Object(blocks, 32L + 4L * blocks.Length, token);
    }
    public KeyframeStream(byte[] bytes, uint version, CancellationToken token)
    {
        this.bytes = bytes; channelSize = version == 39 ? 76 : 28;
        int offset = version == 39 ? 36 : 32; BinaryCursor.CheckRange(bytes.Length, 0, offset);
        int? expected = version == 39 ? BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4)) : null;
        if (expected is < 0 || expected > (bytes.Length - offset) / 12) throw new InvalidDataException("MW3 keyframe count is negative or exceeds the event payload.");
        List<int> index = []; int count = 0;
        while (expected is int n ? count < n : offset < bytes.Length)
        {
            token.ThrowIfCancellationRequested();
            if (count % Stride == 0) index.Add(offset);
            int length;
            try { length = Length(offset); }
            catch (InvalidDataException ex) when (expected != null) { throw new InvalidDataException("MW3 keyframe count exceeds the available complete records.", ex); }
            try { AnimationKeyframe.Validate(bytes.AsSpan(offset, length), channelSize); }
            catch (InvalidDataException ex) { throw new InvalidDataException($"Keyframe {count}: {ex.Message}", ex); }
            if (FirstUnordered < 0 && !AnimationKeyframe.IsOrdered(bytes.AsSpan(offset, length))) FirstUnordered = count;
            offset += length; count++;
        }
        if (offset != bytes.Length) throw new InvalidDataException("MW3 keyframe count does not match the event payload; trailing bytes or records remain.");
        blocks = index.ToArray(); Count = count;
    }
    private int Length(int offset)
    {
        BinaryCursor.CheckRange(bytes.Length, offset, 12);
        int flags = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
        int length = 12 + channelSize * System.Numerics.BitOperations.PopCount((uint)flags & 7);
        BinaryCursor.CheckRange(bytes.Length, offset, length); return length;
    }
    public AnimationKeyframe this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index); ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            int offset = blocks[index / Stride];
            for (int n = index / Stride * Stride; n < index; n++) offset += Length(offset);
            return new(bytes.AsSpan(offset, Length(offset)).ToArray());
        }
    }
    public IEnumerator<AnimationKeyframe> GetEnumerator()
    {
        int offset = blocks.Length == 0 ? 0 : blocks[0];
        for (int i = 0; i < Count; i++) { int length = Length(offset); yield return new(bytes.AsSpan(offset, length).ToArray()); offset += length; }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
