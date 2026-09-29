using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private void RegisterValveCommands(StudioCommands registry)
    {
        RegisterJob(registry, "ai_valves", "Page authored MW3 valve records or exact-name references from an edited resource. Includes repeated triggers, compound definitions and node/edge/objective uses; no runtime state is inferred. Query filters record names or valve references before paging. Parameters and reference previews are bounded; zrd_nodes/export retains complete data.",
            [DocumentParameter, MemberParameter, P("section", "string", "Records, references or eligible network binding targets.", false, "records", "references", "targets"), ..PageParameters], false, async (args, token) =>
        {
            var doc = TargetDocument(args); long revision = doc.Revision; var member = ResourceSession(doc).Member(GuidArg(args, "member"));
            var root = await ResourceTreeAsync(doc, member.Id, token);
            var page = await Task.Run(() =>
            {
                var records = MissionAiValves.Records(member.Name, root, token);
                if (Text(args, "section") == "targets") return Page(MissionAiValves.Targets(root, token), args, r => r.Name,
                    r => new { r.Id, name = MissionAiValves.Short(r.Name), r.SourceOffset, r.Spatial });
                return Text(args, "section") == "references"
                    ? Page(records.SelectMany(r => MissionAiValves.References(r, token)), args, r => r.Name, r => new JsonObject { ["record"] = r.Record.ToString(), ["operand"] = r.Operand.ToString(), ["name"] = MissionAiValves.Short(r.Name), ["nameCharacters"] = r.Name.Length, ["role"] = r.Role })
                    : Page(records, args, project: r => MissionAiValves.Describe(r, token), matches: MissionAiValves.MatchesSearch);
            }, token);
            token.ThrowIfCancellationRequested(); CheckResourceContext(doc, revision); return Result(new { revision, records = page.Data });
        });
        RegisterJob(registry, "ai_valve_edit", "Edit one authored valve occurrence through shared resource history. set uses a scalar operand UUID and invariant typed text. add_record creates a complete action/compound block in valves.zrd; add_binding uses the owning network row's value-node UUID. Record/item moves use final zero-based pair/item positions. No implicit rename propagation or cascade deletion. Unknown fields are preserved.",
            [DocumentParameter, RevisionParameter, MemberParameter,
                P("action", "string", "Semantic operation.", true, "set", "add_record", "add_binding", "delete", "duplicate", "move", "add_action", "add_term", "delete_item", "duplicate_item", "move_item", "add_option", "remove_option"),
                P("record", "string", "Valve occurrence UUID except for add_record/add_binding."), P("operand", "string", "Scalar, action-name, union-term or target row UUID."), P("value", "string", "Typed set value; unquoted new valve/term name."),
                P("kind", "string", "Action, compound or binding kind from authored valve vocabulary."), new("index", "integer", "Final item or pair index for move.", Minimum: 0, Maximum: int.MaxValue)], false, async (args, token) =>
        {
            var doc = TargetDocument(args, true); var member = GuidArg(args, "member");
            var edit = new AiValveEdit(Text(args, "action"), GuidArg(args, "record"), GuidArg(args, "operand"), args.ContainsKey("value") ? Text(args, "value") : null, args.ContainsKey("kind") ? Text(args, "kind") : null, Int(args, "index", -1));
            await ApplyResourceAsync(doc, ct => ResourceSession(doc).PrepareValveAsync(member, edit, ct), doc.Revision, token);
            return Result(DocumentState(doc));
        });
        RegisterJob(registry, "ai_valve_selection", "Discover mission valve resources or open one in pinned Properties without changing the Whole world preview. Uses the current AI snapshot and source member index, never names as identity. Optional record selects a discovered source occurrence. Source edits require the returned owning document's revision and stable member identity.",
            [PreviewParameter, AiSnapshotParameter, P("action", "string", "Selection operation.", true, "sources", "records", "references", "properties", "overlay", "highlight", "frame", "clear"), P("archive", "string", "Exact source archive path from sources."), P("memberIndex", "integer", "Source member index."), P("record", "string", "Optional source record UUID."), P("name", "string", "Exact valve name for highlighting."), P("visible", "boolean", "Overlay visibility."), ..PageParameters], false, async (args, token) =>
        {
            RequireNoDrafts(); var graph = TargetAiGraph(args);
            if (Text(args, "action") == "sources") return Page(graph.ValveSources, args, s => s.Member, s => new { s.Archive, s.MemberIndex, s.Member, snapshot = graph.Id });
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
            if (action is "records" or "references")
            {
                var page = await Task.Run(() => action == "records"
                    ? Page(MissionAiValves.Records(source.Member, source.Root, token), args, r => r.Name, r => MissionAiValves.Describe(r, token))
                    : Page(MissionAiValves.Records(source.Member, source.Root, token).SelectMany(r => MissionAiValves.References(r, token)), args, r => r.Name,
                        r => new JsonObject { ["record"] = r.Record.ToString(), ["operand"] = r.Operand.ToString(), ["name"] = MissionAiValves.Short(r.Name), ["role"] = r.Role }), token);
                _ = TargetAiGraph(args); return page;
            }
            return await OpenValveSourceAsync(graph, source, GuidArg(args, "record"), true, token);
        });
    }
    private async void AiValvesClick(object sender, RoutedEventArgs e)
    {
        await ResourceUiAsync(async () =>
        {
            if (!IsAiWorld || !await ResolvePropertiesDraftsAsync()) return;
            var graph = scene!.AiNetworks;
            var source = graph.ValveSources.FirstOrDefault(s => s.Member.Equals("valves.zrd", StringComparison.OrdinalIgnoreCase)) ?? graph.ValveSources.FirstOrDefault();
            if (source == null) throw new InvalidDataException("This mission has no authored valve resources.");
            await OpenValveSourceAsync(graph, source, Guid.Empty, false, CancellationToken.None);
        });
    }
    private void AiValveOverlayChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || synchronizingAi || !IsAiWorld) return;
        if (!ResolveInspectionDrafts()) { ApplyAiOptions(); return; }
        scene!.SetValveOptions(AiValveOverlay.IsChecked == true, scene.ValveFilter);
    }
    private async Task OpenNodeValvesAsync(string id)
    {
        if (!IsAiWorld || scene!.AiNetworks.Find(id) is not { } target || !await ResolvePropertiesDraftsAsync()) return;
        var graph = scene.AiNetworks;
        var source = graph.ValveSources.SingleOrDefault(s => s.Archive.Equals(target.Network.Archive, StringComparison.OrdinalIgnoreCase) && s.MemberIndex == target.Network.MemberIndex);
        if (source == null) throw new InvalidDataException("The node's valve source is unavailable.");
        await OpenValveSourceAsync(graph, source, MissionAiValves.ForNode(target.Network, target.Node).FirstOrDefault()?.Id ?? Guid.Empty, false, CancellationToken.None);
    }
    private async Task<StudioResult> OpenValveSourceAsync(AiNetworkSnapshot? graph, AiValveSource source, Guid recordId, bool automation, CancellationToken token, AiValveSource[]? scope = null)
    {
        void Current() { if (graph != null && (!IsAiWorld || scene!.AiNetworks.Id != graph.Id)) throw new StudioCommandException("stale_snapshot", "The mission valve sources changed."); }
        Current();
        int ordinal = await Task.Run(() =>
        {
            if (recordId == Guid.Empty) return -1;
            int index = 0;
            foreach (var record in MissionAiValves.Records(source.Member, source.Root, token)) { if (record.Id == recordId) return index; index++; }
            throw new StudioCommandException("stale_record", "Valve occurrence is unavailable.");
        }, token);
        Current();
        var doc = await ViewModel.OpenFileAsync(source.Archive, token, Current, activate: false) ?? throw new InvalidDataException("Cannot open the owning valve archive.");
        Current(); var edits = ResourceSession(doc);
        if (source.MemberIndex < 0 || source.MemberIndex >= edits.Current.Members.Count) throw new StudioCommandException("stale_record", "Valve member changed.");
        var member = edits.Current.Members[source.MemberIndex]; long revision = doc.Revision;
        if (!member.Name.Equals(source.Member, StringComparison.Ordinal) || !await Task.Run(() => member.Data.Span.SequenceEqual(source.Data.Span), token)) throw new StudioCommandException("stale_snapshot", "The owning resource differs from the preview. Refresh the mission.");
        CheckResourceContext(doc, revision); Current();
        Guid? target = null;
        if (ordinal >= 0)
        {
            target = await Task.Run(() => MissionAiValves.Records(member.Name, edits.Tree(member, token), token).Skip(ordinal).First().Id, token);
        }
        CheckResourceContext(doc, revision); Current();
        var window = await OpenResourcePropertiesAsync(doc, member.Id, target, token, automation, valves: true, valveScope: scope);
        if (window?.ResourceFields is not { ValveMode: true }) throw new StudioCommandException("context_changed", "Valve Properties was superseded.");
        return Result(new { document = doc.SessionId, doc.Revision, member = member.Id, record = target });
    }
    /// <summary>Choose the semantic valve editor from the current member structure; names are shared across games.</summary>
    private bool UsesValveProperties(DocumentModel doc, Guid memberId)
    {
        var edits = ResourceSession(doc); var snapshot = edits.Current;
        for (int i = 0; i < snapshot.Members.Count; i++)
        {
            var member = snapshot.Members[i];
            if (member.Id != memberId) continue;
            // Classify the current snapshot's already decoded tree; structure, not identity, decides the editor.
            return MissionAiValves.IsResource(member.Name) && snapshot.Document.Assets[i].Content is ZrdNode root && MissionAiValves.HasSemanticRecords(member.Name, root);
        }
        return false;
    }
    private AiValveSource[] CaptureValveScope(DocumentModel doc)
    {
        if (IsAiWorld && scene!.AiNetworks.ValveSources.Any(s => s.Archive.Equals(doc.Path, StringComparison.OrdinalIgnoreCase))) return scene.AiNetworks.ValveSources.ToArray();
        var snapshot = ResourceSession(doc).Current;
        return snapshot.Members.Select((m, i) => (Member: m, Index: i)).Where(p => MissionAiValves.IsResource(p.Member.Name) && snapshot.Document.Assets[p.Index].Content is ZrdNode)
            .Select(p => new AiValveSource(doc.Path, p.Index, p.Member.Name, ResourceSession(doc).Tree(p.Member), p.Member.Data)).ToArray();
    }
    private async Task<IReadOnlyList<ValveReferenceTarget>> FindValveReferencesAsync(DocumentModel owner, AiValveSource[] scope, string name, int offset)
    {
        var paths = scope.Select(s => s.Archive).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var current = ViewModel.Documents.Where(d => d.ResourceEdits != null && paths.Contains(d.Path)).Select(d => (d.Path, Edits: d.ResourceEdits!, Snapshot: d.ResourceEdits!.Current)).ToArray();
        var token = owner.Lifetime.Token;
        return await Task.Run(() =>
        {
            IEnumerable<AiValveSource> Sources()
            {
                foreach (var path in paths)
                {
                    token.ThrowIfCancellationRequested(); var open = current.FirstOrDefault(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (open.Snapshot == null) { foreach (var source in scope.Where(s => s.Archive.Equals(path, StringComparison.OrdinalIgnoreCase))) yield return source; continue; }
                    for (int i = 0; i < open.Snapshot.Members.Count; i++)
                    { var member = open.Snapshot.Members[i]; if (MissionAiValves.IsResource(member.Name) && open.Snapshot.Document.Assets[i].Content is ZrdNode) yield return new(path, i, member.Name, open.Edits.Tree(member, token), member.Data); }
                }
            }
            return (IReadOnlyList<ValveReferenceTarget>)Sources().SelectMany(source => MissionAiValves.Records(source.Member, source.Root, token)
                .SelectMany(record => MissionAiValves.References(record, token).Where(r => r.Name == name).Select(r => new ValveReferenceTarget(source, record.Id, r.Name, r.Role)))).Skip(offset).Take(33).ToArray();
        }, token);
    }
    private void HighlightValve(AiValveSource[] scope, string name, bool frameView)
    {
        if (!IsAiWorld || !scope.Select(s => s.Archive).ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(scene!.AiNetworks.ValveSources.Select(s => s.Archive)))
            throw new InvalidDataException("Open this valve's mission in Whole world before highlighting its associations.");
        SetAiOptions(true, aiThroughGeometry, null); scene.SetValveOptions(true, name); ApplyAiOptions();
        if (frameView && !scene.FrameValveAssociations()) throw new InvalidDataException("This valve has no resolved spatial association in the current preview.");
    }
}
