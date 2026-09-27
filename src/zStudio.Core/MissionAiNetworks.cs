using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record AiLink(int Slot, int TargetIndex, string? Target, string? Problem);
public sealed record AiNode(string Id, int Index, int RawValue, Vector3 Position, long SourceOffset, IReadOnlyList<AiLink> Links);
public sealed record AiNetwork(string Id, string Archive, int MemberIndex, string Member, string Name, string Type,
    float PathWidth, IReadOnlyList<AiNode> Nodes, IReadOnlyList<Diagnostic> Diagnostics);

/// <summary>Authored navigation data; no AI activation or pathfinding simulation. IDs are snapshot-scoped.</summary>
public sealed record AiNetworkSnapshot(string Id, IReadOnlyList<AiNetwork> Networks)
{
    private static string Bounded(string text) => text.Length <= 4096 ? text : text[..4096] + "… [truncated]";
    public static AiNetworkSnapshot Empty { get; } = new("empty", []);
    public IEnumerable<Diagnostic> Diagnostics => Networks.SelectMany(n => n.Diagnostics);
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
        ["path_width"] = network.PathWidth, ["node_index"] = node.Index, ["raw_node_integer"] = node.RawValue,
        ["position"] = JsonData.Vector(node.Position), ["links"] = new JsonArray(node.Links.Select(l => (JsonNode)new JsonObject
        { ["slot"] = l.Slot, ["target_index"] = l.TargetIndex, ["target_id"] = l.Target, ["problem"] = l.Problem }).ToArray()),
        ["interpretation"] = "Authored AI network. Runtime activation and pathfinding are not simulated."
    };
}

