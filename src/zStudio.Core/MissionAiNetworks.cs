using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using zStudio.Core.Formats;

namespace zStudio.Core;

public sealed record AiLink(int Slot, int TargetIndex, string? Target, string? Problem);
public sealed record AiNode(string Id, int Index, int RawValue, Vector3 Position, long SourceOffset, IReadOnlyList<AiLink> Links)
{
    public ZrdNode? Source { get; init; }
    public IReadOnlyList<AiValveRecord> Valves { get; init; } = [];
    public int ValveCount { get; init; }
    public const int MaximumPreviewLinks = 32;
    public int? AuthoredLinkCount { get; init; }
    /// <summary>The node's tag as stored: <c>node_05</c>, <c>node_5</c> and <c>node_005</c> all name node 5.</summary>
    public string Key { get; init; } = "";
    /// <summary>The node's name as labels show it: its stored tag (at most 32 characters), or <c>node_NN</c> without one.</summary>
    public string Label => Key.Length > 0 ? JsonData.ShownText(Key, 32) : $"node_{Index:00}";
    public int LinkCount => AuthoredLinkCount ?? Links.Count;
    public bool LinksTruncated => LinkCount > Math.Min(Links.Count, MaximumPreviewLinks);
    public IEnumerable<AiLink> PreviewLinks => Links.Take(MaximumPreviewLinks);
}
public sealed record AiConstraint(int Index, int FromNode, int ToNode, string Kind, long SourceOffset, JsonObject Parameters)
{ public int AttributeIndex { get; init; } public Guid SourceId { get; init; } public AiValveRecord? Valve { get; init; } }
public sealed record AiNetwork(string Id, string Archive, int MemberIndex, string Member, string Name, string Type,
    float PathWidth, IReadOnlyList<AiNode> Nodes, IReadOnlyList<Diagnostic> Diagnostics)
{
    public AiAttackStrategy AttackStrategy { get; init; } = AiAttackStrategy.Missing;
    public IReadOnlyList<AiConstraint> Constraints { get; init; } = [];
    public int ConstraintCount { get; init; }
    public ZrdNode? Source { get; init; }
    public bool IsMw3 { get; init; }
    public IReadOnlySet<int> AmbiguousIndices { get; init; } = new HashSet<int>();
    public IReadOnlySet<int> ResolvedIndices { get; init; } = new HashSet<int>();
    public IReadOnlyList<ZrdNode> ValveEdgeSources { get; init; } = [];
    public IReadOnlyDictionary<int, IReadOnlyList<ZrdNode>> ValveEdgesByNode { get; init; } = new Dictionary<int, IReadOnlyList<ZrdNode>>();
    /// <summary>The links before each node's first empty slot, all slots counted (not only the previewed 32): the links the game follows when the network has no repeated node and no missing target.</summary>
    public long FollowedLinks => Nodes.Sum(n => n.Source is { Children: [_, _, { Kind: ZrdKind.Array } slots, ..] }
        ? (long)slots.Children.TakeWhile(s => s.Kind == ZrdKind.Int && unchecked((int)s.Bits) >= 0).Count() : 0);
}

