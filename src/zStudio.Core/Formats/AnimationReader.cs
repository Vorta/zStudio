using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Core.Formats;

internal sealed class AnimationReader : IZbdFormatReader
{
    public FormatFamily Family => FormatFamily.Animation;
    public void Read(ZbdDocument doc, CancellationToken token)
    {
        doc.Animations = AnimationPackage.Read(doc.Bytes, token);
        doc.Diagnostics.AddRange(doc.Animations.Diagnostics);
        BinaryCursor header = new(doc.Bytes); header.Skip(8);
        int stampCount = header.Count(header.U32(), 84); JsonArray stamps = [];
        int shown = Math.Min(16, stampCount);
        for (int i = 0; i < shown; i++) { token.ThrowIfCancellationRequested(); stamps.Add(FieldLayouts.Read(header, 84, "ANIM_STAMP_LAYOUT")); }
        header.Skip(checked((stampCount - shown) * 84));
        doc.Metadata["stamps"] = stamps; doc.Metadata["stamp_count"] = stampCount; doc.Metadata["stamps_truncated"] = shown < stampCount;
        if (doc.Animations.Version == 39) doc.Metadata["globals_raw"] = Convert.ToHexStringLower(header.Take(68).Span);
        else doc.Metadata["globals"] = FieldLayouts.Read(header, 60, "ANIM_GLOBALS_LAYOUT");
        foreach (var entry in doc.Animations.Entries)
        {
            token.ThrowIfCancellationRequested();
            string name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Primary.Name : entry.Name;
            var asset = doc.Add(AssetKind.Animation, entry.Index, string.IsNullOrWhiteSpace(name) ? $"Animation {entry.Index}" : name,
                entry.SourceOffset, entry.SourceLength, entry.ToPreviewJson(token), entry);
            asset.Summary = $"{entry.Sequences.Count + 1} sequences · {entry.References[1].Count} node references";
        }
        if (doc.Animations.Tail.Length != 0) doc.Diagnostics.Add(new("Warning", $"{doc.Animations.Tail.Length} trailing animation bytes preserved.", Offset: doc.Bytes.Length - doc.Animations.Tail.Length));
    }
}
