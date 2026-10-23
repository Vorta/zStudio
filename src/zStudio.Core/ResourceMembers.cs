using System.Collections.Concurrent;
using zStudio.Core.Formats;

namespace zStudio.Core;

public sealed record ResourceMember(Guid Id, int? SourceIndex, string Name, ReadOnlyMemory<byte> Data, ZrdNode? Tree = null, bool TypedDecodeLimited = false);

/// <summary>
/// The members of one archive (with their embedded ZRD trees) or of one standalone ZRD, read for inspection: each member and
/// each decoded node keeps one identity (GUID) for as long as the document is open.
/// </summary>
public sealed class ResourceMembers
{
    private readonly AssetDocument source;
    private readonly ConcurrentDictionary<Guid, ZrdNode> trees = new();
    public IReadOnlyList<ResourceMember> Members { get; }
    public AssetDocument Document => source;
    public bool IsArchive => source.Probe.Family == FormatFamily.Archive;

    public ResourceMembers(AssetDocument document, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (document.Probe.Family is not (FormatFamily.Archive or FormatFamily.Zrd) || document.Diagnostics.Any(d => d.Severity == "Error"))
            throw new InvalidDataException("An intact ZAR archive or standalone ZRD is required.");
        source = document;
        bool text = document.SourceSyntax == "zrd-text";
        var standalone = IsArchive ? null : document.Assets.SingleOrDefault()?.Content as ZrdNode
            ?? (text ? Sources.ZrdText.Parse(document.Bytes.Span, token) : ZrdDecoder.Read(document.Bytes, token));
        // A source text .zrd is shown as the compiled member the game reads.
        ReadOnlyMemory<byte> compiled = text ? ZrdWriter.Write(standalone!, token) : default;
        List<ResourceMember> members = [];
        foreach (var a in document.Assets)
        {
            token.ThrowIfCancellationRequested();
            members.Add(new(Guid.NewGuid(), a.Index, a.Name, text ? compiled : document.Slice(a.Offset, a.Length), standalone ?? a.Content as ZrdNode,
                a.Metadata["typed_decode_limited"]?.GetValue<bool>() == true));
        }
        Members = members.ToArray();
    }
    public ResourceMember Member(Guid id) => Members.SingleOrDefault(m => m.Id == id) ?? throw new InvalidDataException("The archive member no longer exists.");
    public ZrdNode Tree(ResourceMember member, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (member.TypedDecodeLimited) throw new InvalidDataException("This archive member exceeds the shared typed-decoding budget; use raw inspection or exact member export.");
        return member.Tree ?? trees.GetOrAdd(member.Id, _ => ZrdDecoder.Read(member.Data, token));
    }
}