public static partial class MissionAiNetworks
{
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, AiNetworkSnapshot> Cache = [];
    [GeneratedRegex("^net_[0-9]{2}\\.zrd\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResourceName();
    [GeneratedRegex("^node_([0-9]{2})\\z", RegexOptions.CultureInvariant)]
    private static partial Regex NodeName();
    public static bool IsCandidate(string name) => ResourceName().IsMatch(name);

    public static AiNetworkSnapshot Read(IEnumerable<(ZbdDocument Archive, AssetRecord Asset)> resources, CancellationToken token = default)
    {
        List<(ZbdDocument Archive, AssetRecord Asset, string Id)> inputs = []; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
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
        lock (CacheGate) if (Cache.TryGetValue(snapshotId, out var cached)) return cached;
        List<AiNetwork> networks = [];
        foreach (var (archive, asset, id) in inputs)
        {
            token.ThrowIfCancellationRequested(); var bytes = archive.Slice(asset.Offset, asset.Length);
            try { networks.Add(Decode(id, archive.Path, asset.Index, asset.Name, asset.Content as ZrdNode ?? ZrdDecoder.Read(bytes, token), token)); }
            catch (InvalidDataException ex) { networks.Add(new(id, archive.Path, asset.Index, asset.Name, asset.Name, "", 10, [],
                [new("Warning", $"AI network {archive.Path} / {asset.Name} #{asset.Index}: {ex.Message}", asset.Index, asset.Offset)])); }
        }
        token.ThrowIfCancellationRequested();
        var result = new AiNetworkSnapshot(snapshotId, networks.AsReadOnly());
        lock (CacheGate) { if (Cache.Count >= 8) Cache.Clear(); Cache[snapshotId] = result; }
        return result;
    }

    /// <summary>Layout verified against retail 0x403040 and 0x403550. Unknown node integer semantics remain raw.</summary>
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
            if (fields[i].Kind != ZrdKind.String || fields[i + 1].Kind != ZrdKind.Array) throw new InvalidDataException("Expected named network fields with array values.");
            pairs.Add((fields[i].Text, fields[i + 1]));
        }
        List<Diagnostic> notes = [];
        void Note(string message, long? offset = null) => notes.Add(new("Warning", $"AI network {archive} / {member} #{memberIndex}: {message[..Math.Min(1024, message.Length)]}", memberIndex, offset));
        ZrdNode? Field(string name)
        {
            var matches = pairs.Where(p => p.Key == name).ToArray();
            if (matches.Length > 1) throw new InvalidDataException($"Ambiguous repeated {name} field.");
            return matches.FirstOrDefault().Value;
        }
        var version = Field("version");
        if (version != null && (version.Children.Count != 1 || Integer(version.Children[0]) != 105)) throw new InvalidDataException("Unsupported AI network version (expected 105 or absent).");
        string TextField(string name, string fallback)
        {
            var field = Field(name); if (field == null) return fallback;
            if (field.Children.Count == 1 && field.Children[0].Kind == ZrdKind.String) return field.Children[0].Text;
            Note($"Invalid {name}; expected one string.", field.SourceOffset); return fallback;
        }
        float width = 10;
        if (Field("path_width") is { } pathWidth)
        {
            if (pathWidth.Children.Count == 1 && pathWidth.Children[0].Kind == ZrdKind.Float && float.IsFinite(Float(pathWidth.Children[0]))) width = Float(pathWidth.Children[0]);
            else Note("Invalid path_width; displaying the default width 10.", pathWidth.SourceOffset);
        }
        List<AiNode> nodes = [];
        // A malformed duplicate still makes a numeric target ambiguous. Never
        // silently bind to the other record after rejecting the malformed one.
        var declared = pairs.Select(p => NodeName().Match(p.Key)).Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).Where(n => n <= 98)
            .GroupBy(n => n).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (key, value) in pairs)
        {
            token.ThrowIfCancellationRequested(); var match = NodeName().Match(key);
            if (!match.Success) { if (key.StartsWith("node_", StringComparison.Ordinal)) Note($"Unsupported node key {key}.", value.SourceOffset); continue; }
            int index = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (index > 98) { Note($"{key} is outside the retail node range 00–98.", value.SourceOffset); continue; }
            try
            {
                var c = value.Children;
                if (c.Count != 3 || c[1].Kind != ZrdKind.Array || c[1].Children.Count != 3 || c[2].Kind != ZrdKind.Array || c[2].Children.Count != 3)
                    throw new InvalidDataException("Expected raw integer, XYZ and three link slots.");
                var p = c[1].Children; Vector3 position = new(Float(p[0]), Float(p[1]), Float(p[2]));
                if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) || !float.IsFinite(position.Z)) throw new InvalidDataException("Non-finite node coordinates.");
                if (Math.Abs(position.X) > 1e12 || Math.Abs(position.Y) > 1e12 || Math.Abs(position.Z) > 1e12) throw new InvalidDataException("Coordinates outside the supported ±1e12 preview range.");
                nodes.Add(new(id + ":" + nodes.Count, index, Integer(c[0]), position, value.SourceOffset,
                    c[2].Children.Select((n, slot) => new AiLink(slot, Integer(n), null, null)).ToArray()));
            }
            catch (InvalidDataException ex) { Note($"{key}: {ex.Message}", value.SourceOffset); }
        }
        var byIndex = nodes.GroupBy(n => n.Index).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var duplicate in declared.Where(p => p.Value > 1)) Note($"Repeated node_{duplicate.Key:00}; links to it are ambiguous.");
        var resolved = nodes.Select(n => n with { Links = n.Links.Select(link =>
        {
            if (link.TargetIndex < 0) return link;
            var targets = byIndex.GetValueOrDefault(link.TargetIndex);
            if (targets?.Length == 1 && declared[link.TargetIndex] == 1) return link with { Target = targets[0].Id };
            string problem = declared.GetValueOrDefault(link.TargetIndex) > 1 ? "Ambiguous target" : "Missing target";
            Note($"node_{n.Index:00} slot {link.Slot}: {problem} {link.TargetIndex}.", n.SourceOffset);
            return link with { Problem = problem };
        }).ToArray() }).ToArray();
        return new(id, archive, memberIndex, member, TextField("name", member), TextField("type", ""), width, Array.AsReadOnly(resolved), notes.AsReadOnly());
    }
    private static int Integer(ZrdNode node) => node.Kind == ZrdKind.Int ? unchecked((int)node.Bits) : throw new InvalidDataException("Expected an integer.");
    private static float Float(ZrdNode node) => node.Kind == ZrdKind.Float ? BitConverter.UInt32BitsToSingle(node.Bits) : throw new InvalidDataException("Expected a float.");
}
