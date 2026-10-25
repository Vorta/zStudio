using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private sealed class AnimationProjectFiles(SourceWorkspace workspace, CancellationToken token) : IProjectFiles
    {
        public bool Exists(string relative) => workspace.Exists(relative, token);
        public byte[] Read(string relative, CancellationToken cancellation, ProjectReadLimits limits)
        {
            byte[] bytes = workspace.Read(relative, cancellation) ?? throw new FileNotFoundException("Animation source is missing.", relative);
            limits.Validate(bytes); return bytes;
        }
    }
    private CancellationTokenSource? sourceAnimationLoading;
    private CancellationTokenSource? sourcePathLoading;
    private Action? refreshAnimationPathControls;
    private readonly Dictionary<int, (string Id, DocumentModel Document, long Revision, int Entry, Guid Scenario, AnimationPaths Analysis)> sourceAnimationPaths = [];
    private readonly HashSet<int> shownAnimationPaths = [];
    private int? sourceAnimationEntry;
    private bool sourceAnimationPreview;
    private long sourceAnimationGeneration;
    private bool selectingAnimationCard;
    private ComboBox? sourceAnimationBindings;
    private bool synchronizingAnimationBinding;
    internal Action<CancellationToken>? SourceAnimationReading { get; set; }
    internal Action<CancellationToken>? SourceAnimationPathPreparing { get; set; }
    private void CancelSourceAnimationSelectionWork()
    {
        sourceAnimationLoading?.Cancel();
    }
    private void UpdateSourceAnimationControls()
    {
        SourcePickers.IsEnabled = !sourceWorkspaceBusy;
        SourceZoneOverlayItems.IsEnabled = !sourceAnimationPreview && !sourceWorkspaceBusy;
        WorldHighlights.IsEnabled = BackdropEnabled.IsEnabled = true;
        FlyEnabled.IsEnabled = !sourceAnimationPreview;
    }

    private FrameworkElement? AnimationObjectLinks(SceneViewport viewport, SceneInspection selected)
    {
        if (viewport != scene || sceneTree?.Projection is not { } projection || sceneTree.Sections is not { } sections || shownDocument?.SourceWorld == null) return null;
        int node = projection.Owner(selected.SelectionNode);
        if (projection.AnimationUses.GetValueOrDefault(node) is not { } uses) return null;
        var links = uses.Uses.Select(u => (Use: u, Definition: sections.Definitions.Definitions.FirstOrDefault(d => d.Entries.Contains(u.EntryIndex))))
            .Where(p => p.Definition != null).DistinctBy(p => (p.Definition!.Index, p.Use.EntryIndex)).Take(64).ToArray();
        if (links.Length == 0) return null;
        var panel = new CardSection("Animations affecting this object");
        foreach (var link in links)
        {
            var button = new Button { Content = link.Use.Name, ToolTip = $"{link.Use.Reason}\nEntry #{link.Use.EntryIndex}", HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new(0, 0, 0, 4) };
            button.Click += (_, _) => _ = ReportUi(async () =>
            {
                RequireNoDrafts(); sourceAnimationEntry = link.Use.EntryIndex;
                await EnsureAnimationCardForEntryAsync(shownDocument!, link.Use.EntryIndex, CancellationToken.None);
                SetWorldAnimationCandidate(shownDocument!, link.Definition!.Index, link.Use.EntryIndex);
            });
            panel.Body.Children.Add(button);
        }
        return panel;
    }

    private async Task EnsureAnimationCardForEntryAsync(DocumentModel document, int entry, CancellationToken token, bool preserveTimeline = false)
    {
        if (document.SourceWorld == null || sceneTree?.Sections is not { } sections) return;
        var definition = sections.Definitions.Definitions.FirstOrDefault(d => d.Entries.Contains(entry));
        if (definition == null) return;
        var listing = sections.Definitions.Listings.Single(l => l.Index == definition.Listing);
        var row = sceneTree.FindSection(new DefinitionEntry(definition, listing, false, 0, true).Key);
        if (row?.Section is not DefinitionEntry target) return;
        sourceAnimationEntry = entry;
        if (SectionSelection?.Section?.Key != target.Key)
        {
            selectingAnimationCard = true;
            try { SelectSceneTreeRow(row); }
            finally { selectingAnimationCard = false; }
        }
        if (SectionCard().ShowsAnimation(document, target))
        {
            synchronizingAnimationBinding = true;
            try { if (sourceAnimationBindings?.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == entry) is { } choice) sourceAnimationBindings.SelectedItem = choice; }
            finally { synchronizingAnimationBinding = false; }
            return;
        }
        await LoadAnimationCardCoreAsync(document, target, token, false, preserveTimeline);
        if (!SectionCard().ShowsAnimation(document, target)) throw new StudioCommandException("not_ready", "The animation definition could not be opened; inspect the reported source error.");
    }

    private async Task StartSourceAnimationPreviewAsync(DocumentModel document, int definition, int entry)
    {
        RequireNoDrafts();
        if (document.Document is not SourceSceneSnapshot source || !source.AnimationDefinitions.Definitions.Any(d => d.Index == definition && d.Entries.Contains(entry)))
            throw new StudioCommandException("stale_record", "The binding does not belong to this definition.");
        if (document != shownDocument || ViewModel.Resolver == null || scene == null) throw new StudioCommandException("not_ready", "Open a source map first.");
        if (scene.IsFlyActive || scene.IsPickupDragging || ZoneToolEngaged) throw new StudioCommandException("busy", "Finish navigation or zone editing before starting preview.");
        var sourceViewport = scene; long revision = document.Revision;
        await EnsureAnimationCardForEntryAsync(document, entry, document.Lifetime.Token);
        RequireNoDrafts();
        if (document != shownDocument || scene != sourceViewport || document.Revision != revision) throw new StudioCommandException("context_changed", "The selected map changed while opening the preview.");
        if (scene.IsFlyActive || scene.IsPickupDragging || ZoneToolEngaged) throw new StudioCommandException("busy", "Finish navigation or zone editing before starting preview.");
        SetWorldAnimationCandidate(document, definition, entry);
        await PinWorldAnimationAsync(document, definition, entry, document.Lifetime.Token);
        await WorldAnimationTransportAsync("play", 0, document.Lifetime.Token);
    }
    private void EndSourceAnimationPreview()
    {
        ExitWorldAnimation();
    }

    private Task LoadAnimationCardAsync(DocumentModel document, DefinitionEntry definition, CancellationToken token)
        => LoadAnimationCardCoreAsync(document, definition, token, false);
    private async Task LoadAnimationCardCoreAsync(DocumentModel document, DefinitionEntry definition, CancellationToken token, bool analyze, bool preserveTimeline = false)
    {
        if (SectionCard().ShowsAnimation(document, definition)) { if (!preserveTimeline) SetWorldAnimationDefinitionCandidate(document, definition); return; }
        if (parachutes?.Entry is int priorEntry && !definition.Definition.Entries.Contains(priorEntry)) CloseParachutes();
        sourceAnimationLoading?.Cancel();
        using var loading = CancellationTokenSource.CreateLinkedTokenSource(token, document.Lifetime.Token, shutdownToken);
        sourceAnimationLoading = loading;
        long revision = document.Revision;
        try
        {
            var workspace = document.SourceWorld!.Workspace.BeginPreparedEdit().Workspace;
            var model = await Task.Run(() =>
            {
                SourceAnimationReading?.Invoke(loading.Token);
                return SourceAnimationEdits.Read(workspace, document.SourceWorld.Mission, definition.Definition.Index, loading.Token);
            }, loading.Token);
            loading.Token.ThrowIfCancellationRequested();
            if (document != shownDocument || document.Revision != revision || SelectedAnimationDefinition()?.Key != definition.Key) return;
            var controls = AnimationPathControls(document, definition);
            SectionCard().ShowAnimation(document, definition, model, controls, ParachutePanel);
            RefreshWorldAnimationDropRelationships();
            if (!preserveTimeline) SetWorldAnimationDefinitionCandidate(document, definition);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or StudioCommandException)
        { if (document == shownDocument && SelectedAnimationDefinition()?.Key == definition.Key) ViewModel.Status = ex.Message; }
        finally { if (sourceAnimationLoading == loading) sourceAnimationLoading = null; }
    }

    private FrameworkElement AnimationPathControls(DocumentModel document, DefinitionEntry definition)
    {
        StackPanel panel = new();
        panel.Children.Add(new TextBlock { Text = "Preview binding", Margin = new(0, 0, 0, 4) });
        ComboBox bindings = new() { ItemsSource = definition.Definition.Entries.Select(e => new ComboBoxItem { Content = $"{definition.Definition.RootName} · entry #{e}", Tag = e }).ToArray(), SelectedIndex = definition.Definition.Entries.Count == 1 ? 0 : -1, MinHeight = 30 };
        sourceAnimationBindings = bindings;
        if (bindings.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == sourceAnimationEntry) is { } chosen) bindings.SelectedItem = chosen;
        AutomationProperties.SetName(bindings, "Animation binding entry"); panel.Children.Add(bindings);
        TextBlock note = new() { Text = "Paths use the selected binding. Selecting an animation does not start playback.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 5) }; panel.Children.Add(note);
        WrapPanel legend = new() { Margin = new(0, 0, 0, 6) }; panel.Children.Add(legend);
        foreach (var (label, color) in new[] { ("Objects", "#6FC8D6"), ("Cameras", "#F5A742") })
        {
            StackPanel item = new() { Orientation = Orientation.Horizontal, Margin = new(0, 0, 14, 2) };
            item.Children.Add(new Border { Width = 18, Height = 3, CornerRadius = new(1.5), Background = OutlinerKinds.Frozen(color), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0) });
            item.Children.Add(new TextBlock { Text = label }); legend.Children.Add(item);
        }
        WrapPanel actions = new(); panel.Children.Add(actions);
        Button Button(string name, Func<Task> action)
        {
            var button = new Button { Content = name, MinHeight = 30, Margin = new(0, 0, 4, 4) };
            AutomationProperties.SetName(button, name); button.Click += (_, _) => _ = ReportUi(action); actions.Children.Add(button); return button;
        }
        int Entry() => bindings.SelectedItem is ComboBoxItem { Tag: int entry } ? entry : throw new StudioCommandException("not_ready", "This definition has no compiled binding in this map.");
        var visibility = Button("Show guides", () => { if (shownAnimationPaths.Contains(definition.Definition.Index)) HideSourceAnimationPaths(definition.Definition.Index); else ShowSourceAnimationPaths(document, definition.Definition.Index, Entry()); return Task.CompletedTask; });
        Button("Evaluate paths", () => AnalyzeSourceAnimationAsync(document, definition.Definition.Index, Entry(), null, CancellationToken.None));
        Button("Frame paths", () => { scene?.FrameAnimationPaths(definition.Definition.Index.ToString(CultureInfo.InvariantCulture)); return Task.CompletedTask; });
        Button("Clear cached paths", () => { ClearSourceAnimationPaths(definition.Definition.Index); return Task.CompletedTask; });
        Button("Analyze drop windows", () => AnalyzeWorldDropWindowsAsync(document, definition.Definition.Index, Entry(), CancellationToken.None));
        TextBlock analysisInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 5) }; panel.Children.Add(analysisInfo);
        refreshAnimationPathControls = () =>
        {
            bindings.IsEnabled = sourcePathLoading == null && sectionCard?.HasDraft != true && parachuteDraft == null;
            visibility.Content = shownAnimationPaths.Contains(definition.Definition.Index) ? "Hide guides" : "Show guides";
            AutomationProperties.SetName(visibility, (string)visibility.Content);
            if (sourceAnimationPaths.TryGetValue(definition.Definition.Index, out var data))
                analysisInfo.Text = $"{data.Analysis.Status} · {data.Analysis.Seconds:0.###} s\n" + string.Join("\n", data.Analysis.Paths.Select(p => $"{p.Name} · {p.Points.Count} samples").Take(16)) + "\n" + string.Join("\n", data.Analysis.Diagnostics.Take(3).Select(d => Bounded(d, 240)));
            else analysisInfo.Text = "No matching paths — Evaluate paths.\nDrop analysis admits only recognized callback 911 calls; main preview inclusion is unchanged.";
        };
        refreshAnimationPathControls();
        bindings.SelectionChanged += (_, _) =>
        {
            if (synchronizingAnimationBinding || bindings.SelectedItem is not ComboBoxItem { Tag: int entry }) return;
            int? previous = sourceAnimationEntry;
            _ = ReportUi(async () =>
            {
                try { RequireNoDrafts(); sourceAnimationEntry = entry; SetWorldAnimationCandidate(document, definition.Definition.Index, entry); await Task.CompletedTask; }
                catch
                {
                    if (sourceAnimationBindings == bindings && bindings.SelectedItem is ComboBoxItem { Tag: int selected } && selected == entry)
                    {
                        synchronizingAnimationBinding = true;
                        try { sourceAnimationEntry = previous; bindings.SelectedItem = bindings.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == previous); }
                        finally { synchronizingAnimationBinding = false; }
                    }
                    throw;
                }
            });
        };
        // The old controls now have one host in Properties, never a floating overlay in a source map.
        if (ParachutePanel.Parent is Panel parent) parent.Children.Remove(ParachutePanel);
        ParachutePanel.Width = double.NaN; ParachutePanel.Margin = new(0, 8, 0, 0); ParachutePanel.Padding = new(0);
        ParachutePanel.HorizontalAlignment = HorizontalAlignment.Stretch; ParachutePanel.BorderThickness = new(0);
        return panel;
    }

    private async Task AnalyzeSourceAnimationAsync(DocumentModel document, int definition, int entry, double? seconds, CancellationToken token)
    {
        RequireNoDrafts();
        if (document != shownDocument || document.Document is not SourceSceneSnapshot snapshot || scene == null) throw new StudioCommandException("not_ready", "Open a source map first.");
        if (!snapshot.AnimationDefinitions.Definitions.Any(d => d.Index == definition && d.Entries.Contains(entry))) throw new StudioCommandException("stale_record", "The binding does not belong to this definition.");
        RequireWorldAnimationTarget(document, definition, entry);
        sourcePathLoading?.Cancel();
        using var pathLoading = CancellationTokenSource.CreateLinkedTokenSource(token, document.Lifetime.Token, shutdownToken);
        sourcePathLoading = pathLoading; token = pathLoading.Token;
        refreshAnimationPathControls?.Invoke();
        try
        {
            long generation = ++sourceAnimationGeneration;
            long revision = document.Revision; var viewport = scene; Guid scenario = worldAnimationScenarioEpoch;
            var configuration = WorldAnimationConfiguration;
            int lod = Math.Max(0, LodCombo.SelectedIndex);
            double range = seconds ?? worldAnimationSession?.State.RangeEnd ?? worldAnimationRangeEnd;
            ViewModel.Status = "Analyzing animation paths…";
            var context = await PrepareWorldAnimationContextAsync(document, token);
            var result = await Task.Run(() =>
            {
                SourceAnimationPathPreparing?.Invoke(token);
                var player = CreateWorldAnalysisPlayer(context, entry, configuration, token, lod);
                return player.AnalyzePaths(Math.Max(1.0 / 60, range), token);
            }, token);
            token.ThrowIfCancellationRequested();
            if (generation != sourceAnimationGeneration || shownDocument != document || document.Revision != revision || scene != viewport || scenario != worldAnimationScenarioEpoch) throw new StudioCommandException("context_changed", "The map or preview configuration changed during path analysis.");
            RequireNoDrafts();
            foreach (int stale in sourceAnimationPaths.Where(p => p.Value.Document != document || p.Value.Revision != revision).Select(p => p.Key).ToArray())
            { sourceAnimationPaths.Remove(stale); shownAnimationPaths.Remove(stale); viewport.ClearAnimationPaths(stale.ToString(CultureInfo.InvariantCulture)); }
            if (sourceAnimationPaths.Count >= 32 && !sourceAnimationPaths.ContainsKey(definition) || sourceAnimationPaths.Where(p => p.Key != definition).Sum(p => p.Value.Analysis.Paths.Sum(path => (long)path.Points.Count)) + result.Paths.Sum(path => (long)path.Points.Count) > 432002)
                throw new StudioCommandException("limit_exceeded", "The path cache is full; clear cached paths before analyzing more targets.");
            sourceAnimationPaths[definition] = (Guid.NewGuid().ToString(), document, revision, entry, scenario, result); shownAnimationPaths.Add(definition);
            viewport.SetAnimationPaths(definition.ToString(CultureInfo.InvariantCulture), result.Paths);
            viewport.EmphasizeAnimationPaths(definition.ToString(CultureInfo.InvariantCulture));
            ApplySectionPresentation();
            ViewModel.Status = $"{result.Paths.Count} motion paths · {result.Status} · {result.Seconds:0.###} s";
            refreshAnimationPathControls?.Invoke();
            RefreshWorldAnimationTimeline();
            }
        finally { if (sourcePathLoading == pathLoading) sourcePathLoading = null; refreshAnimationPathControls?.Invoke(); }
    }
    private void HideSourceAnimationPaths(int definition)
    {
        RequireNoDrafts(); sourcePathLoading?.Cancel(); ++sourceAnimationGeneration; shownAnimationPaths.Remove(definition); scene?.ClearAnimationPaths(definition.ToString(CultureInfo.InvariantCulture));
        refreshAnimationPathControls?.Invoke(); ApplySectionPresentation();
    }
    private void ShowSourceAnimationPaths(DocumentModel document, int definition, int entry)
    {
        RequireNoDrafts();
        if (!sourceAnimationPaths.TryGetValue(definition, out var paths) || paths.Document != document || paths.Revision != document.Revision || paths.Entry != entry || paths.Scenario != worldAnimationScenarioEpoch)
            throw new StudioCommandException("not_ready", "No matching paths — Evaluate paths first.");
        scene?.SetAnimationPaths(definition.ToString(CultureInfo.InvariantCulture), paths.Analysis.Paths);
        shownAnimationPaths.Add(definition); refreshAnimationPathControls?.Invoke(); ApplySectionPresentation();
    }
    private void ClearSourceAnimationPaths(int definition)
    {
        HideSourceAnimationPaths(definition); sourceAnimationPaths.Remove(definition);
        refreshAnimationPathControls?.Invoke(); RefreshWorldAnimationTimeline();
    }
    private async Task<DocumentModel> EditSourceAnimationAsync(DocumentModel document, int definition, IReadOnlyList<SourceAnimationEdits.Change> changes, CancellationToken token, bool committingSection = false)
    {
        return await PrepareSourceWorldEditAsync(document, "Editing animation", (workspace, ct) =>
        {
            var edit = SourceAnimationEdits.Plan(new AnimationProjectFiles(workspace, ct), SourceBuilder.AnimationRoot(document.SourceWorld!.Mission), definition, changes, ct);
            SourceAnimationEdits.ValidateSharedEdit(workspace, edit, ct);
            return workspace.Apply("Edit animation definition", [(edit.File, (byte[]?)edit.Content)], ct);
        }, token, committingSection: committingSection, requireAnimations: true);
    }

    private Task PreviewSourceAnimationAsync(DocumentModel document, int definition, int entry)
    {
        // Implemented by the source-map transport host; it keeps the world navigation and source Properties card.
        return StartSourceAnimationPreviewAsync(document, definition, entry);
    }

    private void RegisterSourceAnimationCommands(StudioCommands registry)
    {
        RegisterJob(registry, "source_animation", "Inspect and edit an existing source animation definition in the docked Properties pane. Addresses belong to the document revision. Edits preserve source trivia and apply to every expansion of the definition; tracks and event order are read only. begin selects the definition; set updates the shared draft; apply accepts one project undo step; cancel reverts the draft. read pages source records; each record contains its typed fields. context returns bounded read-only definition/script excerpts for a record, optionally near scriptTime. Save uses save_document.",
            [DocumentParameter, RevisionParameter, new("definition", "integer", "Definition index from scene_tree.", true, Minimum: 0),
             P("action", "string", "Operation.", true, "read", "context", "begin", "select", "set", "apply", "cancel"), P("record", "string", "Sequence or event record ID for select or context."), P("token", "string", "Current draft token for set (when pending), apply or cancel."),
             new("scriptTime", "number", "Optional script-local seconds for read-only frame context."),
             new("changes", "array", "Fields to set.", Items: new("", "object", "Field change.", Properties: [P("field", "string", "Field ID from read.", true),
                 new("values", "array", "Values as text; omit to remove an optional field.", Items: new("", "string", "Value."), MaxItems: 128)]), MaxItems: 256), ..PageParameters], false,
            async (a, token) =>
            {
                var doc = TargetDocument(a);
                if (doc.SourceWorld == null || doc != shownDocument || doc.Revision != a["revision"]!.GetValue<long>()) throw new StudioCommandException("revision_conflict", "Read the shown source map's current revision.");
                int definition = Int(a, "definition"); string action = Text(a, "action");
                if (action == "context") return Result(await ReadAnimationContextAsync(doc, definition, Text(a, "record"), a.ContainsKey("scriptTime") ? Number(a, "scriptTime") : null, token));
                if (action == "read")
                {
                    var prepared = doc.SourceWorld.Workspace.BeginPreparedEdit().Workspace;
                    var model = await Task.Run(() => SourceAnimationEdits.Read(prepared, doc.SourceWorld.Mission, definition, token), token);
                    if (doc != shownDocument || doc.Revision != a["revision"]!.GetValue<long>()) throw new StudioCommandException("revision_conflict", "The source map changed while reading the definition.");
                    return Page(model.Records, a);
                }
                var entry = sceneTree?.Sections?.Definitions.Definitions.FirstOrDefault(d => d.Index == definition) ?? throw new StudioCommandException("stale_record", "The definition is unavailable.");
                if (action == "begin")
                {
                    RequireNoDrafts();
                    var listing = sceneTree!.Sections!.Definitions.Listings.Single(l => l.Index == entry.Listing);
                    var row = sceneTree.FindSection(new DefinitionEntry(entry, listing, false, 0, true).Key) ?? throw new StudioCommandException("stale_record", "The definition row is unavailable.");
                    SelectSceneTreeRow(row);
                    await LoadAnimationCardAsync(doc, (DefinitionEntry)row.Section!, token);
                    RequireAnimationCard();
                }
                else
                {
                    var card = RequireAnimationCard();
                    if (action == "select") card.SelectAnimationRecord(Text(a, "record"));
                    else if (action == "set")
                    {
                        RequireNoDrafts(doc, committing: true, committingSection: true);
                        if (card.HasDraft) card.RequireDraft(Text(a, "token"));
                        var changes = (a["changes"] as JsonArray ?? throw new StudioCommandException("invalid_argument", "Supply changes.")).OfType<JsonObject>()
                            .Select(c => new SourceAnimationEdits.Change(Text(c, "field"), c["values"] is JsonArray values ? values.Select(v => v!.GetValue<string>()).ToArray() : null)).ToArray();
                        card.SetAnimationChanges(changes);
                    }
                    else { card.RequireDraft(Text(a, "token")); if (action == "cancel") card.Discard(); else { await card.ApplyAsync(); if (card.HasDraft) throw new StudioCommandException("edit_failed", "The edit was not accepted; the Properties draft retains its error."); } }
                }
                return Result(new { draft = SectionCard().AnimationDraft });

                SourceSectionCard RequireAnimationCard()
                {
                    var card = SectionCard();
                    if (doc != shownDocument || doc.Revision != a["revision"]!.GetValue<long>() ||
                        SelectedAnimationDefinition() is not { } current || current.Definition.Index != definition || !card.ShowsAnimation(doc, current))
                        throw new StudioCommandException("context_changed", "Select this definition in the current map before editing.");
                    return card;
                }
            });
    }

    private async Task<StudioResult> SourceAnimationPathCommandAsync(JsonObject args, DocumentModel document, string action, CancellationToken token)
    {
        int definition = Int(args, "definition");
        if (document.Document is not SourceSceneSnapshot snapshot) throw new StudioCommandException("read_only", "Source animation selection requires a source map.");
        var source = snapshot.AnimationDefinitions.Definitions.FirstOrDefault(d => d.Index == definition) ?? throw new StudioCommandException("stale_record", "The definition is unavailable.");
        int entry = args.ContainsKey("entry") ? Int(args, "entry") : source.Entries.Count == 1 ? source.Entries[0] : -1;
        if (action == "analyze") await AnalyzeSourceAnimationAsync(document, definition, entry, args.ContainsKey("seconds") ? Number(args, "seconds") : null, token);
        else if (action == "show") ShowSourceAnimationPaths(document, definition, entry);
        else if (action == "hide") HideSourceAnimationPaths(definition);
        else if (action == "clear") ClearSourceAnimationPaths(definition);
        else if (action == "preview") await StartSourceAnimationPreviewAsync(document, definition, entry);
        else if (action == "exit") EndSourceAnimationPreview();
        else if (action == "frame") scene?.FrameAnimationPaths(definition.ToString(CultureInfo.InvariantCulture));
        else if (action is "samples" or "markers")
        {
            if (!sourceAnimationPaths.TryGetValue(definition, out var state) || state.Document != document || state.Revision != document.Revision || Text(args, "snapshot") != state.Id)
                throw new StudioCommandException("stale_snapshot", "Read the current animation path snapshot.");
            return action == "markers" ? Page(state.Analysis.Events, args) : Page(state.Analysis.Paths.SelectMany(p => p.Points.Select((s, i) => new { path = p.Id, node = p.Node, segment = i, time = s.Time, position = JsonData.Vector(s.Position), discontinuity = s.Break })), args);
        }
        else if (action != "state") throw new StudioCommandException("invalid_argument", "This operation is for drop windows; omit definition and use its drop-window snapshot.");
        return Result(new { paths = SourceAnimationPathsState(), parachute = ParachuteState() });
    }
    private object SourceAnimationPathsState() => new
    {
        previewing = sourceAnimationPreview,
        definitions = sourceAnimationPaths.Where(p => p.Value.Document == shownDocument && p.Value.Revision == shownDocument.Revision).Select(p => new
        {
            definition = p.Key, snapshot = p.Value.Id, entry = p.Value.Entry, scenario = p.Value.Scenario, purpose = "paths", compatible = p.Value.Scenario == worldAnimationScenarioEpoch, shown = shownAnimationPaths.Contains(p.Key),
            seconds = p.Value.Analysis.Seconds, status = p.Value.Analysis.Status,
            diagnostics = p.Value.Analysis.Diagnostics.Select(d => Bounded(d, 512)).ToArray(),
            paths = p.Value.Analysis.Paths.Select(t => new { t.Id, t.Entry, t.Instance, t.Node, t.Kind, name = Bounded(t.Name, 128), samples = t.Points.Count }).ToArray(),
            eventCount = p.Value.Analysis.Events.Count
        }).ToArray()
    };
}
