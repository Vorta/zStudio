using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// MW3 valves in a source map (design/mw3-valves-ui.md): the mission's valves by name, their card's edits and the tree's
/// commands, shared by the GUI and MCP <c>source_ai_valve</c>.
/// </summary>
public partial class MainWindow
{
    private static string ValveDefinitionSource(SourceSceneSnapshot source) => source.Resources.Values.FirstOrDefault(r =>
        Path.GetFileName(r.Path).Equals("valves.zrd", StringComparison.OrdinalIgnoreCase))?.Path ??
        (source.SourceMission ?? $"data/{source.Mission}/zrdr") + "/valves.zrd";
    private SourceSceneSnapshot ValveSource(DocumentModel document)
    {
        if (document != shownDocument || document.Document is not SourceSceneSnapshot { Game: GameVariant.MechWarrior3 } source || document.SourceWorld == null)
            throw new StudioCommandException("unsupported", "Open an MW3 source map and select its mission first.");
        return source;
    }
    /// <summary>The mission's valves by name and what its reactions can name: its vehicles and targets, and its animations.</summary>
    private static (Mw3ValveMap Map, Mw3ValveCatalog Catalog) BuildValveMap(SourceSceneSnapshot snapshot, MissionSceneContext? mission, CancellationToken token = default)
    {
        var sources = snapshot.Resources.Values.Where(r => r.Root != null && MissionAiValves.IsResource(Path.GetFileName(r.Path))).Select(r => new Mw3ValveSource(r.Path, r.Root!)).ToArray();
        HashSet<string> vehicles = new(StringComparer.Ordinal);
        foreach (var actor in mission?.Actors ?? []) vehicles.Add(actor.Name);
        // A target is named by targets.zrd; a vehicle the shown difficulty leaves out is still one of the mission's.
        foreach (var source in sources.Where(s => Path.GetFileName(s.Path) is var name && (name.Equals("targets.zrd", StringComparison.OrdinalIgnoreCase) || name.Equals("aiv.zrd", StringComparison.OrdinalIgnoreCase))))
        {
            var fields = MissionAiValves.Fields(source.Root).Children;
            for (int i = 0; i + 1 < fields.Count; i += 2) if (fields[i].Kind == ZrdKind.String && fields[i + 1].Kind == ZrdKind.Array) vehicles.Add(fields[i].Text);
        }
        HashSet<string>? animations = snapshot.Animations is { } package ? new(package.Entries.Where(e => e.Index > 0).Select(e => e.Name), StringComparer.Ordinal) : null;
        Mw3ValveCatalog catalog = new(vehicles, null, animations);
        string? folder = snapshot.SourceMission is { } path ? Path.GetFileName(path) : null;
        // The animations that set a valve through callbacks 42–49, from the entries' events and the roots they bind to.
        var callbacks = snapshot.Animations is { } loaded && mission != null ? Mw3ValveSimulation.CallbackValves(loaded, mission, token) : null;
        return (Mw3ValveMap.Build(sources, folder, catalog, token, callbacks), catalog);
    }
    /// <summary>The shown map's valves (the World tree's, built once per shown mission).</summary>
    private (Mw3ValveMap Map, Mw3ValveCatalog? Catalog) ValveMapOf(DocumentModel document)
    {
        var source = ValveSource(document);
        if (sceneTreeDocument == document && sceneTree?.Sections is { ValveMap: { } map } sections) return (map, sections.ValveCatalog);
        var built = BuildValveMap(source, scene?.Mission);
        return (built.Map, built.Catalog);
    }
    private static string? ValveMissionFolder(SourceSceneSnapshot source) => source.SourceMission is { } path ? Path.GetFileName(path) : null;