/// <summary>Authored navigation data; no AI activation or pathfinding simulation. IDs are snapshot-scoped.</summary>
public sealed record AiNetworkSnapshot(string Id, IReadOnlyList<AiNetwork> Networks)
{
    public IReadOnlyList<AiValveSource> ValveSources { get; init; } = [];
    private static string Bounded(string text) => text.Length <= 256 ? text : text[..256] + "… [truncated]";
    public static AiNetworkSnapshot Empty { get; } = new("empty", []);
    /// <summary>Diagnostics dropped by the snapshot-wide budget; each network also bounds its own notes.</summary>
    public long OmittedDiagnostics { get; init; }
    public IEnumerable<Diagnostic> Diagnostics => OmittedDiagnostics == 0 ? Networks.SelectMany(n => n.Diagnostics) :
        Networks.SelectMany(n => n.Diagnostics).Append(new("Warning", $"{OmittedDiagnostics:N0} additional AI network diagnostics omitted. Inspect the source network resources for the complete records."));
    public (AiNetwork Network, AiNode Node)? Find(string id)
    {
        foreach (var network in Networks) foreach (var node in network.Nodes) if (node.Id == id) return (network, node);
        return null;
    }
    public JsonObject Describe(AiNetwork network, AiNode node) => new()
    {
        ["snapshot"] = Id, ["network_id"] = network.Id, ["node_id"] = node.Id,
        ["source_archive"] = network.Archive, ["source_member_index"] = network.MemberIndex, ["source_member"] = network.Member,
        ["source_offset"] = node.SourceOffset, ["network"] = Bounded(network.Name), ["stored_type"] = Bounded(network.Type),
        ["stored_name_characters"] = network.Name.Length, ["stored_type_characters"] = network.Type.Length,
        ["stored_name_truncated"] = network.Name.Length > 256, ["stored_type_truncated"] = network.Type.Length > 256,
        ["attack_strategy"] = network.AttackStrategy.Describe(),
        ["network_constraint_count"] = network.ConstraintCount, ["constraints_truncated"] = network.ConstraintCount > network.Constraints.Count,
        ["path_width"] = network.PathWidth, ["node_index"] = node.Index, ["node_key"] = node.Label, ["raw_node_integer"] = node.RawValue,
        ["link_count"] = node.LinkCount, ["links_truncated"] = node.LinksTruncated,
        ["position"] = JsonData.Vector(node.Position), ["links"] = new JsonArray(node.PreviewLinks.Select(l => (JsonNode)new JsonObject
        { ["slot"] = l.Slot, ["target_index"] = l.TargetIndex, ["target_id"] = l.Target, ["problem"] = l.Problem }).ToArray()),
        ["interpretation"] = "Authored AI network. Runtime activation and pathfinding are not simulated."
        , ["valves"] = new JsonArray(MissionAiValves.ForNode(network, node).Take(2).Select(r => (JsonNode)MissionAiValves.Summary(r)).ToArray()),
        ["valves_truncated"] = node.ValveCount > node.Valves.Count || MissionAiValves.ForNode(network, node).Skip(2).Any()
    };
}

