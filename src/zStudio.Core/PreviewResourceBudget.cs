using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

/// <summary>One preview load's retained resources, shared by mission, sound and prepared-script discovery.</summary>
internal sealed class PreviewResourceBudget
{
    internal PreviewResourceBudget(long maximumBytes = TextureLookupOperation.MaximumColdReadBytes,
        long maximumDecodedBytes = MaximumDecodedBytes)
        : this(new AssetReadBudget(maximumBytes, maximumBytes), maximumDecodedBytes) { }

    internal PreviewResourceBudget(AssetReadBudget reads, long maximumDecodedBytes = MaximumDecodedBytes)
    { Reads = reads; content = new(maximumDecodedBytes); }

    // Conservative retained-graph estimates for the six original MW3 missions reach 608 MiB.
    // Keep decoded headroom separate from the unchanged raw-file and interpretation-work limits.
    internal const long MaximumDecodedBytes = 768L * 1024 * 1024;
    internal AssetReadBudget Reads { get; }
    private readonly Dictionary<string, ZbdDocument> documents = new(StringComparer.OrdinalIgnoreCase);
    private readonly RetainedDocumentBudget content;
    private long remainingWork = 64L * 1024 * 1024;
    private long remainingMemberBytes = 512L * 1024 * 1024;
    internal bool Exhausted => Reads.Exhausted || content.Exhausted || remainingWork < 0 || remainingMemberBytes < 0;
    internal void Member(long bytes, CancellationToken token)
    {
        Check(token);
        // Hashing and WAV scans pay a conservative byte bound, separately from tree/node work.
        // Repeated members and sound aliases pay on every call, even when their backing bytes are shared.
        if (bytes < 0 || bytes > remainingMemberBytes) { remainingMemberBytes = -1; Check(token); }
        remainingMemberBytes -= bytes;
    }
    internal void LookupEntry(string name, CancellationToken token)
    { Work(1L + name.Length, token); Reserve(96, token); }
    private void Work(long units, CancellationToken token)
    {
        Check(token);
        if (units < 0 || units > remainingWork) { remainingWork = -1; Check(token); }
        remainingWork -= units;
    }

    // A shared serialized tree may be interpreted once per member identity. Its decoded objects are
    // admitted once above; derived projections must pay per interpretation before constructing lists.
    internal void Interpret(ZrdNode tree, CancellationToken token, int depth = 0)
    {
        Check(token);
        if (depth > 256) { remainingWork = -1; Check(token); }
        Work(1L + tree.Children.Count + tree.Text.Length, token);
        Reserve(512L + 2L * tree.Text.Length + 16L * tree.Children.Count, token);
        foreach (var child in tree.Children) Interpret(child, token, depth + 1);
    }

    internal async Task<ZbdDocument> OpenAsync(string path, AssetResolver resolver, CancellationToken token)
    {
        Check(token);
        if (documents.TryGetValue(path, out var retained)) return retained;
        var document = await resolver.OpenCachedAsync(path, FormatRegistry.MaximumDocumentBytes, token, Reads).ConfigureAwait(false);
        Retain(document, token);
        return document;
    }

    internal void Retain(ZbdDocument document, CancellationToken token)
    {
        Check(token);
        if (documents.TryGetValue(document.Path, out var prior))
        {
            // Never hide a second backing buffer behind the same path's raw-byte reservation.
            if (!ReferenceEquals(prior, document)) throw new InvalidDataException("Preview resource identity changed during loading; retry the preview.");
            return;
        }
        Reads.Document(document.Path, document.Bytes.Length);
        try { content.Document(document, token); }
        catch (InvalidDataException) when (content.Exhausted) { Check(token); throw; }
        documents.Add(document.Path, document);
    }

    private void Reserve(long bytes, CancellationToken token)
    {
        Check(token);
        try { content.Reserve(bytes, token); }
        catch (InvalidDataException) when (content.Exhausted) { Check(token); throw; }
    }
    internal void Check(CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); Reads.Check();
        if (content.Exhausted) throw new InvalidDataException("Preview resources exceed the shared decoded-content allowance; use fewer resource files and retry the preview.");
        if (remainingWork < 0) throw new InvalidDataException("Preview resources exceed the shared structural interpretation allowance; use fewer resource files and retry the preview.");
        if (remainingMemberBytes < 0) throw new InvalidDataException("Preview resources exceed the shared member-byte processing allowance; use fewer resource files and retry the preview.");
    }
}
