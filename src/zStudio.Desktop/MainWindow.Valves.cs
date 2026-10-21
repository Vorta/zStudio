using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Formats;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private void RegisterValveCommands(StudioCommands registry)
    {
        RegisterJob(registry, "ai_valves", "Page authored MW3 valve records or exact-name references of a resource. Includes repeated triggers, compound definitions and node/edge/objective uses; no runtime state is inferred. Query filters record names or valve references before paging. Parameters and reference previews are bounded; zrd_nodes/export retains complete data.",
            [DocumentParameter, MemberParameter, P("section", "string", "Records or references.", false, "records", "references"), ..PageParameters], false, async (args, token) =>
        {
            var doc = TargetDocument(args); long revision = doc.Revision; var member = ResourceSession(doc).Member(GuidArg(args, "member"));
            var root = await ResourceTreeAsync(doc, member.Id, token);
            var page = await Task.Run(() =>
            {
                var records = MissionAiValves.Records(member.Name, root, token);
                return Text(args, "section") == "references"
                    ? Page(records.SelectMany(r => MissionAiValves.References(r, token)), args, r => r.Name, r => new JsonObject { ["record"] = r.Record.ToString(), ["operand"] = r.Operand.ToString(), ["name"] = MissionAiValves.Short(r.Name), ["nameCharacters"] = r.Name.Length, ["role"] = r.Role })
                    : Page(records, args, project: r => MissionAiValves.Describe(r, token), matches: MissionAiValves.MatchesSearch);
            }, token);
            token.ThrowIfCancellationRequested(); CheckResourceContext(doc, revision); return Result(new { revision, records = page.Data });
        });
        RegisterJob(registry, "ai_valve_selection", "Discover mission valve resources, page their records or references, and highlight or frame valve associations in the Whole world preview. Uses the current AI snapshot and source member index, never names as identity.",
            [PreviewParameter, AiSnapshotParameter, P("action", "string", "Selection operation.", true, "sources", "records", "references", "overlay", "highlight", "frame", "clear"), P("archive", "string", "Exact source archive path from sources."), P("memberIndex", "integer", "Source member index."), P("name", "string", "Exact valve name for highlighting."), P("visible", "boolean", "Overlay visibility."), ..PageParameters], false, async (args, token) =>
        {
            RequireNoDrafts(); var graph = TargetAiGraph(args);
            if (Text(args, "action") == "sources") return Page(graph.ValveSources, args, s => s.Member, s => new { s.Archive, s.MemberIndex, s.Member, snapshot = graph.Id },
                maximumRowBytes: s => 256 + InspectionResultBudget.Text(s.Archive) + InspectionResultBudget.Text(s.Member) + InspectionResultBudget.Text(graph.Id));
            string action = Text(args, "action");
            if (action is "overlay" or "highlight" or "frame" or "clear")
            {
                if (action == "frame") { if (!scene!.FrameValveAssociations()) throw new StudioCommandException("not_ready", "Enable AI nodes and select a valve with uniquely resolved spatial associations."); }
                else
                {
                    if (action == "highlight" && !args.ContainsKey("name")) throw new StudioCommandException("invalid_argument", "Specify an exact authored valve name.");
                    if (action == "highlight") SetAiOptions(true, aiThroughGeometry, null);
                    scene!.SetValveOptions(action == "overlay" ? Flag(args, "visible") : action != "clear", action == "highlight" ? Text(args, "name") : action == "overlay" ? scene.ValveFilter : null);
                    ApplyAiOptions();
                }
                return Result(new { scene!.ValveOverlayVisible, ValveFilter = ValveFilterText(), valveFilterCharacters = scene.ValveFilter?.Length ?? 0, valveFilterTruncated = scene.ValveFilter?.Length > 256 });
            }
            var source = graph.ValveSources.SingleOrDefault(s => s.Archive.Equals(Text(args, "archive"), StringComparison.OrdinalIgnoreCase) && s.MemberIndex == Int(args, "memberIndex", -1)) ?? throw new StudioCommandException("stale_record", "Choose an exact mission valve source.");
            var page = await Task.Run(() => action == "records"
                ? Page(MissionAiValves.Records(source.Member, source.Root, token), args, r => r.Name, r => MissionAiValves.Describe(r, token))
                : Page(MissionAiValves.Records(source.Member, source.Root, token).SelectMany(r => MissionAiValves.References(r, token)), args, r => r.Name,
                    r => new JsonObject { ["record"] = r.Record.ToString(), ["operand"] = r.Operand.ToString(), ["name"] = MissionAiValves.Short(r.Name), ["role"] = r.Role }), token);
            _ = TargetAiGraph(args); return page;
        });
    }
    private void AiValveOverlayChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || synchronizingAi || !IsAiWorld) return;
        if (!ResolveInspectionDrafts()) { ApplyAiOptions(); return; }
        scene!.SetValveOptions(AiValveOverlay.IsChecked == true, scene.ValveFilter);
    }
}
