using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using zStudio.Core.Formats;

namespace zStudio.Core;

public sealed record AiValveRecord(Guid Id, Guid Parent, int ChildIndex, ZrdNode NameNode, ZrdNode Value,
    string Kind, int? NodeIndex = null, int? From = null, int? To = null)
{
    public string Name => NameNode.Text;
    public long SourceOffset => NameNode.SourceOffset;
}
public sealed record AiValveReference(Guid Record, Guid Operand, string Name, string Role);
public sealed record AiValveSource(string Archive, int MemberIndex, string Member, ZrdNode Root, ReadOnlyMemory<byte> Data);

/// <summary>Semantic views over the shared ZRD tree. Occurrences, not names, identify records.</summary>
public static class MissionAiValves
{
    public static AiNetworkSnapshot Attach(AiNetworkSnapshot graph, IEnumerable<AssetDocument> archives, CancellationToken token)
    {
        List<AiValveSource> sources = []; using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Dictionary<string, Dictionary<int, ZrdNode?>> networkRoots = new(StringComparer.OrdinalIgnoreCase);
        foreach (var network in graph.Networks)
        {
            token.ThrowIfCancellationRequested();
            if (!networkRoots.TryGetValue(network.Archive, out var members)) networkRoots.Add(network.Archive, members = []);
            members.TryAdd(network.MemberIndex, network.Source); // Preserve prior first-occurrence behavior for an identical source identity.
        }
        hash.AppendData(Encoding.UTF8.GetBytes(graph.Id));
        foreach (var archive in archives) foreach (var asset in archive.Assets)
        {
            token.ThrowIfCancellationRequested();
            if (!IsResource(asset.Name) || asset.Content is not ZrdNode root) continue;
            var bytes = archive.Slice(asset.Offset, asset.Length);
            var networkRoot = networkRoots.GetValueOrDefault(archive.Path)?.GetValueOrDefault(asset.Index);
            sources.Add(new(archive.Path, asset.Index, asset.Name, networkRoot ?? root, bytes));
            hash.AppendData(Encoding.UTF8.GetBytes(archive.Path + "|" + asset.Index + "|" + asset.Name));
            hash.AppendData(SHA256.HashData(bytes.Span));
        }
        return graph with { Id = Convert.ToHexString(hash.GetHashAndReset()), ValveSources = sources.AsReadOnly() };
    }
    public static readonly string[] Actions = ["attack_strategy", "delayupdate", "destroy", "shutdown", "sound", "sound_once", "teleport"];
    public static readonly string[] Conditions = ["all_zero", "all_nonzero", "any_nonzero"];
    public static bool IsResource(string name) => name.Equals("valves.zrd", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("objectives.zrd", StringComparison.OrdinalIgnoreCase) || MissionAiNetworks.IsCandidate(name);
    /// <summary>Semantic valve Properties require authored valve structure; RECOIL shares the objectives.zrd name.</summary>
    public static bool HasSemanticRecords(string member, ZrdNode root, CancellationToken token = default) =>
        MissionAiNetworks.IsCandidate(member) ? IsNetwork(root) : IsResource(member) && Records(member, root, token).Any();
    public static string Short(string text, int maximum = 256) => text.Length <= maximum ? text : text[..maximum] + "…";
    public static bool MatchesSearch(AiValveRecord record, string query) => record.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (record.NodeIndex?.ToString(CultureInfo.InvariantCulture)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (record.From?.ToString(CultureInfo.InvariantCulture)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (record.To?.ToString(CultureInfo.InvariantCulture)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        // Bindings and objective uses are named by the valves they reference, not by their attribute name.
        References(record).Any(r => r.Name.Contains(query, StringComparison.OrdinalIgnoreCase));
    public static JsonObject Summary(AiValveRecord record)
    {
        var refs = References(record).Take(3).ToArray();
        return new() { ["record"] = record.Id.ToString(), ["kind"] = record.Kind, ["attribute"] = Short(record.Name, 64),
            ["references"] = new JsonArray(refs.Take(2).Select(r => JsonValue.Create(Short(r.Name, 64))).ToArray()),
            ["referencesTruncated"] = refs.Length > 2 || refs.Any(r => r.Name.Length > 64) };
    }
    public static IEnumerable<AiValveRecord> ForNode(AiNetwork network, AiNode node)
    {
        if (!network.IsMw3) yield break;
        if (node.Source is { } source)
        { foreach (var valve in Attributes(source, node.Index)) yield return valve; }
        else foreach (var valve in node.Valves) yield return valve;
        if (network.ValveEdgesByNode.TryGetValue(node.Index, out var edges))
            foreach (var edge in edges) foreach (var valve in Attributes(edge, null)) yield return valve;
    }
    public static bool HasAssociation(AiNetwork network, AiNode node, string? name)
    {
        if (!network.IsMw3) return false;
        if (node.Source is { } source)
        { if (HasAttribute(source, 3, name)) return true; }
        else if (node.Valves.Any(v => Matches(v.Name, v.Value, name))) return true;
        return network.ValveEdgesByNode.TryGetValue(node.Index, out var edges) && edges.Any(e => HasAttribute(e, 1, name));
    }
    private static bool HasAttribute(ZrdNode source, int first, string? name)
    {
        for (int i = first; i + 1 < source.Children.Count; i += 2)
        {
            var key = source.Children[i];
            if (key.Kind == ZrdKind.String && (first == 3 ? key.Text is "valve" or "valveunion" : key.Text == "valve_assign") && Matches(key.Text, source.Children[i + 1], name)) return true;
        }
        return false;
    }
    private static bool Matches(string attribute, ZrdNode value, string? name)
    {
        if (name == null) return true;
        var c = value.Children;
        if (attribute == "valve") return c.Count > 1 && c[1].Kind == ZrdKind.String && c[1].Text == name;
        if (attribute == "valve_assign") return c.Count > 0 && c[0].Kind == ZrdKind.String && c[0].Text == name;
        if (attribute == "valveunion") foreach (var term in c)
            if (term.Children.Count > 1 && term.Children[1].Kind == ZrdKind.String && term.Children[1].Text == name) return true;
        return false;
    }
    public static IEnumerable<AiValveRecord> EdgeAssignments(AiNetwork network, string? name = null)
    { foreach (var edge in network.ValveEdgeSources) foreach (var record in Attributes(edge, null, name)) yield return record; }
    private static IEnumerable<AiValveRecord> Attributes(ZrdNode source, int? node, string? name = null)
    {
        var c = source.Children;
        for (int i = node != null ? 3 : 1; i + 1 < c.Count; i += 2)
        {
            if (c[i].Kind != ZrdKind.String || (node != null ? c[i].Text is not ("valve" or "valveunion") : c[i].Text != "valve_assign") || !Matches(c[i].Text, c[i + 1], name)) continue;
            yield return new(c[i].Id, source.Id, i, c[i], c[i + 1], node != null ? "node" : "edge", node,
                node == null ? unchecked((int)c[0].Children[0].Bits) : null, node == null ? unchecked((int)c[0].Children[1].Bits) : null);
        }
    }
    public static ZrdNode Fields(ZrdNode root) => root.Kind == ZrdKind.Array && root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array ? root.Children[0] : root;
    public static bool IsNetwork(ZrdNode root)
    {
        var fields = Fields(root); if (fields.Kind != ZrdKind.Array || fields.Children.Count % 2 != 0) return false;
        int versions = 0;
        for (int i = 0; i < fields.Children.Count; i += 2)
        {
            if (fields.Children[i].Kind != ZrdKind.String) return false;
            if (fields.Children[i].Text != "version") continue;
            var v = fields.Children[i + 1];
            if (++versions != 1 || v.Kind != ZrdKind.Array || v.Children.Count != 1 || v.Children[0].Kind != ZrdKind.Int || v.Children[0].Bits != 106) return false;
        }
        return versions == 1;
    }
    public static IEnumerable<AiValveRecord> Records(string member, ZrdNode root, CancellationToken token = default)
    {
        var fields = Fields(root);
        if (fields.Kind != ZrdKind.Array) yield break;
        bool valves = member.Equals("valves.zrd", StringComparison.OrdinalIgnoreCase);
        bool network = MissionAiNetworks.IsCandidate(member) && IsNetwork(root);
        if (valves || network)
        {
            for (int i = 0; i + 1 < fields.Children.Count; i += 2)
            {
                token.ThrowIfCancellationRequested(); var key = fields.Children[i]; var value = fields.Children[i + 1];
                if (key.Kind != ZrdKind.String) continue;
                if (valves) { yield return new(key.Id, fields.Id, i, key, value, "definition"); continue; }
                if (!MissionAiNetworks.TryNodeIndex(key.Text, true, out int index)) continue;
                var c = value.Children;
                bool spatial = MissionAiNetworks.IsSpatial(value, true, token);
                bool edge = MissionAiNetworks.IsConstraint(value, token);
                if (!spatial && !edge) continue;
                for (int j = spatial ? 3 : 1; j + 1 < c.Count; j += 2)
                {
                    token.ThrowIfCancellationRequested();
                    if (c[j].Kind != ZrdKind.String || (spatial ? c[j].Text is not ("valve" or "valveunion") : c[j].Text != "valve_assign")) continue;
                    yield return new(c[j].Id, value.Id, j, c[j], c[j + 1], spatial ? "node" : "edge", spatial ? index : null,
                        edge ? unchecked((int)c[0].Children[0].Bits) : null, edge ? unchecked((int)c[0].Children[1].Bits) : null);
                }
            }
        }
        else if (member.Equals("objectives.zrd", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var record in Walk(root, 0)) yield return record;
            IEnumerable<AiValveRecord> Walk(ZrdNode node, int depth)
            {
                token.ThrowIfCancellationRequested(); if (node.Kind != ZrdKind.Array || depth > 128) yield break;
                for (int i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i];
                    if (i % 2 == 0 && child.Kind == ZrdKind.String && child.Text is "valve_change" or "set_valve" && i + 1 < node.Children.Count && node.Children[i + 1].Kind == ZrdKind.Array)
                        yield return new(child.Id, node.Id, i, child, node.Children[i + 1], "objective");
                    foreach (var record in Walk(child, depth + 1)) yield return record;
                }
            }
        }
    }

    public static IEnumerable<AiValveReference> References(AiValveRecord record, CancellationToken token = default)
    {
        var c = record.Value.Children;
        AiValveReference? Reference(ZrdNode? n, string role) => n?.Kind == ZrdKind.String ? new(record.Id, n.Id, n.Text, role) : null;
        if (record.Kind == "definition")
        {
            yield return new(record.Id, record.NameNode.Id, record.Name, "action trigger / compound output");
            if (c.Count >= 3 && c[0].Kind == ZrdKind.String && Conditions.Contains(c[0].Text) && c[1].Text == "namelist")
            { foreach (var n in c[2].Children) { token.ThrowIfCancellationRequested(); if (Reference(n, "compound input") is { } r) yield return r; } }
            else for (int i = 0; i + 1 < c.Count; i += 2)
            { token.ThrowIfCancellationRequested(); if (c[i].Text == "delayupdate" && Reference(c[i + 1].Children.FirstOrDefault(), "delayed update") is { } r) yield return r; }
        }
        else if (record.Name == "valve" && Reference(c.ElementAtOrDefault(1), "node condition") is { } condition) yield return condition;
        else if (record.Name == "valve_assign" && Reference(c.FirstOrDefault(), "edge assignment") is { } assignment) yield return assignment;
        else if (record is { Kind: "node", Name: "valveunion" })
        { foreach (var term in c) { token.ThrowIfCancellationRequested(); if (Reference(term.Children.ElementAtOrDefault(1), "union term") is { } r) yield return r; } }
        else if (record.Name == "set_valve" && Reference(c.FirstOrDefault(), "objective assignment") is { } objective) yield return objective;
        else if (record.Name == "valve_change")
        {
            for (int i = 0; i + 1 < c.Count; i += 2)
            { token.ThrowIfCancellationRequested(); if (c[i].Text == "valve" && Reference(c[i + 1].Children.FirstOrDefault(), "objective condition") is { } r) yield return r; }
        }
    }

    public static JsonObject Describe(AiValveRecord r, CancellationToken token = default)
    {
        var references = References(r, token).Take(9).ToArray();
        return new() { ["record"] = r.Id.ToString(), ["parent"] = r.Parent.ToString(), ["valueNode"] = r.Value.Id.ToString(), ["name"] = Short(r.Name),
            ["nameCharacters"] = r.Name.Length, ["kind"] = r.Kind, ["nodeIndex"] = r.NodeIndex, ["from"] = r.From, ["to"] = r.To, ["sourceOffset"] = r.SourceOffset,
            ["parameters"] = r.Value.ToPreviewJson(token, 24, 256, 5, 128), ["problem"] = Problem(r, token),
            ["references"] = new JsonArray(references.Take(8).Select(x => (JsonNode)new JsonObject { ["operand"] = x.Operand.ToString(), ["name"] = Short(x.Name, 128), ["nameCharacters"] = x.Name.Length, ["role"] = x.Role }).ToArray()),
            ["referencesTruncated"] = references.Length > 8 };
    }
    public static string? Problem(AiValveRecord r, CancellationToken token = default)
    {
        if (r.Value.Kind != ZrdKind.Array) return "Expected an ordered parameter array. Original data is retained.";
        var c = r.Value.Children;
        bool Tuple(ZrdNode n) => n.Kind == ZrdKind.Array && n.Children.Count is 2 or 3 && n.Children[0].Kind == ZrdKind.Int && n.Children[1].Kind == ZrdKind.String && (n.Children.Count == 2 || n.Children[2].Kind == ZrdKind.Int);
        if (r.Name == "valve" && r.Kind == "node" && !Tuple(r.Value)) return "Expected stored integer, valve name and optional stored integer.";
        if (r.Kind == "node" && r.Name == "valveunion" && (c.Count == 0 || c.Any(n => !Tuple(n)))) return "Expected ordered valve tuples.";
        if (r.Kind == "edge" && r.Name == "valve_assign" && (c.Count != 2 || c[0].Kind != ZrdKind.String || c[1].Kind != ZrdKind.Int)) return "Expected valve name and assigned integer.";
        if (r.Kind == "definition")
        {
            if (c.Count >= 1 && Conditions.Contains(c[0].Text)) return c.Count == 3 && c[1].Text == "namelist" && c[2].Kind == ZrdKind.Array && c[2].Children.All(n => n.Kind == ZrdKind.String) ? null : "Expected compound kind, namelist and ordered valve names.";
            if (c.Count % 2 != 0 || c.Count == 0) return "Unrecognized action block. Use source properties to preserve or repair it.";
            for (int i = 0; i < c.Count; i += 2) if (c[i].Kind != ZrdKind.String || c[i + 1].Kind != ZrdKind.Array || !Actions.Contains(c[i].Text)) return "Unrecognized action or layout; stored fields remain accessible.";
            for (int i = 0; i < c.Count; i += 2)
            {
                token.ThrowIfCancellationRequested();
                var p = c[i + 1].Children;
                bool valid = c[i].Text switch
                {
                    "attack_strategy" or "shutdown" => p.Count == 3 && p[0].Kind == ZrdKind.String && p[1].Kind == ZrdKind.Int && p[2].Kind == ZrdKind.Int,
                    "delayupdate" => p.Count == 2 && p[0].Kind == ZrdKind.String && p[1].Kind == ZrdKind.Float,
                    "destroy" or "sound" => p.Count == 2 && p[0].Kind == ZrdKind.String && p[1].Kind == ZrdKind.Int,
                    "sound_once" => p.Count == 2 && p[0].Kind == ZrdKind.String && p[1].Kind is ZrdKind.Int or ZrdKind.Float,
                    "teleport" => p.Count == 4 && p[0].Kind == ZrdKind.String && p[1].Kind == ZrdKind.Array && p[1].Children.Count == 3 && p[1].Children.All(n => n.Kind == ZrdKind.Float) && p[2].Kind == ZrdKind.Float && p[3].Kind == ZrdKind.Int,
                    _ => false
                };
                if (!valid) return $"Action {i / 2} has an unrecognized operand layout; original typed fields are retained.";
            }
        }
        if (r.Kind == "objective" && r.Name == "set_valve" && (c.Count != 2 || c[0].Kind != ZrdKind.String || c[1].Kind != ZrdKind.Int)) return "Expected objective valve name and assigned integer.";
        return null;
    }
}
