using System.Numerics;
using zStudio.Core.Formats;

namespace zStudio.Core;

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

    public void AddCoordinates(IEnumerable<(AssetDocument Archive, AssetRecord Asset)> resources, CancellationToken token = default, bool mw3 = false, IReadOnlySet<string>? presentResources = null)
    {
        // Cancellation can leave successfully admitted earlier rows; invalidate before the first mutation.
        InvalidateMembership();
        var inputs = resources.Where(r => IsCoordinateResource(r.Asset.Name)).ToArray();
        var effective = Enum.GetValues<MissionDifficulty>().ToDictionary(d => d, d =>
        {
            string requested = MissionLayoutSelection.For(d).AivResource;
            var chosen = inputs.FirstOrDefault(r => r.Asset.Name.Equals(requested, StringComparison.OrdinalIgnoreCase));
            return chosen.Asset != null || presentResources?.Contains(requested) == true ? chosen : inputs.FirstOrDefault(r => r.Asset.Name.Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase));
        });
        foreach (var (doc, asset) in inputs)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (doc is not Sources.SourceResourceDocument && doc.Probe.Family != FormatFamily.Archive || doc.Diagnostics.Any(d => d.Severity == "Error") ||
                    Overlaps(doc, asset, token))
                    throw new InvalidDataException("An intact, non-overlapping archive member is required.");
                string archive = Path.GetFullPath(doc.Path).ToUpperInvariant();
                string? memberIdentity = null;
                bool archiveRegistered = false;
                bool ai = MissionAiNetworks.IsCandidate(asset.Name);
                var tree = ZrdDecoder.ReadAsset(doc, asset, token);
                var fields = tree.Kind == ZrdKind.Array ? tree.Children : throw new InvalidDataException("Expected a record array.");
                if (fields.Count == 1 && fields[0].Kind == ZrdKind.Array) fields = fields[0].Children;
                if (fields.Count % 2 != 0) throw new InvalidDataException("Incomplete mission record pair.");
                var graph = ai ? MissionAiNetworks.Decode("coordinates", archive, asset.Index, asset.Name, tree, token) : null;
                var spatialOffsets = graph?.Nodes.Select(n => n.SourceOffset).ToHashSet();
                for (int i = 0; i < fields.Count; i += 2)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        string name = fields[i].Text;
                        if (fields[i].Kind != ZrdKind.String || fields[i + 1].Kind != ZrdKind.Array || fields[i + 1].Children is not { Count: >= 3 } row || !mw3 && !ai && row.Count != 3) continue;
                        long offset = fields[i + 1].SourceOffset;
                        if (ai && !spatialOffsets!.Contains(offset)) continue;
                        var (position, offsets) = ReadVector(row[1], doc, asset);
                        var source = new MissionPickupSource(archive, asset.Index, memberIdentity ??= asset.Name.ToUpperInvariant(), ai ? checked((int)offset) : i / 2);
                        var rotation = ai ? Vector3.Zero : new Vector3(0, ReadNumber(row[2]), 0);
                        var difficulties = ai ? Array.Empty<MissionDifficulty>() : effective.Where(p => p.Value.Asset?.Index == asset.Index &&
                            p.Value.Archive.Path.Equals(doc.Path, StringComparison.OrdinalIgnoreCase)).Select(p => p.Key).ToArray();
                        var record = new MissionCoordinateRecord(source, ai ? "ai" : "tank", name, position, rotation,
                            ai ? "" : MissionSceneLoader.VehicleTemplateName(name), offset, difficulties) { MissionSpecific = mw3 };
                        if (entries.ContainsKey(source)) continue;
                        if (!archiveRegistered)
                        { if (!archives.ContainsKey(archive)) archives.Add(archive, new(doc)); archiveRegistered = true; }
                        var entry = new Entry(new(source, name, position, rotation, difficulties), offsets, ai ? [] : [ReadScalarOffset(row[2], doc, asset)]);
                        sourceComparer.Register(source);
                        // The common coordinate store uses the existing archive-key identity.
                        entries.Add(source, entry);
                        positions.Add(source, position); otherCoordinates.Add(source, record);
                        rotations.Add(source, rotation);
                    }
                    catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException)
                    { diagnostics.Add($"{asset.Name} record {i / 2}: {ex.Message}"); }
                }
            }
            catch (InvalidDataException ex) { diagnostics.Add($"Mission coordinates {asset.Name}: {ex.Message}"); }
        }
    }

    public MissionCoordinateRecord? Coordinate(MissionPickupSource source) => otherCoordinates.GetValueOrDefault(source);

    /// <summary>
    /// Binds each tank record to the world node the game places for it (<c>CreateFromNamesAtPose</c> 0x421ab0 looks the record's
    /// own name up first, else copies the template its name derives; docs/agents/engine-evidence.md, aiv.zrd): an object3d node
    /// of the record's own name is placed itself and is its own template identity (each bftN.gw and bftmulti.gw loads the player tank
    /// as bft_00; bft1.gw's second load, bft, is a copy no retail record uses); otherwise the one template of the derived name. Either must be unique in the world, or the record is read only.
    /// </summary>
    public void BindCoordinateTemplates(GameScene scene)
    {
        InvalidateScopes();
        var templates = scene.Nodes.Where(n => n.Class == "object3d").ToLookup(n => n.Name, StringComparer.Ordinal);
        foreach (var (key, record) in otherCoordinates.ToArray())
        {
            if (record.Kind != "tank") continue;
            var placed = templates[record.Name].Take(2).ToArray();
            var matches = placed.Length > 0 ? placed : templates[record.Template].Take(2).ToArray();
            otherCoordinates[key] = record with { TemplateSourceNode = matches.Length == 1 ? matches[0].Index : null };
        }
    }

    public AiNetworkSnapshot ApplyAiPositions(AiNetworkSnapshot snapshot, IReadOnlyDictionary<MissionPickupSource, PlacementTransform>? preview = null)
    {
        if (!snapshot.Networks.Any(n => n.Nodes.Count > 0)) return snapshot;
        // Hash full identities once per shared prefix rather than once per node. This view is rebuilt from the
        // current accepted/draft dictionaries, so edits, undo and distinct equal-string instances retain semantics.
        var acceptedPositions = new MissionCoordinateProjection<Vector3>(positions);
        // The visible workspace supplies an ordinary dictionary. Preserve arbitrary public callers' custom
        // lookup/comparer behavior rather than replacing it with the projection's exact source-key semantics.
        var previewPositions = preview is Dictionary<MissionPickupSource, PlacementTransform> dictionary &&
            (ReferenceEquals(dictionary.Comparer, EqualityComparer<MissionPickupSource>.Default) || dictionary.Comparer is MissionSourceComparer)
            ? new MissionCoordinateProjection<PlacementTransform>(dictionary) : null;
        Dictionary<string, string> archiveNames = new(ReferenceEqualityComparer.Instance), memberNames = new(ReferenceEqualityComparer.Instance);
        bool changed = false;
        var networks = snapshot.Networks.Select(n =>
        {
            if (n.Nodes.Count == 0) return n; // Empty networks never evaluated their path/member before.
            if (!archiveNames.TryGetValue(n.Archive, out var archive)) archiveNames.Add(n.Archive, archive = Path.GetFullPath(n.Archive).ToUpperInvariant());
            if (!memberNames.TryGetValue(n.Member, out var member)) memberNames.Add(n.Member, member = n.Member.ToUpperInvariant());
            var accepted = acceptedPositions.Find(archive, n.MemberIndex, member);
            var draft = previewPositions?.Find(archive, n.MemberIndex, member);
            return n with { Nodes = n.Nodes.Select(node =>
            {
                int record = checked((int)node.SourceOffset);
                if (accepted == null || !accepted.TryGetValue(record, out var p)) return node;
                if (draft?.TryGetValue(record, out var pending) == true) p = pending.Position;
                else if (previewPositions == null && preview?.TryGetValue(new(archive, n.MemberIndex, member, record), out var custom) == true) p = custom.Position;
                if (p == node.Position) return node;
                changed = true; return node with { Position = p };
            }).ToArray() };
        }).ToArray();
        if (!changed) return snapshot;
        if (preview != null) return snapshot with { Networks = networks }; // A draft does not replace accepted source identities.
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        hash.AppendData(System.Text.Encoding.UTF8.GetBytes(snapshot.Id));
        foreach (var node in networks.SelectMany(n => n.Nodes))
            foreach (float value in new[] { node.Position.X, node.Position.Y, node.Position.Z }) hash.AppendData(BitConverter.GetBytes(value));
        return snapshot with { Id = Convert.ToHexString(hash.GetHashAndReset()), Networks = networks };
    }
}
