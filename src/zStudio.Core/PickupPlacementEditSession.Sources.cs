using System.Buffers.Binary;
using System.Security.Cryptography;
using zStudio.Core.Formats;
using zStudio.Core.Worlds;
using zStudio.Core.Sources;

namespace zStudio.Core;

public sealed partial class PickupPlacementEditSession
{
    public static PickupPlacementEditSession FromSources(SourceSceneSnapshot snapshot, CancellationToken token = default)
    {
        var documents = snapshot.Resources.Where(pair => pair.Value.Root != null).ToDictionary(pair => pair.Key, pair => new SourceResourceDocument(snapshot.Root, pair.Value, token), StringComparer.OrdinalIgnoreCase);
        List<PickupPlacementResource> resources = [];
        foreach (var difficulty in Enum.GetValues<MissionDifficulty>())
        {
            string requested = MissionLayoutSelection.For(difficulty).PickupResource;
            if (!documents.TryGetValue(requested, out var document) && !snapshot.Resources.ContainsKey(requested)) documents.TryGetValue("puppies.zrd", out document);
            if (document != null) resources.Add(new(difficulty, document, document.Assets[0]));
        }
        var result = Create(resources, token: token);
        result.AddCoordinates(documents.Values.Select(d => ((AssetDocument)d, d.Assets[0])), token, presentResources: snapshot.Resources.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));
        result.BindCoordinateTemplates(snapshot.Scene!);
        result.PrepareSourcePresentationKeys(token);
        return result;
    }

    private readonly Dictionary<MissionPickupSource, string> sourcePresentationKeys = [];

    /// <summary>Stable source record identity across coordinate edits, never a text byte offset or complete file hash.</summary>
    public string? SourcePresentationKey(MissionPickupSource source) => sourcePresentationKeys.GetValueOrDefault(source);

    private void PrepareSourcePresentationKeys(CancellationToken token)
    {
        foreach (var group in entries.GroupBy(pair => pair.Key.ArchivePath, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            if (archives[group.Key].Original is not SourceResourceDocument document) continue;
            var ordered = group.OrderBy(pair => pair.Value.Offsets[0]).ToArray();
            var editable = ordered.SelectMany(pair => pair.Value.Offsets.Concat(pair.Value.RotationOffsets)).ToHashSet();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] textBuffer = new byte[1024];
            Visit(document.Source.Root!);
            string fingerprint = Convert.ToHexString(hash.GetHashAndReset());
            for (int ordinal = 0; ordinal < ordered.Length; ordinal++)
            {
                token.ThrowIfCancellationRequested();
                sourcePresentationKeys[ordered[ordinal].Key] = fingerprint + ":" + ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            void Number(uint value)
            {
                Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, value); hash.AppendData(bytes);
            }
            void Visit(ZrdNode node)
            {
                token.ThrowIfCancellationRequested();
                if (node.Kind is ZrdKind.Int or ZrdKind.Float && editable.Contains(document.ScalarKey(node)))
                {
                    // Replacement may change numeric syntax kind or token width; both represent the same editable field.
                    Number(uint.MaxValue); return;
                }
                Number((uint)node.Kind); Number(node.Bits); Number(checked((uint)node.Children.Count)); Number(checked((uint)node.Text.Length));
                for (int offset = 0; offset < node.Text.Length;)
                {
                    token.ThrowIfCancellationRequested();
                    int count = Math.Min(textBuffer.Length / 2, node.Text.Length - offset);
                    for (int i = 0; i < count; i++) BinaryPrimitives.WriteUInt16LittleEndian(textBuffer.AsSpan(i * 2, 2), node.Text[offset + i]);
                    hash.AppendData(textBuffer.AsSpan(0, count * 2)); offset += count;
                }
                foreach (var child in node.Children) Visit(child);
            }
        }
    }

    public IReadOnlyList<(string Relative, byte[] Content)> SourceChanges(IReadOnlyDictionary<MissionPickupSource, PlacementTransform> values,
        SourceWorkspace workspace, CancellationToken token = default)
    {
        List<(string, byte[])> changes = [];
        foreach (var group in ScalarWrites(values).GroupBy(value => value.ArchivePath, StringComparer.OrdinalIgnoreCase))
        {
            if (archives[group.Key].Original is not SourceResourceDocument document) throw new InvalidDataException("This placement does not belong to a source resource.");
            string path = document.Source.Path;
            if (workspace.Read(path, token, ProjectReadLimits.Resource()) is not { } current || !current.AsSpan().SequenceEqual(document.Source.Bytes))
                throw new SourceFileChangedException($"{path} changed; reload the map before editing its placements.", [path]);
            changes.Add((path, document.Replace(group.ToDictionary(value => value.Offset, value => value.Value), token)));
        }
        return changes;
    }
}