public static partial class MissionAiNetworks
{
    private static readonly object CacheGate = new();
    /// <summary>Snapshots retain their decoded source trees, so they are cached only while their first source
    /// archive is alive: closed documents and replaced roots must not keep them reachable.</summary>
    private static readonly ConditionalWeakTable<AssetDocument, Dictionary<string, AiNetworkSnapshot>> Cache = new();
    [GeneratedRegex("^net_([0-9]{2,})\\.zrd\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourceName();
    [GeneratedRegex("^net_[0-9]{2}\\.zrd\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Mw3ResourceName();
    [GeneratedRegex("^node_([0-9]+)\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NodeName();
    /// <summary>
    /// Whether an archive member is an AI network. A RECOIL network is <c>net_</c>, its number (two or more digits, leading
    /// zeros allowed, 1 to 2,147,483,647), then <c>.zrd</c>, in any case of the letters, as reRECOIL v2 reads it; Recoil 1.x
    /// reads only <c>net_01</c> to <c>net_99</c> (docs/agents/engine-evidence.md#net_nnzrd). A MechWarrior 3 network
    /// (<paramref name="mw3"/>) keeps its two-digit name.
    /// </summary>
    public static bool IsCandidate(string name, bool mw3 = false) => mw3 ? Mw3ResourceName().IsMatch(name) : TryNetworkNumber(name, out _);
    /// <summary>The number of a RECOIL network's member name (the network's ID, which vehicles name), as <see cref="IsCandidate"/> reads it.</summary>
    public static bool TryNetworkNumber(string name, out int number)
    {
        number = 0; var match = ResourceName().Match(name);
        return match.Success && int.TryParse(match.Groups[1].ValueSpan, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out number) && number >= 1;
    }
    [GeneratedRegex("^net_[0-9]+\\.zrd\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedName();
    /// <summary>A RECOIL member named like a network that the game reads no network from: <c>net_5.zrd</c>, <c>net_00.zrd</c>, a number past 2,147,483,647.</summary>
    public static bool LooksLikeNetwork(string name) => NumberedName().IsMatch(name) && !IsCandidate(name);
    /// <summary>Whether Recoil 1.x and reRECOIL v1 read a network of this member name: <c>net_01.zrd</c> to <c>net_99.zrd</c>, with two digits.</summary>
    internal static bool RecoilReadsName(string name) => TryNetworkNumber(name, out _) && name.Length == "net_00.zrd".Length;
    /// <summary>The number of a node tag: <c>node_</c>, then one or more digits (leading zeros allowed) up to 2,147,483,647; the case counts.</summary>
    internal static bool TryNodeIndex(string name, out int index)
    {
        index = -1; var match = NodeName().Match(name);
        return match.Success && int.TryParse(match.Groups[1].ValueSpan, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out index);
    }
    /// <summary>Whether Recoil 1.x and reRECOIL v1 read a node of this tag: <c>node_00</c> to <c>node_98</c>, with two digits.</summary>
    internal static bool RecoilReadsNode(string key) => TryNodeIndex(key, out int index) && key.Length == "node_00".Length && index <= 98;
    internal static bool IsConstraint(ZrdNode value, CancellationToken token)
    {
        var c = value.Children;
        if (value.Kind != ZrdKind.Array || c.Count < 3 || c[0].Kind != ZrdKind.Array || c[0].Children.Count != 2 || c[0].Children.Any(n => n.Kind != ZrdKind.Int)) return false;
        return AttributePairs(c, 1, token);
    }
    /// <summary>Every field from <paramref name="first"/> is a string name followed by an array value, with none dangling.
    /// Retail v106 networks have 3,741 attributed node rows and no exceptions.</summary>
    private static bool AttributePairs(IReadOnlyList<ZrdNode> c, int first, CancellationToken token)
    {
        if ((c.Count - first) % 2 != 0) return false;
        for (int i = first; i < c.Count; i += 2) { token.ThrowIfCancellationRequested(); if (c[i].Kind != ZrdKind.String || c[i + 1].Kind != ZrdKind.Array) return false; }
        return true;
    }
    /// <summary>
    /// Whether a node tag's value is a node: a raw integer, an XYZ of three finite floats and an array of integer link slots
    /// of any number (MechWarrior 3 nodes add name/array attribute pairs).
    /// </summary>
    internal static bool IsSpatial(ZrdNode value, bool mw3, CancellationToken token)
    {
        var c = value.Children;
        if (value.Kind != ZrdKind.Array || (mw3 ? c.Count < 3 : c.Count != 3) || c[0].Kind != ZrdKind.Int || c[1].Kind != ZrdKind.Array || c[1].Children.Count != 3 || c[2].Kind != ZrdKind.Array) return false;
        foreach (var n in c[1].Children) if (n.Kind != ZrdKind.Float || !float.IsFinite(Float(n)) || Math.Abs(Float(n)) > 1e12) return false;
        foreach (var n in c[2].Children) { token.ThrowIfCancellationRequested(); if (n.Kind != ZrdKind.Int) return false; }
        // Malformed attributes keep the row inspectable but never make it a node or a semantic edit target.
        return !mw3 || AttributePairs(c, 3, token);
    }

    public static AiNetworkSnapshot Read(IEnumerable<(AssetDocument Archive, AssetRecord Asset)> resources, CancellationToken token = default)
    {
        List<(AssetDocument Archive, AssetRecord Asset, string Id)> inputs = []; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (archive, asset) in resources)
        {
            token.ThrowIfCancellationRequested();
            string source = Path.GetFullPath(archive.Path).ToUpperInvariant() + "|" + asset.Index + "|" + asset.Name;
            string id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
            var bytes = archive.Slice(asset.Offset, asset.Length);
            hash.AppendData(Encoding.UTF8.GetBytes(id)); hash.AppendData(SHA256.HashData(bytes.Span));
            hash.AppendData(Encoding.UTF8.GetBytes(asset.Offset.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"));
            inputs.Add((archive, asset, id));
        }
        token.ThrowIfCancellationRequested();
        string snapshotId = Convert.ToHexString(hash.GetHashAndReset());
        var owner = inputs.Count > 0 ? inputs[0].Archive : null;
        if (owner != null) lock (CacheGate) if (Cache.TryGetValue(owner, out var owned) && owned.TryGetValue(snapshotId, out var cached)) return cached;
        List<AiNetwork> networks = []; int retained = 0; long omitted = 0;
        foreach (var (archive, asset, id) in inputs)
        {
            token.ThrowIfCancellationRequested(); AiNetwork network;
            try { network = Decode(id, archive.Path, asset.Index, asset.Name, ZrdDecoder.ReadAsset(archive, asset, token), token); }
            catch (InvalidDataException ex) { network = new(id, archive.Path, asset.Index, asset.Name, asset.Name, "", 10, [],
                [new("Warning", $"AI network {archive.Path} / {asset.Name} #{asset.Index}: {ex.Message}", asset.Index, asset.Offset)]); }
            // Many (possibly identically named) members must not compose per-network caps into an unbounded snapshot.
            int keep = Math.Min(network.Diagnostics.Count, PreviewNotes.MaximumItems - retained);
            if (keep < network.Diagnostics.Count) { omitted += network.Diagnostics.Count - keep; network = network with { Diagnostics = network.Diagnostics.Take(keep).ToArray() }; }
            retained += keep; networks.Add(network);
        }
        token.ThrowIfCancellationRequested();
        var result = new AiNetworkSnapshot(snapshotId, networks.AsReadOnly()) { OmittedDiagnostics = omitted };
        if (owner != null) lock (CacheGate) { var owned = Cache.GetOrCreateValue(owner); if (owned.Count >= 8) owned.Clear(); owned[snapshotId] = result; }
        return result;
    }

    /// <summary>
    /// Layout verified against retail 0x403040 and 0x403550. Unknown node integer semantics remain raw. A RECOIL network is
    /// version 105 (also without a version tag) or 200, which read alike: node tags <c>node_&lt;n&gt;</c> of any number and link
    /// slots of any number; version 106 is MechWarrior 3's. Links the game refuses or cannot follow are noted: a missing
    /// target and, in a RECOIL network, a link after an empty slot (docs/agents/engine-evidence.md#net_nnzrd).
    /// </summary>
    public static AiNetwork Decode(string id, string archive, int memberIndex, string member, ZrdNode root, CancellationToken token = default)
    {
        var fields = root.Children;
        if (root.Kind != ZrdKind.Array) throw new InvalidDataException("Expected a network field array.");
        if (fields.Count == 1 && fields[0].Kind == ZrdKind.Array) fields = fields[0].Children;
        if (fields.Count % 2 != 0) throw new InvalidDataException("Incomplete network name/value pair.");
        List<(string Key, ZrdNode Value)> pairs = [];
        for (int i = 0; i < fields.Count; i += 2)
        {
            token.ThrowIfCancellationRequested();
            if (fields[i].Kind != ZrdKind.String) throw new InvalidDataException("Expected named network fields.");
            pairs.Add((fields[i].Text, fields[i + 1]));
        }
        List<Diagnostic> notes = []; int noteCount = 0;
        void Note(string message, long? offset = null) { noteCount++; if (notes.Count < 256) notes.Add(new("Warning", $"AI network {archive} / {member} #{memberIndex}: {message[..Math.Min(1024, message.Length)]}", memberIndex, offset)); }
        ZrdNode? Field(string name)
        {
            var matches = pairs.Where(p => p.Key == name).ToArray();
            if (matches.Length > 1) throw new InvalidDataException($"Ambiguous repeated {name} field.");
            return matches.FirstOrDefault().Value;
        }
        var version = Field("version");
        if (version != null && (version.Children.Count != 1 || Integer(version.Children[0]) is not (105 or 106 or 200))) throw new InvalidDataException("Unsupported AI network version (expected 105, 200, 106 or absent).");
        bool mw3 = version?.Children.Count == 1 && Integer(version.Children[0]) == 106;
        string TextField(string name, string fallback)
        {
            var field = Field(name); if (field == null) return fallback;
            if (field.Children.Count == 1 && field.Children[0].Kind == ZrdKind.String) return field.Children[0].Text;
            Note($"Invalid {name}; expected one string.", field.SourceOffset); return fallback;
        }
        AiAttackStrategy ReadAttackStrategy()
        {
            var matches = pairs.Where(p => p.Key == "attack_strategy").ToArray();
            if (matches.Length == 0) return AiAttackStrategy.Missing;
            var value = matches[0].Value;
            if (matches.Length != 1 || value.Kind != ZrdKind.Array || value.Children.Count != 1 || value.Children[0].Kind != ZrdKind.String)
            {
                Note(matches.Length != 1 ? "Ambiguous repeated attack_strategy field." : "Invalid attack_strategy; expected an array containing one string.", value.SourceOffset);
                return AiAttackStrategy.Invalid;
            }
            return AiAttackStrategy.Stored(value.Children[0].Text);
        }
        var attackStrategy = ReadAttackStrategy();
        float width = 10;
        if (Field("path_width") is { } pathWidth)
        {
            if (pathWidth.Children.Count == 1 && pathWidth.Children[0].Kind == ZrdKind.Float && float.IsFinite(Float(pathWidth.Children[0]))) width = Float(pathWidth.Children[0]);
            else Note("Invalid path_width; displaying the default width 10.", pathWidth.SourceOffset);
        }
        List<AiNode> nodes = []; List<AiConstraint> constraints = []; List<ZrdNode> valveEdges = []; int constraintCount = 0;
        // A malformed duplicate still makes a numeric target ambiguous. Never
        // silently bind to the other record after rejecting the malformed one.
        Dictionary<int, int> declared = [];
        Dictionary<int, string> tagKeys = [];
        List<(string Key, long Offset, int Slot, int Target)> unpreviewed = [];
        foreach (var (key, value) in pairs)
        {
            token.ThrowIfCancellationRequested(); var match = NodeName().Match(key);
            if (!match.Success) { if (key.StartsWith("node_", StringComparison.Ordinal)) Note($"Unsupported node key {key[..Math.Min(128, key.Length)]}.", value.SourceOffset); continue; }
            if (!int.TryParse(match.Groups[1].ValueSpan, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int index))
            { Note($"Unsupported node index {MissionAiValves.Short(key, 128)}: node numbers go up to 2,147,483,647.", value.SourceOffset); continue; }
            bool constraint = false;
            try
            {
                var c = value.Children;
                if (mw3 && IsConstraint(value, token))
                {
                    int from = Integer(c[0].Children[0]), to = Integer(c[0].Children[1]);
                    constraint = true; // Only fully validated v106 constraints are a separate identity kind.
                    constraintCount += (c.Count - 1) / 2;
                    for (int attribute = 1; attribute < c.Count; attribute += 2)
                        if (c[attribute].Text == "valve_assign") { valveEdges.Add(value); break; }
                    for (int attribute = 1; attribute < c.Count && constraints.Count < 1024; attribute += 2)
                        constraints.Add(new(index, from, to, c[attribute].Text, value.SourceOffset, c[attribute + 1].ToPreviewJson(token, 32, 512, 6, 256)) { AttributeIndex = (attribute - 1) / 2, SourceId = c[attribute].Id,
                            Valve = c[attribute].Text == "valve_assign" ? new(c[attribute].Id, value.Id, attribute, c[attribute], c[attribute + 1], "edge", From: from, To: to) : null });
                    continue;
                }
                if (value.Kind != ZrdKind.Array || (mw3 ? c.Count < 3 : c.Count != 3) || c[1].Kind != ZrdKind.Array || c[1].Children.Count != 3 || c[2].Kind != ZrdKind.Array)
                    throw new InvalidDataException("Expected raw integer, XYZ and link slots.");
                if (mw3 && !AttributePairs(c, 3, token)) throw new InvalidDataException("Expected name/array attribute pairs after the link slots; the row remains inspectable in the ZRD resource.");
                var p = c[1].Children; Vector3 position = new(Float(p[0]), Float(p[1]), Float(p[2]));
                if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) throw new InvalidDataException("Non-finite node coordinates.");
                if (Math.Abs(position.X) > 1e12 || Math.Abs(position.Y) > 1e12 || Math.Abs(position.Z) > 1e12) throw new InvalidDataException("Coordinates outside the supported ±1e12 preview range.");
                int linkCount = c[2].Children.Count, firstEmpty = -1, lateLink = -1;
                List<AiLink> links = [];
                for (int slot = 0; slot < linkCount; slot++)
                {
                    if ((slot & 4095) == 0) token.ThrowIfCancellationRequested();
                    int target = Integer(c[2].Children[slot]); // Validate omitted slots too.
                    if (slot < AiNode.MaximumPreviewLinks) links.Add(new(slot, target, null, null));
                    if (target < 0) { if (firstEmpty < 0) firstEmpty = slot; }
                    else if (firstEmpty >= 0 && lateLink < 0) lateLink = slot;
                    // Links past the preview are checked too (bounded): the game refuses or cannot follow a missing target.
                    if (target >= 0 && slot >= AiNode.MaximumPreviewLinks && unpreviewed.Count < 256) unpreviewed.Add((key, value.SourceOffset, slot, target));
                }
                if (linkCount > AiNode.MaximumPreviewLinks) Note($"{key}: showing the first {AiNode.MaximumPreviewLinks} of {linkCount} authored link slots. Inspect/export the ZRD resource for the complete list.", value.SourceOffset);
                // reRECOIL v2 takes a node's links as the slots before its first empty one and refuses a link after it (MechWarrior 3's networks are not RECOIL's).
                if (!mw3 && lateLink >= 0)
                    Note($"{key}: link slot {lateLink} follows the empty slot {firstEmpty}; reRECOIL v2 does not load a network with a link after an empty slot.", value.SourceOffset);
                List<AiValveRecord> valves = []; int valveCount = 0;
                if (mw3) for (int a = 3; a + 1 < c.Count; a += 2)
                {
                    token.ThrowIfCancellationRequested();
                    if (c[a].Kind != ZrdKind.String || c[a].Text is not ("valve" or "valveunion")) continue;
                    valveCount++; if (valves.Count < 16) valves.Add(new(c[a].Id, value.Id, a, c[a], c[a + 1], "node", index));
                }
                nodes.Add(new(id + ":" + nodes.Count, index, Integer(c[0]), position, value.SourceOffset, links.AsReadOnly()) { AuthoredLinkCount = linkCount, Source = value, Valves = valves.AsReadOnly(), ValveCount = valveCount, Key = key });
            }
            catch (InvalidDataException ex) { Note($"{key}: {ex.Message}", value.SourceOffset); }
            finally
            {
                if (!constraint && (declared[index] = declared.GetValueOrDefault(index) + 1) <= 2) tagKeys[index] = tagKeys.TryGetValue(index, out var first) ? first + " and " + MissionAiValves.Short(key, 32) : MissionAiValves.Short(key, 32);
            }
        }
        var byIndex = nodes.GroupBy(n => n.Index).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var duplicate in declared.Where(p => p.Value > 1)) Note($"Repeated node {duplicate.Key} ({tagKeys[duplicate.Key]}{(duplicate.Value > 2 ? " and more" : "")}); links to it are ambiguous.");
        var resolved = nodes.Select(n => n with { Links = n.Links.Select(link =>
        {
            if (link.TargetIndex < 0) return link;
            var targets = byIndex.GetValueOrDefault(link.TargetIndex);
            if (targets?.Length == 1 && declared[link.TargetIndex] == 1) return link with { Target = targets[0].Id };
            string problem = declared.GetValueOrDefault(link.TargetIndex) > 1 ? "Ambiguous target" : "Missing target";
            Note($"{n.Label} slot {link.Slot}: {problem} {link.TargetIndex}.", n.SourceOffset);
            return link with { Problem = problem };
        }).ToArray() }).ToArray();
        foreach (var (key, offset, slot, target) in unpreviewed)
            if (!(byIndex.GetValueOrDefault(target)?.Length == 1 && declared[target] == 1))
                Note($"{MissionAiValves.Short(key, 32)} slot {slot}: {(declared.GetValueOrDefault(target) > 1 ? "Ambiguous target" : "Missing target")} {target}.", offset);
        if (constraintCount > constraints.Count) Note($"Showing {constraints.Count} of {constraintCount} constraint attributes; the complete source remains available in ZRD inspection and export.");
        if (noteCount > notes.Count) notes.Add(new("Warning", $"{noteCount - notes.Count} additional AI diagnostics omitted.", memberIndex));
        var unique = byIndex.Where(p => p.Value.Length == 1 && declared[p.Key] == 1).Select(p => p.Key).ToHashSet();
        // Retain source rows, not one allocated record/JSON tree per attribute.
        // Semantic associations must not inherit the constraint display budget.
        Dictionary<int, List<ZrdNode>> edgeValves = []; List<ZrdNode> resolvedEdges = [];
        foreach (var edge in valveEdges)
        {
            token.ThrowIfCancellationRequested(); int from = Integer(edge.Children[0].Children[0]), to = Integer(edge.Children[0].Children[1]);
            if (!unique.Contains(from) || !unique.Contains(to)) continue;
            resolvedEdges.Add(edge);
            if (!edgeValves.TryGetValue(from, out var first)) edgeValves.Add(from, first = []); first.Add(edge);
            if (to != from) { if (!edgeValves.TryGetValue(to, out var second)) edgeValves.Add(to, second = []); second.Add(edge); }
        }
        return new(id, archive, memberIndex, member, TextField("name", member), TextField("type", ""), width, Array.AsReadOnly(resolved), notes.AsReadOnly()) { AttackStrategy = attackStrategy, Constraints = constraints.AsReadOnly(), ConstraintCount = constraintCount, Source = root, IsMw3 = mw3, AmbiguousIndices = declared.Where(p => p.Value > 1).Select(p => p.Key).ToHashSet(), ResolvedIndices = unique, ValveEdgeSources = resolvedEdges.AsReadOnly(), ValveEdgesByNode = edgeValves.ToDictionary(p => p.Key, p => (IReadOnlyList<ZrdNode>)p.Value.AsReadOnly()) };
    }
    private static int Integer(ZrdNode node) => node.Kind == ZrdKind.Int ? unchecked((int)node.Bits) : throw new InvalidDataException("Expected an integer.");
    private static float Float(ZrdNode node) => node.Kind == ZrdKind.Float ? BitConverter.UInt32BitsToSingle(node.Bits) : throw new InvalidDataException("Expected a float.");
}