    /// <summary>Applies a valve's changes to valves.zrd as one undoable change (the card's Apply and MCP set alike).</summary>
    private async Task<DocumentModel> EditValveAsync(DocumentModel document, string name, SourceValveEdits.ValveChanges changes, CancellationToken token, bool committingSection = false)
    {
        RequireNoDrafts(document, committing: true, committingSection: committingSection);
        var source = ValveSource(document);
        string path = ValveDefinitionSource(source);
        var resource = source.Resources.Values.SingleOrDefault(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (resource is { Root: null }) throw new StudioCommandException("invalid_argument", "valves.zrd could not be read; the map's notes say why. Correct it in a text editor first.");
        EndValveTest();
        try
        {
            return await PrepareSourceWorldEditAsync(document, "Editing valve", (workspace, ct) =>
            {
                byte[]? bytes = workspace.Read(path, ct);
                if (bytes == null && resource != null) throw new StudioCommandException("stale_record", "valves.zrd is no longer in the project.");
                if (bytes != null && resource == null) throw new SourceFileChangedException("valves.zrd appeared; reload the mission before editing valves.", [path]);
                SourceValveEdits.Edit plan;
                try { plan = SourceValveEdits.PlanValve(bytes, name, changes, ct); }
                catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
                catch (InvalidOperationException) { throw new StudioCommandException("unchanged", "Nothing changed."); }
                return workspace.Apply(plan.Label, [(path, (byte[]?)plan.Content)], ct);
            }, token, committingSection: committingSection);
        }
        catch (StudioCommandException ex) when (ex.Code == "unchanged") { return document; }
    }
    /// <summary>Renames a valve in every file of the mission that names it: one undoable change.</summary>
    private async Task<DocumentModel> RenameValveAsync(DocumentModel document, string name, string renamed, CancellationToken token)
    {
        RequireNoDrafts(document);
        var source = ValveSource(document);
        var (map, _) = ValveMapOf(document);
        if (map.Find(name) is not { } renaming) throw new StudioCommandException("stale_record", "The mission has no valve of that name. Read its valves again.");
        if (renamed == name) return document;
        if (ValveRenameRefusal(renaming) is { } refusal) throw new StudioCommandException("unsupported", refusal);
        if (map.Find(renamed) != null) throw new StudioCommandException("invalid_argument", $"The mission already has a valve named {JsonData.ShownText(renamed, 64)}.");
        string? mission = ValveMissionFolder(source);
        // Every file that names it must be a text source: a rename that skipped a binary one would split the valve in two.
        if (source.Resources.Values.FirstOrDefault(r => r is { Root: not null, Syntax: null } && MissionAiValves.IsResource(Path.GetFileName(r.Path))
            && MissionAiValves.Records(Path.GetFileName(r.Path), r.Root, token).Any(record => MissionAiValves.References(record, token).Any(reference => reference.Name == name))) is { } binary)
            throw new StudioCommandException("unsupported", $"{Path.GetFileName(binary.Path)} names this valve and is stored as binary zReader data, so the valve can be renamed only in text sources.");
        string[] paths = [.. source.Resources.Values.Where(r => r.Syntax != null && MissionAiValves.IsResource(Path.GetFileName(r.Path))).Select(r => r.Path)];
        EndValveTest();
        rememberedValve = renamed;
        try
        {
        return await PrepareSourceWorldEditAsync(document, "Renaming valve", (workspace, ct) =>
        {
            List<(string, byte[]?)> files = [];
            foreach (string path in paths)
            {
                if (workspace.Read(path, ct) is not { } bytes) continue;
                try { if (SourceValveEdits.PlanRename(Path.GetFileName(path), bytes, name, renamed, mission, ct) is { } edit) files.Add((path, edit.Content)); }
                catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
            }
            if (files.Count == 0) throw new StudioCommandException("stale_record", "No text source of the mission names this valve.");
            return workspace.Apply($"Rename valve {MissionAiValves.Short(name, 48)}", files, ct);
        }, token);
        }
        catch { rememberedValve = null; throw; }
    }
    /// <summary>Deletes a valve's valves.zrd records: one undoable change. Other files keep naming it.</summary>
    private async Task<DocumentModel> RemoveValveAsync(DocumentModel document, string name, CancellationToken token)
    {
        RequireNoDrafts(document);
        var source = ValveSource(document);
        string path = ValveDefinitionSource(source);
        if (ValveMapOf(document).Map.Find(name) is not { Defined: true }) throw new StudioCommandException("stale_record", "valves.zrd has no record of that valve. Read the mission's valves again.");
        EndValveTest();
        return await PrepareSourceWorldEditAsync(document, "Deleting valve", (workspace, ct) =>
        {
            byte[] bytes = workspace.Read(path, ct) ?? throw new StudioCommandException("stale_record", "valves.zrd is no longer in the project.");
            try { var plan = SourceValveEdits.PlanRemove(bytes, name, ct); return workspace.Apply(plan.Label, [(path, (byte[]?)plan.Content)], ct); }
            catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
        }, token);
    }
    /// <summary>A new valve: a valves.zrd record that starts at 0 (a name other files already mention gets its first record).</summary>
    private async Task<DocumentModel> NewValveAsync(DocumentModel document, string name, CancellationToken token)
    {
        if (ValveNameProblem(document, name, creating: true) is { } problem) throw new StudioCommandException("invalid_argument", problem);
        rememberedValve = name;
        try { return await EditValveAsync(document, name, new(Initial: "0"), token); }
        catch { rememberedValve = null; throw; }
    }
    /// <summary>
    /// An animation sets the valve named as the scene node it binds (callbacks 42–49): renaming the valve in the mission's
    /// files would leave the animation setting the old name, so the rename is refused.
    /// </summary>
    private static string? ValveRenameRefusal(Mw3ValveInfo valve) => valve.SetBy.FirstOrDefault(l => l.Role == "animation") is { } animation
        ? $"The animation {JsonData.ShownText(animation.Owner, 64)} sets this valve through the scene node named {JsonData.ShownText(valve.Name, 64)}, so the valve keeps that node's name. Renaming it would disconnect the animation."
        : null;
    private string? ValveNameProblem(DocumentModel document, string name, bool creating, string? current = null)
    {
        if (name.Length == 0) return "Enter a name.";
        if (name.Length > 256) return "At most 256 characters.";
        if (name != name.Trim()) return "No spaces around the name.";
        if (name.Any(char.IsControl) || name.Contains('"')) return "No quotes or control characters.";
        if (name == current) return null;
        var existing = ValveMapOf(document).Map.Find(name);
        if (creating ? existing is { Defined: true } : existing != null) return "The mission already has a valve of that name.";
        return null;
    }
    /// <summary>A valve just added or renamed: the rebuilt map's tree selects it (<see cref="RestoreTreeLayout"/>).</summary>
    private string? rememberedValve;
    /// <summary>Selects a valve's row by name (a link on a card, a new or renamed valve); false when the mission has none.</summary>
    private bool SelectValve(string name)
    {
        if (sceneTree?.FindSection("valve:" + name) is not { } row) { ViewModel.Status = $"This mission has no valve named {JsonData.ShownText(name, 64)}."; return false; }
        if (sceneTree.Selected == row) return true;
        if (!ResolveSelectionDrafts(sceneTreeDocument)) return false;
        try { SelectSectionRow(row); return true; }
        catch (StudioCommandException ex) { Report(ex); return false; }
    }
    /// <summary>A Set by or Used by link: the vehicle or target, the route node (its network shown), or the other valve.</summary>
    private void FollowValveLink(Mw3ValveLink link)
    {
        if (link.Valve != null) { SelectValve(link.Valve); return; }
        if (link.Entry is int entry)
        {
            // The animation's definition row (its card has Open in Animation mode).
            var definition = sceneTree?.Sections?.Definitions.Definitions.FirstOrDefault(d => d.Entries.Contains(entry));
            var row = definition == null ? null : sceneTree!.FindSection($"definition:{definition.File}#{definition.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture)}@0");
            if (row == null) { ViewModel.Status = $"The World tree does not list the animation {JsonData.ShownText(link.Owner, 64)}."; return; }
            if (!ResolveSelectionDrafts(sceneTreeDocument)) return;
            try { SelectSectionRow(row); }
            catch (StudioCommandException ex) { Report(ex); }
            return;
        }
        if (scene is not { } viewport || !ResolveSelectionDrafts(sceneTreeDocument)) return;
        try
        {
            if (link.Actor != null)
            {
                // A placed vehicle, or a target's scene node (targets.zrd names one, such as a dish).
                int[] named = viewport.Mission is { } mission ? [.. ValveNamedNodes(mission, new HashSet<string>(StringComparer.Ordinal) { link.Actor })] : [];
                if (named.Length != 1 || sceneTree?.Reveal(named[0]) is not { Selectable: true } row)
                { ViewModel.Status = named.Length > 1 ? $"Several placed vehicles are named {JsonData.ShownText(link.Actor, 64)}; select one in the World tree." : $"{JsonData.ShownText(link.Actor, 64)} is not on the map at the shown difficulty."; return; }
                SelectSceneTreeRow(row); RunCameraNavigation("frameSelected");
            }
            else if (link is { Network: { } network, Node: int index } && viewport.AiNetworks.Networks.FirstOrDefault(n => n.Id.Equals(network, StringComparison.OrdinalIgnoreCase)) is { } found
                && found.Nodes.FirstOrDefault(n => n.Index == index) is { } node)
            {
                ResetSectionStateFor(shownDocument); shownNetworks.Add(found.Id); ReconcileMasters(); ApplySectionPresentation();
                ClearSectionSelection();
                if (viewport.SelectAiNode(node.Id)) RunCameraNavigation("frameSelected");
            }
        }
        catch (StudioCommandException ex) { Report(ex); }
    }

    // ---------------------------------------------------------------- the tree's commands

    private void SceneNewValveClick(object sender, RoutedEventArgs e)
    {
        if (sceneContextDocument is not { IsDisposed: false } doc || doc != shownDocument) return;
        string? name = PromptName("New valve", "A valve is a named switch of this mission. It starts at 0; add what sets it and its reactions on its card.", "", "Add valve", n => ValveNameProblem(doc, n, creating: true));
        if (name != null) _ = ReportUi(() => NewValveAsync(LiveDocument(doc), name, CancellationToken.None));
    }
    private void SceneRenameValveClick(object sender, RoutedEventArgs e) { if (contextScene?.Section is ValveEntry valve) ValveCardCommand("rename", valve.Valve.Name); }
    private void SceneDeleteValveClick(object sender, RoutedEventArgs e) { if (contextScene?.Section is ValveEntry valve) ValveCardCommand("delete", valve.Valve.Name); }
    private void SceneCopyValveNameClick(object sender, RoutedEventArgs e)
    {
        if (contextScene?.Section is not ValveEntry valve) return;
        try { Clipboard.SetText(valve.Valve.Name); ViewModel.Status = "Copied the valve's name."; }
        catch (System.Runtime.InteropServices.COMException) { ViewModel.Status = "The clipboard is busy; try again."; }
    }
    private void SceneValveTestClick(object sender, RoutedEventArgs e) => ToggleValveTest();
    private void OutlinerValveTestClick(object sender, RoutedEventArgs e) { e.Handled = true; ToggleValveTest(); }
    /// <summary>Rename… and Delete from a valve's card or row.</summary>
    private void ValveCardCommand(string command, string name)
    {
        if (shownDocument is not { IsDisposed: false } doc || doc.SourceWorld == null || !ResolveSelectionDrafts(doc)) return;
        if (command == "rename")
        {
            if (sceneTree?.Sections?.ValveMap?.Find(name) is { } current && ValveRenameRefusal(current) is { } refusal)
            { MessageBox.Show(this, refusal, "Rename valve", MessageBoxButton.OK, MessageBoxImage.Information); return; }
            string? renamed = PromptName("Rename valve", $"Rename {JsonData.ShownText(name, 64)} in every file of the mission that names it: its records, vehicle triggers, routes, other valves and this mission's objectives.",
                name, "Rename", n => ValveNameProblem(doc, n, creating: false, current: name));
            if (renamed != null && renamed != name) _ = ReportUi(() => RenameValveAsync(LiveDocument(doc), name, renamed, CancellationToken.None));
        }
        else if (command == "delete")
        {
            var info = sceneTree?.Sections?.ValveMap?.Find(name);
            int others = (info?.SetBy.Count ?? 0) + (info?.UsedBy.Count ?? 0);
            var answer = MessageBox.Show(this, $"Delete the valves.zrd records of {JsonData.ShownText(name, 64)}?\n\nIts reactions and starting value go."
                + (others > 0 ? $" {others} other place{(others == 1 ? " still names" : "s still name")} it; the game then makes it at 0 with no reactions." : ""),
                "Delete valve", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            if (answer == MessageBoxResult.OK) _ = ReportUi(() => RemoveValveAsync(LiveDocument(doc), name, CancellationToken.None));
        }
    }
    /// <summary>What the valve card shows beside the valve: the names for its pickers, its records as stored, and why it cannot edit.</summary>
    private SourceSectionCard.ValveCardContext ValveCardContextOf(DocumentModel doc, Mw3ValveInfo valve)
    {
        var sections = sceneTree?.Sections; var snapshot = doc.Document as SourceSceneSnapshot;
        var map = sections?.ValveMap ?? Mw3ValveMap.Empty;
        List<(string, string?)> source = [];
        foreach (var record in valve.Records.Take(16))
        {
            var resource = snapshot?.Resources.Values.FirstOrDefault(r => r.Path.Equals(record.Source, StringComparison.OrdinalIgnoreCase));
            var body = resource?.Root != null ? MissionAiValves.Fields(resource.Root).Children : [];
            int at = -1;
            for (int i = 0; i + 1 < body.Count; i += 2) if (body[i].Id == record.Record) { at = i; break; }
            if (at < 0) continue;
            string text = resource!.Syntax is { } syntax && syntax.TryGetSpan(body[at].Id, out var key) && syntax.TryGetSpan(body[at + 1].Id, out var value) && value.End - key.Start <= 4096
                ? syntax.Text.Substring(key.Start, value.End - key.Start) : JsonData.ShownText(ZrdText.Write(body[at + 1], default, 4096), 4096);
            source.Add((text, record.Problem));
        }
        string path = snapshot != null ? ValveDefinitionSource(snapshot) : "";
        var definitions = snapshot?.Resources.Values.FirstOrDefault(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        string? readOnly = definitions is { Root: null } ? "valves.zrd could not be read, so valves are not edited here; the map's notes say why."
            : definitions is { Syntax: null } ? "valves.zrd is stored as binary zReader data, so valves can be edited only in a text source." : null;
        // The mission's sounds are not known without its sound banks: the picker offers the sounds its valves already play.
        var catalog = sections?.ValveCatalog is { } known ? known with { Sounds = null } : null;
        return new(map, catalog, source, readOnly);
    }

    // ---------------------------------------------------------------- MCP

    private static object DescribeValveRow(Mw3ValveInfo valve) => new
    {
        kind = "valve", name = JsonData.ShownText(valve.Name, 256), nameCharacters = valve.Name.Length, defined = valve.Defined, computed = valve.Condition != null,
        summary = JsonData.ShownText(valve.Summary, 256), setBy = valve.SetBy.Count, reactions = valve.Reactions.Count, usedBy = valve.UsedBy.Count, problems = valve.Problems.Count,
    };
    private static JsonObject DescribeValveLink(Mw3ValveLink link) => new()
    {
        ["role"] = link.Role, ["source"] = link.Source, ["record"] = link.Record.ToString(), ["owner"] = JsonData.ShownText(link.Owner, 256), ["text"] = JsonData.ShownText(link.Text, 256), ["value"] = link.Value,
        ["network"] = link.Network, ["node"] = link.Node, ["vehicle"] = link.Actor is { } actor ? JsonData.ShownText(actor, 256) : null, ["valve"] = link.Valve is { } other ? JsonData.ShownText(other, 256) : null,
        ["animationEntry"] = link.Entry,
    };
    private static JsonObject DescribeValveSpec(Mw3ValveReactionSpec spec) => new()
    {
        ["kind"] = spec.Kind, ["target"] = spec.Target is { } target ? JsonData.ShownText(target, 256) : null, ["operand"] = spec.Operand, ["when"] = spec.When,
        ["x"] = spec.X, ["y"] = spec.Y, ["z"] = spec.Z, ["yaw"] = spec.Yaw,
    };
    private static object DescribeValve(Mw3ValveInfo valve) => new
    {
        name = JsonData.ShownText(valve.Name, 256), nameCharacters = valve.Name.Length, defined = valve.Defined, initial = valve.Initial, condition = valve.Condition,
        inputs = valve.Inputs.Take(64).Select(i => JsonData.ShownText(i, 256)).ToArray(), inputCount = valve.Inputs.Count, summary = JsonData.ShownText(valve.Summary, 256),
        problems = valve.Problems.Select(p => JsonData.ShownText(p, 400)).ToArray(),
        records = valve.Records.Take(64).Select(r => new { source = r.Source, record = r.Record, occurrence = r.Occurrence, problem = r.Problem }).ToArray(), recordCount = valve.Records.Count,
        reactions = valve.Reactions.Take(64).Select(r => new JsonObject { ["occurrence"] = r.Occurrence, ["record"] = r.Record.ToString(), ["text"] = JsonData.ShownText(r.Text, 256), ["problem"] = r.Problem, ["fields"] = DescribeValveSpec(r.Spec) }).ToArray(),
        reactionCount = valve.Reactions.Count,
        setBy = valve.SetBy.Take(64).Select(DescribeValveLink).ToArray(), setByCount = valve.SetBy.Count,
        usedBy = valve.UsedBy.Take(64).Select(DescribeValveLink).ToArray(), usedByCount = valve.UsedBy.Count,
    };
    private static Mw3ValveReactionSpec ValveSpecArgument(JsonObject fields) => new(Text(fields, "kind"), fields["target"]?.GetValue<string>(), Scalar(fields["operand"]), Scalar(fields["when"]),
        Scalar(fields["x"]), Scalar(fields["y"]), Scalar(fields["z"]), Scalar(fields["yaw"]));
    private static string? Scalar(JsonNode? node) => node is JsonValue value ? value.TryGetValue<string>(out string? text) ? text : value.ToJsonString() : null;
    private static SourceValveEdits.ValveChanges ValveChangesArgument(JsonObject args)
    {
        if (args["changes"] is not JsonObject changes) throw new StudioCommandException("invalid_argument", "Give the changes: initial, condition, inputs or reactions.");
        var reactions = (changes["reactions"] as JsonArray ?? []).OfType<JsonObject>().Select(r => new SourceValveEdits.ReactionChange(r["occurrence"]?.GetValue<int>(), r["storedKind"]?.GetValue<string>(),
            r["remove"]?.GetValue<bool>() == true ? null : r["fields"] is JsonObject fields ? ValveSpecArgument(fields) : throw new StudioCommandException("invalid_argument", "A reaction needs fields, or remove: true."))).ToArray();
        if (reactions.Any(r => r.Occurrence != null && r.Kind == null)) throw new StudioCommandException("invalid_argument", "An existing reaction is named by occurrence and storedKind.");
        return new(Scalar(changes["initial"]), changes["condition"]?.GetValue<string>(), changes["inputs"] is JsonArray inputs ? [.. inputs.Select(i => i?.GetValue<string>() ?? "")] : null, reactions);
    }

    private void RegisterValveAuthoringCommands(StudioCommands registry)
    {
        StudioParameter reactionFields = new("fields", "object", "The reaction as the card shows it.", Properties:
        [
            P("kind", "string", "The stored tag.", true, "sound", "sound_once", "animation", "animation_once", "shutdown", "destroy", "teleport", "setteam", "attack_strategy", "scan_aim", "scan_hit", "scan_time", "canleave",
                "delayupdate", "update", "missionabort", "missionreset", "missioncomplete"),
            P("target", "string", "The sound, animation, vehicle or valve it names (none for the mission reactions)."),
            P("operand", "string", "shutdown: 0 wake, 1 power down; scans and canleave: 1 on, 0 off; setteam: the team; attack_strategy: the strategy; delayupdate: seconds."),
            P("when", "string", "The valve value that triggers it, a whole number (not for update and delayupdate, which run on every change)."),
            P("x", "string", "teleport: X."), P("y", "string", "teleport: Y."), P("z", "string", "teleport: Z."), P("yaw", "string", "teleport: heading."),
        ]);
        RegisterJob(registry, "source_ai_valve", "Read or edit the valves of the shown MW3 source mission. A valve is a named whole-number switch; the World tree's Valves section and its card show one per exact name, gathered from valves.zrd (starting value, computed condition, reactions), aiv.zrd vehicle triggers, targets.zrd health thresholds, the networks' route gates, assignments and deploy points, and this mission's objectives. valves pages them (name, summary, counts, problems); valve reads one (initial: the stored starting value, which a computed valve keeps but ignores; condition and inputs, reactions with their fields, setBy and usedBy links, records; setBy also lists the animations that set it through callbacks 42–49 on the scene node of its name, role animation with animationEntry). set applies the card's changes as one undoable change of valves.zrd that keeps comments, layout and unknown tags: initial; condition (any_nonzero, any_zero, all_nonzero, all_zero, or empty for a plain switch) with inputs; reactions, each an existing one by occurrence and storedKind (fields to change it, remove: true to remove it) or a new one (fields only; written as its own record after the valve's last). new adds a valve starting at 0; rename renames it in every file of the mission that names it (one change; refused for a valve an animation sets, which keeps its scene node's name); remove deletes its valves.zrd records. draft stages the same changes on the selected valve's card (Apply, Revert and resolve_drafts as for any card). Record level: sources, records, read, create, update, delete and attach edit one stored record or node attribute as zReader text. Test state is ai_valve_preview's and is never saved.",
            [DocumentParameter, RevisionParameter with { Required = false }, P("action", "string", "Operation.", true, "valves", "valve", "set", "new", "rename", "remove", "draft", "sources", "records", "read", "create", "update", "delete", "attach"),
             P("name", "string", "valve, set, new, rename, remove, draft: the exact case-sensitive valve name. create, update: the record's name."), P("newName", "string", "rename: the new name."),
             new("changes", "object", "set, draft: what changes; omitted parts stay.", Properties:
             [
                 P("initial", "string", "The starting value, a whole number."), P("condition", "string", "Computed kind, or empty for a plain switch.", false, "", "any_nonzero", "any_zero", "all_nonzero", "all_zero"),
                 new("inputs", "array", "A computed valve's input valves, in order.", Items: new("", "string", "A valve name."), MaxItems: 256),
                 new("reactions", "array", "Reactions to change, remove or add.", Items: new("", "object", "One reaction.", Properties:
                 [
                     new("occurrence", "integer", "An existing reaction's record occurrence (from valve); omit for a new one.", Minimum: 0, Maximum: int.MaxValue),
                     P("storedKind", "string", "An existing reaction's stored tag (from valve)."), P("remove", "boolean", "Remove this existing reaction."), reactionFields,
                 ]), MaxItems: 64),
             ]),
             P("source", "string", "Record level: exact relative source path from sources."), P("record", "string", "Record level: occurrence ID from records."),
             P("nodeIndex", "integer", "attach: uniquely resolved spatial node number."), P("attribute", "string", "attach: node attribute.", false, "valve", "valveunion", "valve_assign"),
             P("parameters", "string", "Record level: ordered zReader parameter array, at most 65,536 characters."),
             P("draft", "string", "draft: the current card draft token when a draft is already open."), ..PageParameters], false, async (args, token) =>
        {
            var document = TargetDocument(args); var source = ValveSource(document); string action = Text(args, "action");
            if (action is "valves" or "valve" or "set" or "new" or "rename" or "remove" or "draft")
            {
                var (map, _) = ValveMapOf(document);
                if (action == "valves") return Page(map.Valves, args, v => v.Name, v => DescribeValveRow(v), matches: (v, query) => v.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || v.Summary.Contains(query, StringComparison.OrdinalIgnoreCase) || v.Mentions.Any(m => m.Contains(query, StringComparison.OrdinalIgnoreCase)));
                string name = Text(args, "name");
                if (action == "valve")
                    return Result(new { document = DocumentState(document), source = ValveDefinitionSource(source), valve = DescribeValve(map.Find(name) ?? throw new StudioCommandException("stale_record", "The mission has no valve of that name.")), truncated = map.Truncated });
                if (args["revision"]?.GetValue<long>() != document.Revision) throw new StudioCommandException("revision_conflict", "Read the current document revision.");
                if (action == "draft")
                {
                    if (sectionCard is not { ShownValve: { } shown } card || shown.Name != name || card.Document != document) throw new StudioCommandException("not_ready", "Select this valve's World tree row first.");
                    if (card.HasDraft) card.RequireDraft(Text(args, "draft"));
                    card.SetValveDraft(ValveChangesArgument(args));
                    return Result(card.Describe());
                }
                var next = action switch
                {
                    "set" => await EditValveAsync(document, map.Find(name)?.Name ?? throw new StudioCommandException("stale_record", "The mission has no valve of that name; new adds one."), ValveChangesArgument(args), token),
                    "new" => await NewValveAsync(document, name, token),
                    "rename" => ValveNameProblem(document, Text(args, "newName"), creating: false, current: name) is { } problem ? throw new StudioCommandException("invalid_argument", problem)
                        : await RenameValveAsync(document, name, Text(args, "newName"), token),
                    _ => await RemoveValveAsync(document, name, token),
                };
                return Result(new { document = DocumentState(next), name = action == "rename" ? Text(args, "newName") : name });
            }
            if (action == "sources")
            {
                string proposed = ValveDefinitionSource(source);
                var sources = source.Resources.Values.Where(r => MissionAiValves.IsResource(Path.GetFileName(r.Path)))
                    .Select(r => (Path: r.Path, Exists: true));
                if (!source.Resources.Values.Any(r => r.Path.Equals(proposed, StringComparison.OrdinalIgnoreCase))) sources = sources.Append((proposed, false));
                return Page(sources, args, r => r.Path, r => new { source = r.Path, member = Path.GetFileName(r.Path), exists = r.Exists });
            }
            string path = Text(args, "source");
            var resource = source.Resources.Values.SingleOrDefault(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (resource == null && !path.Equals(ValveDefinitionSource(source), StringComparison.OrdinalIgnoreCase)) throw new StudioCommandException("stale_record", "Read the current valve sources.");
            if (resource is { Root: null }) throw new StudioCommandException("invalid_argument", "The source resource could not be read.");
            var records = resource?.Root == null ? [] : MissionAiValves.Records(Path.GetFileName(path), resource.Root, token).ToArray();
            if (action == "records") return Page(records, args, r => r.Name, r => MissionAiValves.Describe(r, token));
            Guid id = args.ContainsKey("record") ? GuidArg(args, "record") : Guid.Empty;
            var record = records.FirstOrDefault(r => r.Id == id);
            if (action == "read")
            {
                if (record == null) throw new StudioCommandException("stale_record", "Read the current valve occurrence.");
                return Result(new { document = DocumentState(document), source = path, record = MissionAiValves.Describe(record, token),
                    parameters = ZrdText.Write(record.Value, token, 65_536) });
            }
            if (args["revision"]?.GetValue<long>() != document.Revision) throw new StudioCommandException("revision_conflict", "Read the current document revision.");
            if (action == "attach")
            {
                var attached = await AttachValveAsync(document, path, Int(args, "nodeIndex", -1), Text(args, "attribute"), Text(args, "parameters"), token);
                return Result(new { document = DocumentState(attached), source = path });
            }
            if (action is not ("create" or "update" or "delete")) throw new StudioCommandException("invalid_argument", "Unknown valve operation.");
            var edited = await EditValveRecordAsync(document, path, id, action, args.ContainsKey("name") ? Text(args, "name") : null,
                args.ContainsKey("parameters") ? Text(args, "parameters") : null, token);
            return Result(new { document = DocumentState(edited), source = path });
        });
        RegisterJob(registry, "ai_valve_preview", "The valve test of the shown MW3 source mission (the Valves row's Test, the banner over the view and the valve card's Test section): the mission's valves run in memory from 0 s with their conditions, reactions and timers, animation callbacks 42–49, vehicle state and route gates. start (or prepare) begins it paused and returns its simulation ID, which the other actions need; state reads it (time, playing, the first 64 values, timers, vehicles and the latest activity). set gives a valve a value (as a trigger, threshold or route would), update fires its reactions without changing it, traverse drives a vehicle through a node's outgoing link slot (refused while its valves close it; its assignments apply). play, pause, step (one tick), advance (to an absolute time, at most one hour), next (to the first tick on which the next pending timer runs), reset (back to 0 s, a new ID) and stop. trace, timers, values and vehicles page the whole state. Nothing is saved; a source edit, another mission or Animation mode ends it. Gameplay is manual: no AI drives, fights or triggers on its own.",
            [PreviewParameter, P("action", "string", "Operation.", true, "state", "start", "prepare", "play", "pause", "step", "advance", "next", "reset", "stop", "set", "update", "traverse", "trace", "timers", "values", "actors"),
             P("simulation", "string", "Current simulation ID; required except start, prepare and state."), P("name", "string", "set, update: exact valve name."),
             P("value", "integer", "set: the value."), P("seconds", "number", "advance: absolute test time, the current time through 3600 seconds."),
             P("node", "string", "traverse: snapshot-scoped AI node ID."), P("slot", "integer", "traverse: physical outgoing link slot."), ..PageParameters], true, async (args, token) =>
        {
            _ = TargetAiGraph(args, false); string action = Text(args, "action");
            if (action == "start") action = "prepare";
            if (action is not ("prepare" or "state") && (valveSimulation == null || Text(args, "simulation") != valveSimulation.Id.ToString()))
                throw new StudioCommandException("stale_record", "Read the current valve simulation ID.");
            if (action == "trace") return Page(CurrentValveSimulation().Runtime.Trace, args, t => t.Valve, t => t);
            if (action == "values") return Page(CurrentValveSimulation().Runtime.Values(), args, v => v.Name,
                v => new { name = MissionAiValves.Short(v.Name), nameCharacters = v.Name.Length, v.Value, v.Condition });
            if (action == "actors") return Page(CurrentValveSimulation().Actors.Values, args, a => a.Name,
                a => new { name = MissionAiValves.Short(a.Name), nameCharacters = a.Name.Length, a.PoweredDown, a.Destroyed, a.Team, a.Strategy,
                    position = a.Position is { } p ? JsonData.Vector(p) : null, a.Yaw, a.Switches });
            if (action == "timers") return Page(CurrentValveSimulation().Runtime.Timers, args, t => t.Target,
                t => new { target = MissionAiValves.Short(t.Target), targetCharacters = t.Target.Length, t.Value, t.Due });
            await ValvePreviewActionAsync(action, Text(args, "name"), Int(args, "value"), args["seconds"]?.GetValue<double>(),
                Text(args, "node"), Int(args, "slot", -1), token);
            return Result(ValvePreviewState());
        });
    }
    /// <summary>Record level (MCP create, update, delete): one stored record as zReader text.</summary>
    private async Task<DocumentModel> EditValveRecordAsync(DocumentModel document, string path, Guid record, string action, string? name, string? parameters, CancellationToken token)
    {
        RequireNoDrafts(document);
        var source = ValveSource(document);
        var resource = source.Resources.Values.SingleOrDefault(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        bool creatingResource = resource == null && action == "create" && path.Equals(ValveDefinitionSource(source), StringComparison.OrdinalIgnoreCase);
        if (resource == null && !creatingResource) throw new StudioCommandException("stale_record", "Read the current mission's valve sources.");
        if (resource is { Root: null }) throw new StudioCommandException("invalid_argument", "The source resource could not be read.");
        var records = resource?.Root == null ? [] : MissionAiValves.Records(Path.GetFileName(path), resource.Root, token).ToArray();
        int index = Array.FindIndex(records, r => r.Id == record);
        if (action != "create" && index < 0) throw new StudioCommandException("stale_record", "Read the current valve occurrence.");
        EndValveTest();
        return await PrepareSourceWorldEditAsync(document, "Editing MW3 valve", (workspace, ct) =>
        {
            var stored = workspace.Read(path, ct);
            if (creatingResource && stored != null) throw new SourceFileChangedException("The valve source appeared; reload the mission before creating a definition.", [path]);
            var bytes = stored ?? (creatingResource ? System.Text.Encoding.Latin1.GetBytes("()\r\n") :
                throw new StudioCommandException("stale_record", "The valve source no longer exists."));
            SourceValveEdits.Edit edit;
            try { edit = SourceValveEdits.Plan(Path.GetFileName(path), bytes, action, index, name, parameters, ct); }
            catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
            return workspace.Apply(edit.Label, [(path, (byte[]?)edit.Content)], ct);
        }, token);
    }
    private async Task<DocumentModel> AttachValveAsync(DocumentModel document, string path, int nodeIndex, string attribute, string parameters, CancellationToken token)
    {
        RequireNoDrafts(document); var source = ValveSource(document);
        if (!source.Resources.Values.Any(r => r.Path.Equals(path, StringComparison.OrdinalIgnoreCase)) || !MissionAiNetworks.IsCandidate(Path.GetFileName(path), mw3: true))
            throw new StudioCommandException("stale_record", "Choose a current mission network.");
        EndValveTest();
        return await PrepareSourceWorldEditAsync(document, "Adding valve association", (workspace, ct) =>
        {
            var bytes = workspace.Read(path, ct) ?? throw new StudioCommandException("stale_record", "The network source no longer exists.");
            SourceValveEdits.Edit edit;
            try { edit = SourceValveEdits.Attach(bytes, nodeIndex, attribute, parameters, ct); }
            catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
            return workspace.Apply(edit.Label, [(path, (byte[]?)edit.Content)], ct);
        }, token);
    }
}
