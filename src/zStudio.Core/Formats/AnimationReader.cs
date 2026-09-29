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
        int keyframeFailures = 0;
        foreach (var entry in doc.Animations.Entries)
        {
            token.ThrowIfCancellationRequested();
            // Opening already runs on the background parser. Prepare sparse indices
            // here so selecting a large event never validates its payload on the UI thread.
            foreach (var ev in entry.AllSequences.SelectMany(s => s.Events).Where(e => e.Type == 12))
                try { _ = ev.Keyframes(token); }
                catch (InvalidDataException ex)
                {
                    if (++keyframeFailures <= 256) doc.Diagnostics.Add(new("Warning", $"Animation {entry.Index}: {ex.Message} Source event retained; transform preview unavailable.", entry.Index, ev.SourceOffset));
                }
            string name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Primary.Name : entry.Name;
            var asset = doc.Add(AssetKind.Animation, entry.Index, string.IsNullOrWhiteSpace(name) ? $"Animation {entry.Index}" : name,
                entry.SourceOffset, entry.SourceLength, entry.ToPreviewJson(token), entry);
            asset.Summary = $"{entry.Sequences.Count + 1} sequences · {entry.References[1].Count} node references";
        }
        if (keyframeFailures > 256) doc.Diagnostics.Add(new("Warning", $"{keyframeFailures - 256} additional invalid transform streams omitted; each event retains its inspection diagnostic."));
        if (doc.Animations.Tail.Length != 0) doc.Diagnostics.Add(new("Warning", $"{doc.Animations.Tail.Length} trailing animation bytes preserved.", Offset: doc.Bytes.Length - doc.Animations.Tail.Length));
    }
}
