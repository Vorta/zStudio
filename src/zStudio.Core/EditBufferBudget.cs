using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

/// <summary>Raw buffers retained by one texture preparation, including originals and complete replacements.
/// This is an admission bound, not an estimate of parsed object or undo-history heap usage.</summary>
internal sealed class EditBufferBudget
{
    private readonly HashSet<object> documents = new(ReferenceEqualityComparer.Instance);
    public long Remaining { get; private set; }
    public EditBufferBudget(long maximumBytes)
    {
        if (maximumBytes < 0 || maximumBytes > FormatRegistry.MaximumDocumentBytes) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        Remaining = maximumBytes;
    }
    public void Document(ZbdDocument document)
    {
        if (documents.Contains(document)) return;
        Reserve(document.Bytes.Length); documents.Add(document);
    }
    public void Reserve(long bytes)
    {
        if (bytes < 0 || bytes > Remaining) throw new InvalidDataException("Texture edit buffers exceed the shared 512 MiB preparation budget.");
        Remaining -= bytes;
    }
}
