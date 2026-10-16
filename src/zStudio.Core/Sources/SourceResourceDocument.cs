using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

/// <summary>A real project resource, retaining source syntax identities rather than compiled member offsets.</summary>
public sealed class SourceResourceDocument : AssetDocument
{
    public SourceSceneResource Source { get; }
    private readonly Dictionary<int, ZrdNode> scalars = [];
    public SourceResourceDocument(string root, SourceSceneResource source, CancellationToken token = default)
        : base(System.IO.Path.Combine(root, source.Path), FileStamp.Unverified,
            new(FormatFamily.Zrd, null, Recognition.Supported, "Source resource"), source.Bytes)
    {
        if (source.Root == null) throw new InvalidDataException($"{source.Path} is not a valid source resource.");
        Source = source; SourceSyntax = source.Syntax == null ? null : "zrd-text";
        Add(AssetKind.Zrd, 0, System.IO.Path.GetFileName(source.Path), 0, source.Bytes.Length, content: source.Root);
        Index(source.Root);
        void Index(ZrdNode node)
        {
            token.ThrowIfCancellationRequested();
            if (node.Kind is ZrdKind.Float or ZrdKind.Int)
            {
                int key = checked((int)(source.Syntax?.SpanOf(node.Id).Start ?? node.SourceOffset));
                if (!scalars.TryAdd(key, node)) throw new InvalidDataException("Ambiguous resource scalar identity.");
            }
            foreach (var child in node.Children) Index(child);
        }
    }
    internal int ScalarKey(ZrdNode node) => checked((int)(Source.Syntax?.SpanOf(node.Id).Start ?? node.SourceOffset));
    internal byte[] Replace(IReadOnlyDictionary<int, float> values, CancellationToken token)
    {
        Dictionary<Guid, ZrdNode> replacements = [];
        foreach (var (key, value) in values)
        {
            token.ThrowIfCancellationRequested();
            if (!scalars.TryGetValue(key, out var original)) throw new InvalidDataException("The resource scalar no longer belongs to this source revision.");
            replacements.Add(original.Id, SourceResourceEdits.Float(value));
        }
        if (Source.Syntax != null) return ZrdTextSyntax.Encode(Source.Syntax.ReplaceScalars(replacements, token));
        // An originally binary .zrd remains binary; only the selected scalar words change.
        byte[] bytes = Source.Bytes.ToArray();
        foreach (var (key, value) in values)
        {
            BinaryCursor.CheckRange(bytes.Length, key, 8);
            var scalar = SourceResourceEdits.Float(value);
            var encoded = ZrdWriter.Write(scalar, token);
            if (encoded.Length != 8) throw new InvalidDataException("Expected a numeric scalar.");
            encoded.CopyTo(bytes, key);
        }
        _ = ZrdDecoder.Read(bytes, token); return bytes;
    }
}
