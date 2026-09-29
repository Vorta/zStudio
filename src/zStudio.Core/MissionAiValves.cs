using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed record AiValveRecord(Guid Id, Guid Parent, int ChildIndex, ZrdNode NameNode, ZrdNode Value,
    string Kind, int? NodeIndex = null, int? From = null, int? To = null)
{
    public string Name => NameNode.Text;
    public long SourceOffset => NameNode.SourceOffset;
}
public sealed record AiValveReference(Guid Record, Guid Operand, string Name, string Role);
public sealed record AiValveEdit(string Action, Guid Record = default, Guid Operand = default,
    string? Value = null, string? Kind = null, int Index = -1);
public sealed record AiValveSource(string Archive, int MemberIndex, string Member, ZrdNode Root, ReadOnlyMemory<byte> Data);
public sealed record AiValveTarget(Guid Id, string Name, long SourceOffset, bool Spatial);

/// <summary>Semantic views over the shared ZRD tree. Occurrences, not names, identify editable records.</summary>
public static class MissionAiValves
{
    public static AiNetworkSnapshot Attach(AiNetworkSnapshot graph, IEnumerable<ZbdDocument> archives, CancellationToken token)
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
    public static string Short(string text, int maximum = 256) => text.Length <= maximum ? text : text[..maximum] + "…";
    public static bool MatchesSearch(AiValveRecord record, string query) => record.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        (record.NodeIndex?.ToString(CultureInfo.InvariantCulture)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (record.From?.ToString(CultureInfo.InvariantCulture)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
        (record.To?.ToString(CultureInfo.InvariantCulture)?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);
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
    public static IEnumerable<AiValveTarget> Targets(ZrdNode root, CancellationToken token = default)
    {
        if (!IsNetwork(root)) yield break;
        var c = Fields(root).Children;
        for (int i = 0; i < c.Count; i += 2)
        {
            token.ThrowIfCancellationRequested();
            if (!MissionAiNetworks.TryNodeIndex(c[i].Text, true, out _)) continue;
            bool spatial = MissionAiNetworks.IsSpatial(c[i + 1], true, token);
            if (spatial || MissionAiNetworks.IsConstraint(c[i + 1], token)) yield return new(c[i + 1].Id, c[i].Text, c[i + 1].SourceOffset, spatial);
        }
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

    private static ZrdNode S(string s) => ZrdNode.Create(ZrdKind.String, JsonSerializer.Serialize(s));
    private static ZrdNode I(int n) => ZrdNode.Create(ZrdKind.Int, n.ToString(CultureInfo.InvariantCulture));
    private static ZrdNode F() => ZrdNode.Create(ZrdKind.Float, "0");
    private static ZrdNode A(params ZrdNode[] nodes) => ZrdNode.Create(ZrdKind.Array) with { Children = nodes };
    public static ZrdNode Parameters(string kind) => kind switch
    {
        "valve" => A(I(1), S(""), I(1)), "valveunion" => A(Parameters("valve")), "valve_assign" or "set_valve" => A(S(""), I(1)),
        "attack_strategy" or "shutdown" => A(S(""), I(0), I(1)), "destroy" or "sound" or "sound_once" => A(S(""), I(1)),
        "delayupdate" => A(S(""), F()), "teleport" => A(S(""), A(F(), F(), F()), F(), I(1)),
        _ => throw new InvalidDataException("Choose a supported valve action or attribute.")
    };
    public static ZrdNode Edit(string member, ZrdNode root, AiValveEdit edit, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var record = edit.Record == Guid.Empty ? null : Records(member, root, token).FirstOrDefault(r => r.Id == edit.Record);
        if (edit.Action == "add_record")
        {
            if (!member.Equals("valves.zrd", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Add valve records in valves.zrd.");
            string name = edit.Value ?? ""; if (name.Length is < 1 or > 16384) throw new InvalidDataException("Enter a valve name of 1–16384 characters.");
            string kind = edit.Kind ?? "delayupdate";
            if (!Actions.Contains(kind) && !Conditions.Contains(kind)) throw new InvalidDataException("Choose a supported valve action or compound condition for a definition.");
            var value = Conditions.Contains(kind) ? A(S(kind), S("namelist"), A(S(""))) : A(S(kind), Parameters(kind));
            var fields = Fields(root);
            if (fields.Kind != ZrdKind.Array || fields.Children.Count % 2 != 0 || fields.Children.Where((_, i) => i % 2 == 0).Any(n => n.Kind != ZrdKind.String)) throw new InvalidDataException("Repair the incomplete source name/value pairs before adding a record.");
            return Change(root, fields.Id, n => n with { Children = [.. n.Children, S(name), value] }, token);
        }
        if (edit.Action == "add_binding")
        {
            if (!MissionAiNetworks.IsCandidate(member) || !IsNetwork(root)) throw new InvalidDataException("Valve bindings require a version-106 network.");
            var target = Targets(root, token).FirstOrDefault(n => n.Id == edit.Operand) ?? throw new InvalidDataException("Choose an existing spatial node or edge record.");
            bool spatial = target.Spatial;
            var owner = Fields(root).Children.First(n => n.Id == target.Id);
            if (owner.Children.Count % 2 != 1) throw new InvalidDataException("Repair the incomplete attribute pairs before adding a binding.");
            string kind = edit.Kind ?? (spatial ? "valve" : "valve_assign");
            if (spatial ? kind is not ("valve" or "valveunion") : kind != "valve_assign") throw new InvalidDataException("Attribute is not supported for this record kind.");
            return Change(root, owner.Id, n => n with { Children = [.. n.Children, S(kind), Parameters(kind)] }, token);
        }
        if (record == null) throw new InvalidDataException("The valve record no longer exists. Refresh its source identity.");
        if (edit.Action is "add_option" or "remove_option")
        {
            var tuple = record.Name == "valve" && record.Kind == "node" ? record.Value : record is { Kind: "node", Name: "valveunion" } ? record.Value.Children.FirstOrDefault(n => n.Id == edit.Operand) : null;
            if (tuple?.Kind != ZrdKind.Array || tuple.Children.Count is not (2 or 3) || tuple.Children[0].Kind != ZrdKind.Int || tuple.Children[1].Kind != ZrdKind.String || tuple.Children.Count == 3 && tuple.Children[2].Kind != ZrdKind.Int)
                throw new InvalidDataException("Choose a two/three-field node condition or union term.");
            return Change(root, tuple.Id, n => n with { Children = edit.Action == "add_option" ? [n.Children[0], n.Children[1], n.Children.Count == 3 ? n.Children[2] : I(1)] : [n.Children[0], n.Children[1]] }, token);
        }
        if (edit.Action == "set")
        {
            var target = edit.Operand == record.NameNode.Id ? record.NameNode : record.Value.Find(edit.Operand);
            if (target == null || target.Kind == ZrdKind.Array) throw new InvalidDataException("Choose a scalar operand in this valve record.");
            if (target.Id == record.NameNode.Id && record.Kind != "definition") throw new InvalidDataException("The binding kind cannot be renamed; replace the attribute explicitly.");
            if (edit.Value == null) throw new InvalidDataException("Specify the typed scalar value.");
            return Change(root, target.Id, n => ZrdNode.Set(n, n.Kind, edit.Value), token);
        }
        if (edit.Action is "delete" or "duplicate" or "move")
            return Change(root, record.Parent, parent =>
            {
                var children = parent.Children.ToList(); int at = record.ChildIndex;
                var pair = children.GetRange(at, 2);
                if (edit.Action == "duplicate") children.InsertRange(at + 2, pair.Select(n => n.Duplicate()));
                else
                {
                    children.RemoveRange(at, 2);
                    if (edit.Action == "move")
                    {
                        int first = record.Kind == "node" ? 3 : record.Kind == "edge" ? 1 : 0;
                        long to = first + 2L * edit.Index;
                        if (edit.Index < 0 || to > children.Count) throw new InvalidDataException("Destination pair index is outside its owner.");
                        children.InsertRange((int)to, pair);
                    }
                }
                return parent with { Children = children.ToArray() };
            }, token);
        if (edit.Action is "add_action" or "add_term")
        {
            if (edit.Action == "add_action" && (record.Kind != "definition" || record.Value.Kind != ZrdKind.Array || record.Value.Children.Count % 2 != 0 || record.Value.Children.Count != 0 && Problem(record, token) != null)) throw new InvalidDataException("Choose a recognized action block.");
            Guid destination = record.Value.Id; ZrdNode[] appended;
            if (edit.Action == "add_action") { var kind = edit.Kind ?? "delayupdate"; if (!Actions.Contains(kind)) throw new InvalidDataException("Choose a supported valve action."); appended = [S(kind), Parameters(kind)]; }
            else if (record is { Kind: "node", Name: "valveunion", Value.Kind: ZrdKind.Array }) appended = [Parameters("valve")];
            else if (record.Kind == "definition" && record.Value.Children.Count == 3 && record.Value.Children[1].Text == "namelist" && record.Value.Children[2].Kind == ZrdKind.Array && Conditions.Contains(record.Value.Children[0].Text)) { destination = record.Value.Children[2].Id; appended = [S(edit.Value ?? "")]; }
            else throw new InvalidDataException("Choose a union or compound definition.");
            return Change(root, destination, n => n with { Children = [.. n.Children, .. appended] }, token);
        }
        if (edit.Action is "delete_item" or "duplicate_item" or "move_item")
        {
            bool pair = record.Kind == "definition" && record.Value.Children.Count % 2 == 0 && Problem(record, token) == null;
            ZrdNode container = record.Value;
            if (!pair && record.Kind == "definition" && container.Children.Count == 3 && container.Children[1].Text == "namelist" && container.Children[2].Kind == ZrdKind.Array && Conditions.Contains(container.Children[0].Text)) container = container.Children[2];
            else if (!pair && record is not { Kind: "node", Name: "valveunion" }) throw new InvalidDataException("Choose an action block, compound or union.");
            int width = pair ? 2 : 1;
            int at = container.Children.ToList().FindIndex(n => n.Id == edit.Operand);
            if (at < 0 || at % width != 0 || at + width > container.Children.Count) throw new InvalidDataException("Choose an action name or term identity.");
            return Change(root, container.Id, n =>
            {
                var list = n.Children.ToList(); var item = list.GetRange(at, width);
                if (edit.Action == "duplicate_item") list.InsertRange(at + width, item.Select(c => c.Duplicate()));
                else { list.RemoveRange(at, width); if (edit.Action == "move_item") { if (edit.Index < 0 || (long)edit.Index * width > list.Count) throw new InvalidDataException("Item index is outside the container."); list.InsertRange(edit.Index * width, item); } }
                return n with { Children = list.ToArray() };
            }, token);
        }
        throw new InvalidDataException("Unsupported valve edit.");
    }
    private static ZrdNode Change(ZrdNode root, Guid id, Func<ZrdNode, ZrdNode> edit, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); if (root.Id == id) return edit(root);
        if (root.Kind != ZrdKind.Array) return root;
        ZrdNode[]? changed = null;
        for (int i = 0; i < root.Children.Count; i++)
        { var next = Change(root.Children[i], id, edit, token); if (!ReferenceEquals(next, root.Children[i])) { changed ??= root.Children.ToArray(); changed[i] = next; } }
        return changed == null ? root : root with { Children = changed };
    }
}
