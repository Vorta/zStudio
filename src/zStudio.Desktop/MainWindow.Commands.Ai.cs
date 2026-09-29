using System.Text.Json.Nodes;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private static StudioParameter AiSnapshotParameter => P("snapshot", "string", "AI graph snapshot from preview_state.ai. Handles apply only to this snapshot.", true);
    private AiNetworkSnapshot TargetAiGraph(JsonObject args, bool snapshot = true)
    {
        RequirePreview(args); TargetViewport(args);
        if (!IsAiWorld) throw new StudioCommandException("unsupported", "AI networks are available only in Whole world.");
        var graph = scene!.AiNetworks;
        if (snapshot && Text(args, "snapshot") != graph.Id) throw new StudioCommandException("stale_snapshot", "AI resources changed. Read preview_state and list the current graph.");
        return graph;
    }
    private void RegisterAiCommands(StudioCommands registry)
    {
        Register(registry, "ai_networks", "List authored AI networks with source identities, counts and attack_strategy metadata/color (1024-character value preview). Queries match member/name/type prefixes (70 characters) or source archive before pagination. links counts resolved preview links; linkSlots counts authored slots, with linksTruncated when capped. Diagnostics preview 8 messages at 128 characters with diagnosticCount/diagnosticsTruncated. Does not simulate activation or change visualization.", false,
            [PreviewParameter, .. PageParameters], args =>
        {
            var graph = TargetAiGraph(args, false);
            return Page(graph.Networks, args, search: n => $"{ShortAiText(n.Member)} {ShortAiText(n.Name)} {ShortAiText(n.Type)} {n.Archive}",
                project: n => new { snapshot = graph.Id, n.Id, n.Archive, n.MemberIndex, n.Member, Name = ShortAiText(n.Name), Type = ShortAiText(n.Type), n.PathWidth,
                    attack_strategy = DescribeAiStrategy(n),
                    nodes = n.Nodes.Count, constraints = n.Constraints.Count, links = n.Nodes.Sum(p => p.PreviewLinks.Count(l => l.Target != null)),
                    linkSlots = n.Nodes.Sum(p => (long)p.LinkCount), linksTruncated = n.Nodes.Any(p => p.LinksTruncated),
                    diagnostics = n.Diagnostics.Take(8).Select(d => d with { Message = d.Message[..Math.Min(128, d.Message.Length)] }).ToArray(), diagnosticCount = n.Diagnostics.Count,
                    diagnosticsTruncated = n.Diagnostics.Count > 8 || n.Diagnostics.Take(8).Any(d => d.Message.Length > 128) });
        });
        Register(registry, "ai_nodes", "List authored AI nodes, XYZ, raw integer, attack_strategy and ordered link slots (three for RECOIL; MW3 preview capped at 32). link_count/links_truncated disclose full slot count and omission; inspect/export the source ZRD for all links. Network name/type previews use 256 characters with counts/truncation flags. Node queries match 70-character member/name prefixes or node_NN before pagination. Negative indices mean no link; unresolved targets retain diagnostics.", false,
            [PreviewParameter, AiSnapshotParameter, P("network", "string", "Network ID, or all/omitted for all networks."), P("section", "string", "Spatial nodes (default) or MW3 edge constraints, which have no authored position. Constraint queries match member/name prefixes (70 characters), kind prefix (256), edge_NN, attribute_NN or node_NN endpoints before pagination. KindCharacters/KindTruncated disclose shortened kinds. Parameters is a bounded preview (64 nodes, depth 8, 2048 total text characters, 512 per string), with ParametersTruncated.", false, "nodes", "constraints"), .. PageParameters], args =>
        {
            var graph = TargetAiGraph(args); string network = Text(args, "network", "all");
            if (network != "all" && !graph.Networks.Any(n => n.Id == network)) throw new StudioCommandException("stale_record", "AI network unavailable.");
            if (Text(args, "section") == "constraints") return Page(graph.Networks.Where(n => network == "all" || n.Id == network)
                .SelectMany(n => n.Constraints.Select(c => (Network: n, Constraint: c))), args,
                search: p => FormattableString.Invariant($"{ShortAiText(p.Network.Member)} {ShortAiText(p.Network.Name)} {ConstraintKind(p.Constraint)} edge_{p.Constraint.Index:00} attribute_{p.Constraint.AttributeIndex:00} node_{p.Constraint.FromNode:00} node_{p.Constraint.ToNode:00}"),
                project: p => new { network = p.Network.Id, constraint = DescribeConstraint(p.Constraint) });
            return Page(graph.Networks.Where(n => network == "all" || n.Id == network).SelectMany(n => n.Nodes.Select(p => (Network: n, Node: p))), args,
                search: p => $"{ShortAiText(p.Network.Member)} {ShortAiText(p.Network.Name)} node_{p.Node.Index:00}", project: p => DescribeAiNode(graph, p.Network, p.Node));
        });
        Register(registry, "ai_selection", "Select/clear an AI marker or open its pinned read-only Properties. Selection requires unlocked Whole world editing, enabled visualization and a matching filter. Read-only Properties remains available while locked. Frame a selected marker with camera action=frame,target=selected.", true,
            [PreviewParameter, AiSnapshotParameter, P("action", "string", "Selection action.", true, "select", "clear", "properties"), P("node", "string", "Snapshot-scoped node ID; required except for clear.")], async (args, token) =>
        {
            RequireNoDrafts(); var graph = TargetAiGraph(args); string action = Text(args, "action"), id = Text(args, "node");
            if (scene!.IsPickupDragging || scene.IsFlyActive) throw new StudioCommandException("busy", "Finish the pickup drag or exit Fly before changing AI inspection.");
            if (action == "clear") { scene.SelectAiNode(null); return Result(new { selectedNode = scene.SelectedAiNode }); }
            var target = graph.Find(id) ?? throw new StudioCommandException("stale_record", "AI node unavailable.");
            if (action == "select" && !scene.InspectionSelectionEnabled) throw new StudioCommandException("locked", "Unlock editing before selecting an AI marker.");
            if (action == "select" && !scene.SelectAiNode(id)) throw new StudioCommandException("not_ready", "Enable AI nodes and choose All networks or this node's network before selecting it.");
            if (action == "properties")
            {
                var window = await OpenAiPropertiesAsync(id, true); token.ThrowIfCancellationRequested();
                if (window == null) throw new StudioCommandException("context_changed", "Properties target was not published.");
            }
            return Result(DescribeAiNode(graph, target.Network, target.Node));
        });
    }

    private static string ConstraintKind(AiConstraint constraint) => constraint.Kind[..Math.Min(256, constraint.Kind.Length)];
    private static object DescribeConstraint(AiConstraint constraint)
    {
        var parameters = ConstraintParameters(constraint.Parameters);
        return new { constraint.Index, constraint.AttributeIndex, constraint.FromNode, constraint.ToNode, constraint.SourceOffset,
            Kind = ConstraintKind(constraint), KindCharacters = constraint.Kind.Length, KindTruncated = constraint.Kind.Length > 256,
            Parameters = parameters.Value, ParametersTruncated = parameters.Truncated };
    }
    // Clone only a small preview: never serialize authored strings/subtrees before applying the budgets.
    private static (JsonNode? Value, bool Truncated) ConstraintParameters(JsonNode source)
    {
        int nodes = 64, characters = 2048; bool truncated = false;
        var value = Visit(source, 0); return (value, truncated);
        JsonNode? Visit(JsonNode? node, int depth)
        {
            if (nodes-- <= 0 || depth > 8) { truncated = true; return null; }
            if (node is JsonObject obj)
            {
                JsonObject result = [];
                foreach (var pair in obj)
                {
                    if (nodes <= 0 || pair.Key.Length > characters) { truncated = true; break; }
                    characters -= pair.Key.Length;
                    result[pair.Key] = Visit(pair.Value, depth + 1);
                    if (pair.Key.EndsWith("_truncated", StringComparison.Ordinal) && pair.Value is JsonValue flag && flag.TryGetValue<bool>(out bool alreadyTruncated) && alreadyTruncated) truncated = true;
                }
                return result;
            }
            if (node is JsonArray array)
            {
                JsonArray result = [];
                foreach (var child in array) { if (nodes <= 0) { truncated = true; break; } result.Add(Visit(child, depth + 1)); }
                return result;
            }
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out string? text))
            {
                int count = Math.Min(text.Length, Math.Min(512, characters)); characters -= count;
                truncated |= count != text.Length; return JsonValue.Create(text[..count]);
            }
            return node?.DeepClone();
        }
    }
}
