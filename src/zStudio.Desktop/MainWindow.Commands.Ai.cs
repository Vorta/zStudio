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
        Register(registry, "ai_networks", "List authored AI networks with distinct source identities, counts and diagnostics. Does not simulate activation or change visualization.", false,
            [PreviewParameter, .. PageParameters], args =>
        {
            var graph = TargetAiGraph(args, false);
            return Page(graph.Networks.Where(n => (n.Member + " " + n.Name + " " + n.Type + " " + n.Archive).Contains(Text(args, "query"), StringComparison.OrdinalIgnoreCase))
                , args, project: n => new { snapshot = graph.Id, n.Id, n.Archive, n.MemberIndex, n.Member, Name = ShortAiText(n.Name), Type = ShortAiText(n.Type), n.PathWidth,
                    nodes = n.Nodes.Count, links = n.Nodes.Sum(p => p.Links.Count(l => l.Target != null)), diagnostics = n.Diagnostics.Take(32).ToArray(), diagnosticCount = n.Diagnostics.Count });
        });
        Register(registry, "ai_nodes", "List authored AI nodes, XYZ, raw integer and all three directed link slots. Negative indices mean no link; unresolved targets retain diagnostics.", false,
            [PreviewParameter, AiSnapshotParameter, P("network", "string", "Network ID, or all/omitted for all networks."), .. PageParameters], args =>
        {
            var graph = TargetAiGraph(args); string network = Text(args, "network", "all");
            if (network != "all" && !graph.Networks.Any(n => n.Id == network)) throw new StudioCommandException("stale_record", "AI network unavailable.");
            return Page(graph.Networks.Where(n => network == "all" || n.Id == network).SelectMany(n => n.Nodes
                .Where(p => $"{n.Member} {n.Name} node_{p.Index:00}".Contains(Text(args, "query"), StringComparison.OrdinalIgnoreCase)).Select(p => (Network: n, Node: p))), args, project: p => graph.Describe(p.Network, p.Node));
        });
        Register(registry, "ai_selection", "Select/clear an AI marker or open its pinned read-only Properties. Selection requires enabled visualization and a matching filter. Frame it with camera action=frame,target=selected.", true,
            [PreviewParameter, AiSnapshotParameter, P("action", "string", "Selection action.", true, "select", "clear", "properties"), P("node", "string", "Snapshot-scoped node ID; required except for clear.")], async (args, token) =>
        {
            RequireNoDrafts(); var graph = TargetAiGraph(args); string action = Text(args, "action"), id = Text(args, "node");
            if (scene!.IsPickupDragging || scene.IsFlyActive) throw new StudioCommandException("busy", "Finish the pickup drag or exit Fly before changing AI inspection.");
            if (action == "clear") { scene.SelectAiNode(null); return Result(new { selectedNode = scene.SelectedAiNode }); }
            var target = graph.Find(id) ?? throw new StudioCommandException("stale_record", "AI node unavailable.");
            if (action == "select" && !scene.SelectAiNode(id)) throw new StudioCommandException("not_ready", "Enable AI nodes and choose All networks or this node's network before selecting it.");
            if (action == "properties")
            {
                var window = await OpenAiPropertiesAsync(id, true); token.ThrowIfCancellationRequested();
                if (window == null) throw new StudioCommandException("context_changed", "Properties target was not published.");
            }
            return Result(graph.Describe(target.Network, target.Node));
        });
    }
}
