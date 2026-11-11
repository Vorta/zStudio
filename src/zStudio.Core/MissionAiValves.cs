using System.Globalization;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using zStudio.Core.Formats;

namespace zStudio.Core;

public sealed record AiValveRecord(Guid Id, Guid Parent, int ChildIndex, ZrdNode NameNode, ZrdNode Value,
    string Kind, int? NodeIndex = null, int? From = null, int? To = null)
{
    public string Name => NameNode.Text;
    public IReadOnlyList<int> TargetNodes { get; init; } = [];
    public int TargetNodeCount => Kind == "edge" ? TargetNodes.Count == 256 ? -1 : TargetNodes.Count : 0;
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
    public static readonly string[] Actions = [.. Mw3ValveProgram.ActionOrder, "sound_once", "animation_once", "value"];
    public static readonly string[] Conditions = Mw3ValveProgram.Conditions;
    public static bool IsResource(string name) => name.Equals("valves.zrd", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("objectives.zrd", StringComparison.OrdinalIgnoreCase) || name.Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("targets.zrd", StringComparison.OrdinalIgnoreCase) || MissionAiNetworks.IsCandidate(name, mw3: true);
    private static bool ValveAttribute(string key) => key is "valve" or "valveunion" or "valve_assign";
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
            if (key.Kind != ZrdKind.String) continue;
            if (ValveAttribute(key.Text) && Matches(key.Text, source.Children[i + 1], name)) return true;
            // A deploy point names two valves and a wait point one (Mw3ValveMap lists them): found when a valve is asked for by
            // name. The overlay of every valve attribute (no name) keeps to gates, unions and assignments.
            if (name != null && key.Text is "deploy" or "mcbwait" && source.Children[i + 1].Kind == ZrdKind.Array)
                foreach (int at in key.Text == "deploy" ? new[] { 0, 2 } : [0])
                    if (source.Children[i + 1].Children.ElementAtOrDefault(at) is { Kind: ZrdKind.String } named && named.Text == name) return true;
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
    { foreach (var edge in network.ValveEdgeSources) foreach (var record in Attributes(edge, null, name).Where(r => r.Name == "valve_assign")) yield return record; }
    private static IEnumerable<AiValveRecord> Attributes(ZrdNode source, int? node, string? name = null)
    {
        var c = source.Children;
        for (int i = node != null ? 3 : 1; i + 1 < c.Count; i += 2)
        {
            if (c[i].Kind != ZrdKind.String || !ValveAttribute(c[i].Text) || !Matches(c[i].Text, c[i + 1], name)) continue;
            yield return new(c[i].Id, source.Id, i, c[i], c[i + 1], node != null ? "node" : "edge", node,
                node == null ? unchecked((int)c[0].Children[0].Bits) : null, node == null ? unchecked((int)c[0].Children[1].Bits) : null)
                { TargetNodes = node == null ? c[0].Children.Skip(1).Take(256).Select(n => unchecked((int)n.Bits)).ToArray() : [] };
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
        bool network = MissionAiNetworks.IsCandidate(member, mw3: true) && IsNetwork(root);
        if (valves || network)
        {
            for (int i = 0; i + 1 < fields.Children.Count; i += 2)
            {
                token.ThrowIfCancellationRequested(); var key = fields.Children[i]; var value = fields.Children[i + 1];
                if (key.Kind != ZrdKind.String) continue;
                if (valves) { yield return new(key.Id, fields.Id, i, key, value, "definition"); continue; }
                if (!MissionAiNetworks.TryNodeIndex(key.Text, out int index)) continue;
                var c = value.Children;
                bool spatial = MissionAiNetworks.IsSpatial(value, true, token);
                bool edge = MissionAiNetworks.IsConstraint(value, token);
                if (!spatial && !edge) continue;
                for (int j = spatial ? 3 : 1; j + 1 < c.Count; j += 2)
                {
                    token.ThrowIfCancellationRequested();
                    if (c[j].Kind != ZrdKind.String || !ValveAttribute(c[j].Text)) continue;
                    yield return new(c[j].Id, value.Id, j, c[j], c[j + 1], spatial ? "node" : "edge", spatial ? index : null,
                        edge ? unchecked((int)c[0].Children[0].Bits) : null, edge ? unchecked((int)c[0].Children[1].Bits) : null)
                        { TargetNodes = edge ? c[0].Children.Skip(1).Take(256).Select(n => unchecked((int)n.Bits)).ToArray() : [] };
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
        else if (member.Equals("targets.zrd", StringComparison.OrdinalIgnoreCase) || member.Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase))
        {
            bool target = member.Equals("targets.zrd", StringComparison.OrdinalIgnoreCase);
            for (int i = 0; i + 1 < fields.Children.Count; i += 2)
            {
                token.ThrowIfCancellationRequested();
                var owner = fields.Children[i + 1];
                if (fields.Children[i].Kind != ZrdKind.String || owner.Kind != ZrdKind.Array) continue;
                if (target && Mw3ValveProgram.Find(owner, "health_below") is { } thresholds)
                    for (int j = 0; j + 2 < thresholds.Children.Count; j += 3)
                        if (thresholds.Children[j] is { Kind: ZrdKind.String } name)
                            yield return new(name.Id, thresholds.Id, j, name, thresholds, "target");
                if (!target)
                {
                    if (Mw3ValveProgram.Find(owner, "vlist") is { } list)
                        foreach (var trigger in list.Children)
                        {
                            token.ThrowIfCancellationRequested();
                            if (trigger.Children.Count >= 16 && trigger.Children[15] is { Kind: ZrdKind.String } name)
                                yield return new(name.Id, trigger.Id, 15, name, trigger, "actor");
                        }
                    if (Mw3ValveProgram.Find(owner, "p4") is { Children.Count: >= 23 } old && old.Children[22] is { Kind: ZrdKind.String } n)
                        yield return new(n.Id, old.Id, 22, n, old, "actor");
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
            if (Conditions.Any(k => Mw3ValveProgram.HasTag(record.Value, k)) && Mw3ValveProgram.Find(record.Value, "namelist") is { } names)
                foreach (var n in names.Children) { token.ThrowIfCancellationRequested(); if (Reference(n, "compound input") is { } input) yield return input; }
            foreach (string tag in new[] { "update", "delayupdate" })
            {
                token.ThrowIfCancellationRequested();
                if (Mw3ValveProgram.Find(record.Value, tag) is { } update && Reference(update.Children.FirstOrDefault(), tag == "update" ? "update" : "delayed update") is { } target)
                    yield return target;
            }
        }
        else if (record.Kind is "node" or "edge" && record.Name == "valve" && Reference(c.ElementAtOrDefault(1), "node condition") is { } condition) yield return condition;
        else if (record.Kind is "node" or "edge" && record.Name == "valve_assign" && Reference(c.FirstOrDefault(), "edge assignment") is { } assignment) yield return assignment;
        else if (record.Name == "valveunion" && record.Kind is "node" or "edge")
        { foreach (var term in c) { token.ThrowIfCancellationRequested(); if (Reference(term.Children.ElementAtOrDefault(1), "union term") is { } r) yield return r; } }
        else if (record.Kind == "objective" && record.Name == "set_valve" && Reference(c.FirstOrDefault(), "objective assignment") is { } objective) yield return objective;
        else if (record.Kind == "objective" && record.Name == "valve_change")
        {
            for (int i = 0; i + 1 < c.Count; i += 2)
            { token.ThrowIfCancellationRequested(); if (c[i].Text == "valve" && Reference(c[i + 1].Children.FirstOrDefault(), "objective condition") is { } r) yield return r; }
        }
        else if (record.Kind is "actor" or "target")
            yield return new(record.Id, record.NameNode.Id, record.Name, record.Kind == "actor" ? "actor trigger" : "health threshold");
    }

    public static JsonObject Describe(AiValveRecord r, CancellationToken token = default)
    {
        var references = References(r, token).Take(9).ToArray();
        return new() { ["record"] = r.Id.ToString(), ["parent"] = r.Parent.ToString(), ["valueNode"] = r.Value.Id.ToString(), ["name"] = Short(r.Name),
            ["nameCharacters"] = r.Name.Length, ["kind"] = r.Kind, ["nodeIndex"] = r.NodeIndex, ["from"] = r.From, ["to"] = r.To, ["sourceOffset"] = r.SourceOffset,
            ["targetNodes"] = new JsonArray(r.TargetNodes.Select(n => JsonValue.Create(n)).ToArray()), ["targetNodesMayBeTruncated"] = r.TargetNodes.Count == 256,
            ["parameters"] = r.Value.ToPreviewJson(token, 24, 256, 5, 128), ["problem"] = Problem(r, token),
            ["references"] = new JsonArray(references.Take(8).Select(x => (JsonNode)new JsonObject { ["operand"] = x.Operand.ToString(), ["name"] = Short(x.Name, 128), ["nameCharacters"] = x.Name.Length, ["role"] = x.Role }).ToArray()),
            ["referencesTruncated"] = references.Length > 8 };
    }
    public static string? Problem(AiValveRecord r, CancellationToken token = default)
    {
        if (r.Value.Kind != ZrdKind.Array) return "Expected an ordered parameter array. Original data is retained.";
        var c = r.Value.Children;
        bool Word(ZrdNode n) => n.Kind is ZrdKind.Int or ZrdKind.Float;
        bool Tuple(ZrdNode n) => n.Kind == ZrdKind.Array && n.Children.Count is 2 or 3 && Word(n.Children[0]) && n.Children[1].Kind == ZrdKind.String && (n.Children.Count == 2 || Word(n.Children[2]));
        if (r.Name == "valve" && r.Kind is "node" or "edge" && !Tuple(r.Value)) return "Expected stored integer, valve name and optional stored integer.";
        if (r.Kind is "node" or "edge" && r.Name == "valveunion" && (c.Count == 0 || c.Any(n => !Tuple(n)))) return "Expected ordered valve tuples.";
        if (r.Kind is "node" or "edge" && r.Name == "valve_assign" && (c.Count != 2 || c[0].Kind != ZrdKind.String || !Word(c[1]))) return "Expected valve name and assigned integer.";
        if (r.Kind == "definition")
        {
            if (Mw3ValveProgram.SourceProblem(r.Value) is { } problem) return problem;
            var root = ZrdNode.Create(ZrdKind.Array) with { Children = [r.NameNode, r.Value] };
            var definition = Mw3ValveProgram.Read(root, token).Definitions[0];
            if (definition.Condition == null && definition.Initial == null && definition.Actions.Count == 0)
                return "No recognized native property or action; stored fields remain accessible.";
            return Mw3ValveProgram.Problem(definition);
        }
        if (r.Kind == "objective" && r.Name == "set_valve" && (c.Count != 2 || c[0].Kind != ZrdKind.String || !Word(c[1]))) return "Expected objective valve name and assigned integer.";
        if (r.Kind == "objective" && r.Name == "valve_change")
        {
            if (Mw3ValveProgram.Find(r.Value, "valve") is not { Kind: ZrdKind.Array, Children.Count: 1 } v || v.Children[0].Kind != ZrdKind.String)
                return "Expected valve (name) and optional value (comparison word).";
            if (Mw3ValveProgram.HasTag(r.Value, "value") &&
                (Mw3ValveProgram.Find(r.Value, "value") is not { Kind: ZrdKind.Array, Children.Count: 1 } comparison || !Word(comparison.Children[0])))
                return "Expected one objective comparison word.";
        }
        if (r.Kind == "target" && (c.Count % 3 != 0 || Enumerable.Range(0, c.Count / 3).Any(i =>
            c[i * 3].Kind != ZrdKind.String || !Word(c[i * 3 + 1]) || !Word(c[i * 3 + 2]) || !float.IsFinite(Mw3ValveRuntime.Number(r.Value, i * 3 + 2))))) return "Expected health threshold triples: valve name, assigned word, finite fraction.";
        if (r.Kind == "actor" && (r.ChildIndex >= c.Count || c[r.ChildIndex].Kind != ZrdKind.String ||
            c.Count < r.ChildIndex + 2 || !Word(c[r.ChildIndex + 1]))) return "Expected actor trigger name and assigned word.";
        return null;
    }
}
