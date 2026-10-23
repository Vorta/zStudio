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
    private readonly Dictionary<int, (string Id, DocumentModel Document, long Revision, int Entry, AnimationPaths Analysis)> sourceAnimationPaths = [];
    private readonly HashSet<int> shownAnimationPaths = [];
    private int? sourceAnimationEntry;
    private bool sourceAnimationPreview;
    private long sourceAnimationGeneration;
    private bool selectingAnimationCard;
    private ComboBox? sourceAnimationBindings;
    private bool synchronizingAnimationBinding;
    internal Action<CancellationToken>? SourceAnimationReading { get; set; }
    private void CancelSourceAnimationSelectionWork()
    {
        sourceAnimationLoading?.Cancel(); sourcePathLoading?.Cancel(); ++sourceAnimationGeneration;
        if (parachuteLoading != null) { parachuteLoading.Cancel(); ++parachuteGeneration; }
    }
    private void UpdateSourceAnimationControls()
    {
        SourcePickers.IsEnabled = SourceZoneOverlayItems.IsEnabled = !sourceAnimationPreview && !sourceWorkspaceBusy;
        WorldHighlights.IsEnabled = BackdropEnabled.IsEnabled = !sourceAnimationPreview;
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
                await AnalyzeSourceAnimationAsync(shownDocument!, link.Definition!.Index, link.Use.EntryIndex, null, CancellationToken.None);
            });
            panel.Body.Children.Add(button);
        }
        return panel;
    }

    private async Task EnsureAnimationCardForEntryAsync(DocumentModel document, int entry, CancellationToken token)
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
        await LoadAnimationCardCoreAsync(document, target, token, false);
        if (!SectionCard().ShowsAnimation(document, target)) throw new StudioCommandException("not_ready", "The animation definition could not be opened; inspect the reported source error.");
    }

    private async Task StartSourceAnimationPreviewAsync(DocumentModel document, int definition, int entry)
    {
        RequireNoDrafts();
        if (document.Document is not SourceSceneSnapshot source || !source.AnimationDefinitions.Definitions.Any(d => d.Index == definition && d.Entries.Contains(entry)))
            throw new StudioCommandException("stale_record", "The binding does not belong to this definition.");
        EndSourceAnimationPreview();
        CancelSourceAnimationSelectionWork();
        if (document != shownDocument || ViewModel.Resolver == null || scene == null) throw new StudioCommandException("not_ready", "Open a source map first.");
        if (scene.IsFlyActive || scene.IsPickupDragging || ZoneToolEngaged) throw new StudioCommandException("busy", "Finish navigation or zone editing before starting preview.");
        var sourceViewport = scene; long revision = document.Revision;
        await EnsureAnimationCardForEntryAsync(document, entry, document.Lifetime.Token);
        RequireNoDrafts();
        if (document != shownDocument || scene != sourceViewport || document.Revision != revision) throw new StudioCommandException("context_changed", "The selected map changed while opening the preview.");
        if (scene.IsFlyActive || scene.IsPickupDragging || ZoneToolEngaged) throw new StudioCommandException("busy", "Finish navigation or zone editing before starting preview.");
        var pose = scene.CaptureView();
        var editor = new AnimationEditor(document, entry, ViewModel.Resolver, document.Lifetime.Token, ViewModel);
        editor.SetSourceMapPreview(); animation = editor; sourceAnimationPreview = true; previewId = Guid.NewGuid();
        UpdateSourceAnimationControls();
        refreshAnimationPathControls?.Invoke();
        editor.SetupRequested += () => SectionCard().SetAnimationSetup(editor.PreviewSetupView, true);
        AnimationHost.Content = editor; AnimationHost.Visibility = Visibility.Visible;
        try
        {
            await editor.InitializeAsync();
            if (!ReferenceEquals(animation, editor) || document != shownDocument) throw new StudioCommandException("context_changed", "The selected preview changed while loading.");
            editor.Viewport.RestoreView(pose);
            ApplySceneOptions();
        }
        catch { if (ReferenceEquals(animation, editor)) EndSourceAnimationPreview(); else editor.Dispose(); throw; }
    }
    private void EndSourceAnimationPreview()
    {
        if (!sourceAnimationPreview) return;
        sectionCard?.SetAnimationSetup(null);
        sourceAnimationPreview = false; animation?.Dispose(); animation = null; previewId = Guid.NewGuid();
        AnimationHost.Content = null; AnimationHost.Visibility = Visibility.Collapsed;
        UpdateSourceAnimationControls(); ApplySceneOptions();
        refreshAnimationPathControls?.Invoke();
    }

    private Task LoadAnimationCardAsync(DocumentModel document, DefinitionEntry definition, CancellationToken token)
        => LoadAnimationCardCoreAsync(document, definition, token, true);
    private async Task LoadAnimationCardCoreAsync(DocumentModel document, DefinitionEntry definition, CancellationToken token, bool analyze)
    {
        if (SectionCard().ShowsAnimation(document, definition)) return;
        if (parachutes?.Entry is int priorEntry && !definition.Definition.Entries.Contains(priorEntry)) CloseParachutes();
        EndSourceAnimationPreview();
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
            if (document != shownDocument || document.Revision != revision || SectionSelection?.Section?.Key != definition.Key) return;
            var controls = AnimationPathControls(document, definition);
            SectionCard().ShowAnimation(document, definition, model, controls, ParachutePanel);
            if (analyze && definition.Definition.Entries.Count > 0)
            {
                int entry = definition.Definition.Entries.Contains(sourceAnimationEntry ?? -1) ? sourceAnimationEntry!.Value : definition.Definition.Entries[0];
                await AnalyzeSourceAnimationAsync(document, definition.Definition.Index, entry, null, loading.Token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or InvalidDataException or StudioCommandException)
        { if (document == shownDocument && SectionSelection?.Section?.Key == definition.Key) ViewModel.Status = ex.Message; }
        finally { if (sourceAnimationLoading == loading) sourceAnimationLoading = null; }
    }

    private FrameworkElement AnimationPathControls(DocumentModel document, DefinitionEntry definition)
    {
        StackPanel panel = new();
        panel.Children.Add(new TextBlock { Text = "Preview binding", Margin = new(0, 0, 0, 4) });
        ComboBox bindings = new() { ItemsSource = definition.Definition.Entries.Select(e => new ComboBoxItem { Content = $"{definition.Definition.RootName} · entry #{e}", Tag = e }).ToArray(), SelectedIndex = 0, MinHeight = 30 };
        sourceAnimationBindings = bindings;
        if (bindings.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (int)i.Tag == sourceAnimationEntry) is { } chosen) bindings.SelectedItem = chosen;
        AutomationProperties.SetName(bindings, "Animation binding entry"); panel.Children.Add(bindings);
        TextBlock note = new() { Text = "Paths use the selected binding. Selecting an animation does not start playback.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 5) }; panel.Children.Add(note);
        WrapPanel actions = new(); panel.Children.Add(actions);
        Button Button(string name, Func<Task> action)
        {
            var button = new Button { Content = name, MinHeight = 30, Margin = new(0, 0, 4, 4) };
            AutomationProperties.SetName(button, name); button.Click += (_, _) => _ = ReportUi(action); actions.Children.Add(button); return button;
        }
        int Entry() => bindings.SelectedItem is ComboBoxItem { Tag: int entry } ? entry : throw new StudioCommandException("not_ready", "This definition has no compiled binding in this map.");
        var visibility = Button("Show paths", async () => { if (shownAnimationPaths.Contains(definition.Definition.Index)) HideSourceAnimationPaths(definition.Definition.Index); else await AnalyzeSourceAnimationAsync(document, definition.Definition.Index, Entry(), null, CancellationToken.None); });
        Button("Frame paths", () => { scene?.FrameAnimationPaths(definition.Definition.Index.ToString(CultureInfo.InvariantCulture)); return Task.CompletedTask; });
        var preview = Button("Preview", async () => { if (sourceAnimationPreview) EndSourceAnimationPreview(); else await PreviewSourceAnimationAsync(document, definition.Definition.Index, Entry()); });
        TextBlock analysisInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 5) }; panel.Children.Add(analysisInfo);
        refreshAnimationPathControls = () =>
        {
            bindings.IsEnabled = !sourceAnimationPreview && sourcePathLoading == null && sectionCard?.HasDraft != true && parachuteDraft == null;
            visibility.Content = shownAnimationPaths.Contains(definition.Definition.Index) ? "Hide paths" : "Show paths";
            preview.Content = sourceAnimationPreview ? "Exit preview" : "Preview";
            AutomationProperties.SetName(visibility, (string)visibility.Content); AutomationProperties.SetName(preview, (string)preview.Content);
            if (sourceAnimationPaths.TryGetValue(definition.Definition.Index, out var data))
                analysisInfo.Text = $"{data.Analysis.Status} · {data.Analysis.Seconds:0.###} s\n" + string.Join("\n", data.Analysis.Paths.Select(p => $"{p.Name} · {p.Points.Count} samples").Take(16)) + "\n" + string.Join("\n", data.Analysis.Diagnostics.Take(3).Select(d => Bounded(d, 240)));
            else analysisInfo.Text = "";
        };
        refreshAnimationPathControls();
        bindings.SelectionChanged += (_, _) =>
        {
            if (synchronizingAnimationBinding || bindings.SelectedItem is not ComboBoxItem { Tag: int entry }) return;
            int? previous = sourceAnimationEntry;
            _ = ReportUi(async () =>
            {
                try { await AnalyzeSourceAnimationAsync(document, definition.Definition.Index, entry, null, CancellationToken.None); }
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
        if (sourceAnimationPreview) throw new StudioCommandException("busy", "Exit animation preview before analyzing map paths.");
        if (document != shownDocument || document.Document is not SourceSceneSnapshot snapshot || scene == null) throw new StudioCommandException("not_ready", "Open a source map first.");
        if (!snapshot.AnimationDefinitions.Definitions.Any(d => d.Index == definition && d.Entries.Contains(entry))) throw new StudioCommandException("stale_record", "The binding does not belong to this definition.");
        if (parachutes != null && parachutes.Entry != entry) CloseParachutes();
        await EnsureAnimationCardForEntryAsync(document, entry, token);
        sourcePathLoading?.Cancel();
        using var pathLoading = CancellationTokenSource.CreateLinkedTokenSource(token, document.Lifetime.Token, shutdownToken);
        sourcePathLoading = pathLoading; token = pathLoading.Token;
        refreshAnimationPathControls?.Invoke();
        try
        {
            long generation = ++sourceAnimationGeneration;
            long revision = document.Revision; var viewport = scene; var difficulty = ViewModel.Difficulty;
            ViewModel.Status = "Analyzing animation paths…";
            var result = await Task.Run(() =>
            {
                var context = AnimationPreviewContext.FromSources(snapshot, difficulty, token);
                var player = new AnimationPlayer(context, entry, 1, false, token);
                double range = seconds ?? player.MeasureDuration(token).Seconds;
                return (Context: context, Analysis: player.AnalyzePaths(Math.Max(1.0 / 60, range), token));
            }, token);
            token.ThrowIfCancellationRequested();
            if (generation != sourceAnimationGeneration || shownDocument != document || document.Revision != revision || scene != viewport || SectionSelection?.Section is not DefinitionEntry selectedDefinition || selectedDefinition.Definition.Index != definition) throw new StudioCommandException("context_changed", "The map or selected analysis changed during path analysis.");
            RequireNoDrafts();
            foreach (int stale in sourceAnimationPaths.Where(p => p.Value.Document != document || p.Value.Revision != revision).Select(p => p.Key).ToArray())
            { sourceAnimationPaths.Remove(stale); shownAnimationPaths.Remove(stale); viewport.ClearAnimationPaths(stale.ToString(CultureInfo.InvariantCulture)); }
            if (sourceAnimationPaths.Count >= 32 && !sourceAnimationPaths.ContainsKey(definition) || sourceAnimationPaths.Where(p => p.Key != definition).Sum(p => p.Value.Analysis.Paths.Sum(path => (long)path.Points.Count)) + result.Analysis.Paths.Sum(path => (long)path.Points.Count) > 432002)
                throw new StudioCommandException("limit_exceeded", "Hide some animation paths before analyzing more targets.");
            sourceAnimationPaths[definition] = (Guid.NewGuid().ToString(), document, revision, entry, result.Analysis); shownAnimationPaths.Add(definition); sourceAnimationEntry = entry;
            viewport.SetAnimationPaths(definition.ToString(CultureInfo.InvariantCulture), result.Analysis.Paths);
            viewport.EmphasizeAnimationPaths(definition.ToString(CultureInfo.InvariantCulture));
            ApplySectionPresentation();
            var flight = VtolDropWindows.Discover(result.Context.Package, token).Candidates.FirstOrDefault(c => c.EntryIndex == entry);
            if (flight != null) await ShowParachutesAsync(token, entry);
            else if (parachutes != null) CloseParachutes();
            ViewModel.Status = $"{result.Analysis.Paths.Count} motion paths · {result.Analysis.Status} · {result.Analysis.Seconds:0.###} s";
            refreshAnimationPathControls?.Invoke();
            }
        finally { if (sourcePathLoading == pathLoading) sourcePathLoading = null; refreshAnimationPathControls?.Invoke(); }
    }
    private void HideSourceAnimationPaths(int definition)
    {
        RequireNoDrafts(); sourcePathLoading?.Cancel(); ++sourceAnimationGeneration; shownAnimationPaths.Remove(definition); scene?.ClearAnimationPaths(definition.ToString(CultureInfo.InvariantCulture));
        if (sourceAnimationPaths.TryGetValue(definition, out var data) && parachutes?.Entry == data.Entry) CloseParachutes();
        sourceAnimationPaths.Remove(definition);
        refreshAnimationPathControls?.Invoke(); ApplySectionPresentation();
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
        RegisterJob(registry, "source_animation", "Inspect and edit an existing source animation definition in the docked Properties pane. Addresses belong to the document revision. Edits preserve source trivia and apply to every expansion of the definition; tracks and event order are read only. begin selects the definition; set updates the shared draft; apply accepts one project undo step; cancel reverts the draft. read pages source records; each record contains its typed fields. Save uses save_document.",
            [DocumentParameter, RevisionParameter, new("definition", "integer", "Definition index from scene_tree.", true, Minimum: 0),
             P("action", "string", "Operation.", true, "read", "begin", "select", "set", "apply", "cancel"), P("record", "string", "Sequence or event record ID for select."), P("token", "string", "Current draft token for set (when pending), apply or cancel."),
             new("changes", "array", "Fields to set.", Items: new("", "object", "Field change.", Properties: [P("field", "string", "Field ID from read.", true),
                 new("values", "array", "Values as text; omit to remove an optional field.", Items: new("", "string", "Value."), MaxItems: 128)]), MaxItems: 256), ..PageParameters], false,
            async (a, token) =>
            {
                var doc = TargetDocument(a);
                if (doc.SourceWorld == null || doc != shownDocument || doc.Revision != a["revision"]!.GetValue<long>()) throw new StudioCommandException("revision_conflict", "Read the shown source map's current revision.");
                int definition = Int(a, "definition"); string action = Text(a, "action");
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
                        SectionSelection?.Section is not DefinitionEntry current || current.Definition.Index != definition || !card.ShowsAnimation(doc, current))
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
        int entry = args.ContainsKey("entry") ? Int(args, "entry") : source.Entries.FirstOrDefault(-1);
        if (action is "show" or "analyze") await AnalyzeSourceAnimationAsync(document, definition, entry, args.ContainsKey("seconds") ? Number(args, "seconds") : null, token);
        else if (action == "hide") HideSourceAnimationPaths(definition);
        else if (action == "preview") await StartSourceAnimationPreviewAsync(document, definition, entry);
        else if (action == "exit") { RequireNoDrafts(); EndSourceAnimationPreview(); }
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
            definition = p.Key, snapshot = p.Value.Id, entry = p.Value.Entry, shown = shownAnimationPaths.Contains(p.Key),
            seconds = p.Value.Analysis.Seconds, status = p.Value.Analysis.Status,
            diagnostics = p.Value.Analysis.Diagnostics.Select(d => Bounded(d, 512)).ToArray(),
            paths = p.Value.Analysis.Paths.Select(t => new { t.Id, t.Entry, t.Instance, t.Node, name = Bounded(t.Name, 128), samples = t.Points.Count }).ToArray(),
            eventCount = p.Value.Analysis.Events.Count
        }).ToArray()
    };
}
