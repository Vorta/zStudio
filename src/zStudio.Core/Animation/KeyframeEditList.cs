using System.Collections;

namespace Recoil.Zbd.Core.Animation;

/// <summary>An editable ordering over a lazy stream. Only explicitly accessed frames become mutable copies.</summary>
public sealed class KeyframeEditList(IReadOnlyList<AnimationKeyframe> source) : IList<AnimationKeyframe>
{
    private readonly List<int> order = Enumerable.Range(0, source.Count).ToList();
    private readonly Dictionary<int, AnimationKeyframe> edits = [];
    private int next = -1;
    public int Count => order.Count;
    public bool IsReadOnly => false;
    public AnimationKeyframe this[int index]
    {
        get { int id = order[index]; if (!edits.TryGetValue(id, out var frame)) edits.Add(id, frame = new((byte[])source[id].Bytes.Clone())); return frame; }
        set { int id = next--; edits.Add(id, value); order[index] = id; }
    }
    public void Add(AnimationKeyframe item) => Insert(Count, item);
    public void Insert(int index, AnimationKeyframe item) { int id = next--; order.Insert(index, id); edits.Add(id, item); }
    public void RemoveAt(int index) => order.RemoveAt(index);
    public void Clear() { order.Clear(); edits.Clear(); }
    public bool Contains(AnimationKeyframe item) => IndexOf(item) >= 0;
    public int IndexOf(AnimationKeyframe item) { for (int i = 0; i < Count; i++) if (edits.TryGetValue(order[i], out var frame) && ReferenceEquals(frame, item)) return i; return -1; }
    public bool Remove(AnimationKeyframe item) { int index = IndexOf(item); if (index < 0) return false; RemoveAt(index); return true; }
    public void CopyTo(AnimationKeyframe[] array, int arrayIndex) { foreach (var frame in this) array[arrayIndex++] = frame; }
    public IEnumerator<AnimationKeyframe> GetEnumerator()
    {
        // Structural edits touch a few frames. Sequential spans retain linear decoding,
        // including the untouched tail of million-record streams.
        var cursor = source.GetEnumerator(); int at = -1;
        try
        {
            foreach (int id in order)
            {
                if (edits.TryGetValue(id, out var edited)) { yield return edited; continue; }
                if (id < at) { cursor.Dispose(); cursor = source.GetEnumerator(); at = -1; }
                while (at < id) { if (!cursor.MoveNext()) throw new InvalidDataException("The keyframe source changed during editing."); at++; }
                yield return cursor.Current;
            }
        }
        finally { cursor.Dispose(); }
    }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
