using System.Collections;

namespace Recoil.Zbd.Core;

/// <summary>Bound notices before retaining or publishing them; source records remain independently inspectable.</summary>
public sealed class PreviewNotes : IReadOnlyList<string>
{
    public const int MaximumItems = 256;
    private readonly List<string> items = [];
    public int TotalCount { get; private set; }
    public int Count => items.Count + (TotalCount > items.Count ? 1 : 0);
    public string this[int index] => index < items.Count ? items[index] : index == items.Count && TotalCount > items.Count
        ? $"{TotalCount - items.Count:N0} additional preview notices omitted. Inspect the source resources for the complete authored records."
        : throw new ArgumentOutOfRangeException(nameof(index));
    public void Add(string message)
    {
        TotalCount++;
        if (items.Count < MaximumItems) items.Add(message.Length <= 1024 ? message : message[..1024] + "…");
    }
    public void AddRange(IEnumerable<string> messages)
    {
        if (messages is PreviewNotes notes)
        {
            foreach (string message in notes.items) Add(message);
            TotalCount += notes.TotalCount - notes.items.Count;
        }
        else foreach (string message in messages) Add(message);
    }
    public IEnumerator<string> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
