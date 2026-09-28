using System.Numerics;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record MissionCoordinateRecord(MissionPickupSource Source, string Kind, string Name,
    Vector3 OriginalPosition, Vector3 Rotation, string Template, long SourceOffset,
    IReadOnlyList<MissionDifficulty> Difficulties)
{
    public int? TemplateSourceNode { get; init; }
    public bool MissionSpecific { get; init; }
}

public sealed partial class PickupPlacementEditSession
{
    private static bool IsCoordinateResource(string name) => MissionAiNetworks.IsCandidate(name) ||
        name.ToLowerInvariant() is "aiv.zrd" or "aiv_easy.zrd" or "aiv_hard.zrd";

    public void AddCoordinates(IEnumerable<(ZbdDocument Archive, AssetRecord Asset)> resources, CancellationToken token = default, bool mw3 = false)
    {
        if (CanUndo || CanRedo || IsDirty) throw new InvalidOperationException("Coordinate sources must be loaded before editing.");
        var inputs = resources.Where(r => IsCoordinateResource(r.Asset.Name)).ToArray();
        var effective = Enum.GetValues<MissionDifficulty>().ToDictionary(d => d, d =>
        {
            string requested = MissionLayoutSelection.For(d).AivResource;
            var chosen = inputs.FirstOrDefault(r => r.Asset.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
            return chosen.Asset != null ? chosen : inputs.FirstOrDefault(r => r.Asset.Name.Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase));
        });
        foreach (var (doc, asset) in inputs)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (doc.Probe.Family != FormatFamily.Archive || doc.Diagnostics.Any(d => d.Severity == "Error") ||
                    doc.Assets.Any(a => a.Index != asset.Index && a.Offset < asset.Offset + asset.Length && asset.Offset < a.Offset + a.Length))
                    throw new InvalidDataException("An intact, non-overlapping archive member is required.");
                string archive = Path.GetFullPath(doc.Path).ToUpperInvariant();
                bool ai = MissionAiNetworks.IsCandidate(asset.Name);
                var tree = ZrdDecoder.Decode(doc.Slice(asset.Offset, asset.Length), token);
                var fields = tree["children"] as JsonArray ?? throw new InvalidDataException("Expected a record array.");
                if (fields.Count == 1 && fields[0]?["children"] is JsonArray inner) fields = inner;
                if (fields.Count % 2 != 0) throw new InvalidDataException("Incomplete mission record pair.");
                var graph = ai ? MissionAiNetworks.Decode("coordinates", archive, asset.Index, asset.Name, ZrdDecoder.Read(doc.Slice(asset.Offset, asset.Length), token), token) : null;
                for (int i = 0; i < fields.Count; i += 2)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        string name = fields[i].Text("value");
                        if (fields[i].Text("type") != "string" || fields[i + 1]?["children"] is not JsonArray { Count: >= 3 } row || !mw3 && !ai && row.Count != 3) continue;
                        long offset = Convert.ToInt64(fields[i + 1].Text("offset")[2..], 16);
                        if (ai && graph!.Nodes.All(n => n.SourceOffset != offset)) continue;
                        var (position, offsets) = ReadVector(row[1], doc, asset);
                        var source = new MissionPickupSource(archive, asset.Index, asset.Name.ToUpperInvariant(), ai ? checked((int)offset) : i / 2);
                        var rotation = ai ? Vector3.Zero : new Vector3(0, ReadNumber(row[2]), 0);
                        var difficulties = ai ? Array.Empty<MissionDifficulty>() : effective.Where(p => p.Value.Asset?.Index == asset.Index &&
                            p.Value.Archive.Path.Equals(doc.Path, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray();
                        var record = new MissionCoordinateRecord(source, ai ? "ai" : "tank", name, position, rotation,
                            ai ? "" : MissionSceneLoader.VehicleTemplateName(name), offset, difficulties) { MissionSpecific = mw3 };
                        if (entries.ContainsKey(source)) continue;
                        archives.TryAdd(archive, new(doc));
                        // The common coordinate store uses the existing archive-key identity.
                        entries.Add(source, new(new(source, name, position, rotation, difficulties), offsets, ai ? [] : [ReadScalarOffset(row[2], doc, asset)]));
                        positions.Add(source, position); savedPositions.Add(source, position); otherCoordinates.Add(source, record);
                        rotations.Add(source, rotation); savedRotations.Add(source, rotation);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException)
                    { diagnostics.Add($"{asset.Name} record {i / 2}: {ex.Message}"); }
                }
            }
            catch (InvalidDataException ex) { diagnostics.Add($"Mission coordinates {asset.Name}: {ex.Message}"); }
        }
    }

    private PickupPlacementScope CoordinateScope(MissionCoordinateRecord selected)
    {
        if (selected.Kind == "ai") return new([selected.Source], "This AI network node");
        if (selected.MissionSpecific) return new([selected.Source], "This authored mission actor only");
        if (selected.Difficulties.Count == 0) return new([selected.Source], "Read-only: shadowed tank resource");
        if (selected.TemplateSourceNode == null) return new([selected.Source], "Read-only: missing or ambiguous tank template");
        var candidates = otherCoordinates.Values.Where(r => r.Kind == "tank" && r.Template == selected.Template && r.TemplateSourceNode == selected.TemplateSourceNode &&
            r.Difficulties.Count > 0 && r.OriginalPosition == selected.OriginalPosition && r.Rotation == selected.Rotation).ToArray();
        var groups = candidates.GroupBy(r => (r.Source.ArchivePath, r.Source.AssetIndex)).ToArray();
        bool unique = groups.Single(g => g.Key == (selected.Source.ArchivePath, selected.Source.AssetIndex)).Count() == 1;
        var matches = unique ? groups.Where(g => g.Count() == 1).Select(g => g.Single()).ToArray() : [selected];
        var affected = matches.SelectMany(r => r.Difficulties).Distinct().Order().ToArray();
        var skipped = Enum.GetValues<MissionDifficulty>().Except(affected).ToArray();
        return new(matches.Select(r => r.Source).ToArray(), "Applies to: " + string.Join(", ", affected) +
            (skipped.Length == 0 ? "" : ". Unmatched or ambiguous (unchanged): " + string.Join(", ", skipped)));
    }

    public MissionCoordinateRecord? Coordinate(MissionPickupSource source) => otherCoordinates.GetValueOrDefault(source);

    public void BindCoordinateTemplates(GameScene scene)
    {
        foreach (var (key, record) in otherCoordinates.ToArray())
        {
            if (record.Kind != "tank") continue;
            var matches = scene.Nodes.Where(n => n.Class == "object3d" && n.Name == record.Template).ToArray();
            otherCoordinates[key] = record with { TemplateSourceNode = matches.Length == 1 ? matches[0].Index : null };
        }
    }

    public IEnumerable<ZbdDocument> WorkingArchives(CancellationToken token = default)
    {
        foreach (var (path, archive) in archives)
            yield return FormatRegistry.Default.OpenBytes(archive.Original.Path, EncodeArchive(path), archive.Original.Stamp, token);
    }

    public AiNetworkSnapshot ApplyAiPositions(AiNetworkSnapshot snapshot, IReadOnlyDictionary<MissionPickupSource, PlacementTransform>? preview = null)
    {
        bool changed = false;
        var networks = snapshot.Networks.Select(n => n with { Nodes = n.Nodes.Select(node =>
        {
            var key = new MissionPickupSource(Path.GetFullPath(n.Archive).ToUpperInvariant(), n.MemberIndex, n.Member.ToUpperInvariant(), checked((int)node.SourceOffset));
            if (!positions.TryGetValue(key, out var p)) return node;
            if (preview?.TryGetValue(key, out var pending) == true) p = pending.Position;
            if (p == node.Position) return node;
            changed = true; return node with { Position = p };
        }).ToArray() }).ToArray();
        if (!changed) return snapshot;
        if (preview != null) return new(snapshot.Id, networks); // A draft does not replace accepted source identities.
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(snapshot.Id));
        foreach (var node in networks.SelectMany(n => n.Nodes))
            foreach (float value in new[] { node.Position.X, node.Position.Y, node.Position.Z }) hash.AppendData(BitConverter.GetBytes(value));
        return new(Convert.ToHexString(hash.GetHashAndReset()), networks);
    }

    public IReadOnlyDictionary<int, Vector3> TankPreviewPositions(MissionSceneContext mission) => mission.Actors
        .GroupBy(a => a.Root).Where(g => g.Count() == 1).Select(g => g.Single())
        .Where(a => a.CoordinateSource != null && positions.ContainsKey(a.CoordinateSource))
        .ToDictionary(a => a.Root, a => positions[a.CoordinateSource!]);
}
