using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Input;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private enum AiPathDraftLifetime { Pending, ReleasedForAcceptance, Canceled, Completed }
    private sealed class AiPathDraft(DocumentModel document, SceneViewport viewport, string network,
        Dictionary<string, MissionPickupSource> sources, IReadOnlyDictionary<MissionPickupSource, Vector3> before, string mode)
    {
        public readonly DocumentModel Document = document;
        public readonly SceneViewport Viewport = viewport;
        public readonly string Snapshot = viewport.AiNetworks.Id, Network = network, Token = Guid.NewGuid().ToString();
        public long Revision = document.Revision;
        public AiPathDraftLifetime Lifetime;
        public long? RolledBackRevision;
        public readonly Dictionary<string, MissionPickupSource> Sources = sources;
        public readonly IReadOnlyDictionary<MissionPickupSource, Vector3> Before = before;
        public IReadOnlyDictionary<MissionPickupSource, Vector3> After = before;
        public readonly string Mode = mode;
        public string Axis = "all";
        public string[] Input = mode == "scale" ? ["1", "1", "1"] : ["0", "0", "0"];
        public string? Error;
    }
    private AiPathDraft? aiPathDraft;
    private bool synchronizingAiPath;
    private bool HasAiPathDraft => aiPathDraft != null;
    private void ConfigureAiPath(SceneViewport viewport)
    {
        viewport.AiPathSelectionChanging = () => !HasAiPathDraft && ResolvePropertiesDrafts();
        viewport.AiPathSelectionChanged += () => { if (scene == viewport) UpdateAiPathPanel(); };
        viewport.AiPathEditingChanged += () =>
        {
            if (scene != viewport) return;
            UpdateAiPathPanel();
            // A source map shows its networks by their eyes again once path editing (which shows only its network) ends.
            if (sourceChromeActive && viewport.AiPathEditingNetwork == null) ApplySectionPresentation();
        };
        viewport.AiPathPointerMoved += (delta, x, y) =>
        {
            if (scene != viewport || aiPathDraft is not { } draft) return;
            try
            {
                RequireAiPathDraft();
                var value = draft.Mode switch { "move" => delta, "rotate" => new Vector3((float)x, 0, 0), _ => new Vector3((float)(1 + x / 150)) };
                if (draft.Mode == "move" && draft.Axis != "all")
                {
                    // Constrained motion follows pointer distance along the chosen world axis.
                    float amount = (float)((x - y) * viewport.AiPathPointerUnitsPerPixel);
                    value = draft.Axis switch { "x" => new(amount, 0, 0), "y" => new(0, amount, 0), _ => new(0, 0, amount) };
                }
                draft.Input = [value.X.ToString("G9", CultureInfo.InvariantCulture), value.Y.ToString("G9", CultureInfo.InvariantCulture), value.Z.ToString("G9", CultureInfo.InvariantCulture)];
                PreviewAiPathDraft(); UpdateAiPathPanel();
            }
            catch (Exception ex) when (ex is StudioCommandException or ArgumentException or InvalidOperationException) { ViewModel.Status = ex.Message; }
        };
        viewport.AiPathTransformConfirmRequested += () => _ = ReportUi(async () => { await ApplyAiPathAsync(CancellationToken.None); });
        viewport.AiPathTransformCancelRequested += CancelAiPathDraft;
    }
    private void RequireAiPathEditable()
    {
        if (!IsAiWorld || shownDocument is not { IsDisposed: false } doc || doc.PickupEdits == null)
            throw new StudioCommandException("not_ready", "Open a Whole world with supported AI coordinate records.");
        if (doc.SourceWorld == null) throw new StudioCommandException("read_only", "This world is read only: edit its source project instead.");
        if (scene!.IsFlyActive || scene.IsPickupDragging || doc.SourceWorld?.IsRebuilding == true || sourceWorkspaceBusy)
            throw new StudioCommandException("busy", "Finish the active gesture or world operation first.");
    }
    private AiPathDraft RequireAiPathDraft(string? token = null)
    {
        var draft = aiPathDraft ?? throw new StudioCommandException("no_draft", "Begin an AI path transform first.");
        if (token != null && token != draft.Token) throw new StudioCommandException("stale_draft", "Read the current AI path draft token.");
        RequireAiPathEditable();
        if (shownDocument != draft.Document || scene != draft.Viewport || draft.Document.Revision != draft.Revision || scene.AiNetworks.Id != draft.Snapshot)
            throw new StudioCommandException("context_changed", "The AI path draft's document or graph changed. Cancel it and select the current nodes.");
        return draft;
    }
    private void EnterAiPath(string? network = null)
    {
        RequireNoDrafts(); RequireAiPathEditable();
        network ??= scene!.SelectedAiNode is { } selected ? scene.AiNetworks.Find(selected)?.Network.Id : scene!.AiNetworkFilter;
        if (network == null) throw new StudioCommandException("selection_required", "Select an AI node or choose one network before entering Edit mode.");
        var graph = scene!.AiNetworks;
        var target = graph.Networks.FirstOrDefault(n => n.Id == network) ?? throw new StudioCommandException("stale_record", "Network unavailable.");
        if (target.Nodes.Count == 0 || target.Nodes.Count > AiPathTransforms.MaximumNodes)
            throw new StudioCommandException("unsupported", $"Edit mode supports 1–{AiPathTransforms.MaximumNodes:N0} nodes in one network.");
        EndZoneToolFor("editing an AI path");
        // In a source map the edited network shows (its eye opens), and stays shown after editing.
        if (sourceChromeActive && shownDocument?.SourceWorld != null) { ResetSectionStateFor(shownDocument); shownNetworks.Add(network); ReconcileMasters(); }
        SetAiOptions(true, aiThroughGeometry, network);
        selectedNode = null; inspectedSceneSource = null;
        scene.SetAiPathEditing(network, scene.SelectedAiNode is { } id ? [id] : []);
        // The edited network shows in the view (it may have been hidden), filtered to it while editing.
        if (sourceChromeActive && shownDocument?.SourceWorld != null) ApplySectionPresentation();
        UpdateAiPathPanel();
    }
    private void BeginAiPathTransform(string mode, bool pointer = false)
    {
        if (mode is not ("move" or "rotate" or "scale")) throw new StudioCommandException("invalid_argument", "Use move, rotate or scale.");
        RequireNoDrafts(); RequireAiPathEditable();
        var viewport = scene!; var document = shownDocument!; var edits = document.PickupEdits!;
        string network = viewport.AiPathEditingNetwork ?? throw new StudioCommandException("not_ready", "Enter AI path Edit mode first.");
        if (viewport.SelectedAiPathNodes.Count == 0) throw new StudioCommandException("selection_required", "Select one or more nodes.");
        Dictionary<string, MissionPickupSource> sources = [];
        Dictionary<MissionPickupSource, Vector3> before = new(edits.SourceComparer);
        foreach (string id in viewport.SelectedAiPathNodes)
        {
            var found = viewport.AiNetworks.Find(id) ?? throw new StudioCommandException("stale_record", "Node unavailable.");
            if (found.Network.Id != network) throw new StudioCommandException("stale_record", "Selection spans different networks.");
            var source = new MissionPickupSource(Path.GetFullPath(found.Network.Archive).ToUpperInvariant(), found.Network.MemberIndex, found.Network.Member.ToUpperInvariant(), checked((int)found.Node.SourceOffset));
            sources.Add(id, source); before.Add(source, edits.Position(source));
        }
        edits.PreviewAiPositions(before);
        aiPathDraft = new(document, viewport, network, sources, before, mode);
        PreviewAiPathDraft(); UpdateAiPathPanel(); viewport.SetAiPathPointerTransform(pointer);
    }
    private void PreviewAiPathDraft()
    {
        var draft = RequireAiPathDraft();
        try
        {
            var values = draft.Input.Select(s => s.Length <= 64 && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && double.IsFinite(v)
                ? v : throw new ArgumentException("Enter finite numbers (up to 64 characters)." )).ToArray();
            Vector3 vector = new((float)values[0], (float)values[1], (float)values[2]);
            if (draft.Axis != "all" && draft.Mode != "rotate")
            {
                float neutral = draft.Mode == "scale" ? 1 : 0;
                vector = new(draft.Axis == "x" ? vector.X : neutral, draft.Axis == "y" ? vector.Y : neutral, draft.Axis == "z" ? vector.Z : neutral);
            }
            draft.After = draft.Mode switch
            {
                "move" => AiPathTransforms.Translate(draft.Before, vector),
                "rotate" => AiPathTransforms.Rotate(draft.Before, draft.Axis == "x" ? 0 : draft.Axis == "z" ? 2 : 1, values[0]),
                _ => AiPathTransforms.Scale(draft.Before, vector)
            };
            // The preview shows the positions as they are stored (one decimal), as Apply writes them.
            var stored = draft.Document.PickupEdits!.PreviewAiPositions(draft.After);
            draft.Viewport.SetAiPathPreview(draft.Sources.ToDictionary(p => p.Key, p => stored[p.Value].Position));
            draft.Error = null;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or InvalidOperationException)
        { draft.Error = ex.Message; }
        AiPathValidation.Text = draft.Error ?? "Enter confirms one undo step; Escape cancels. Rotation uses X input as degrees.";
    }
    private async Task<DocumentModel> ApplyAiPathAsync(CancellationToken token)
    {
        var draft = RequireAiPathDraft(); RequireNoDrafts(draft.Document, committingAiPath: draft);
        PreviewAiPathDraft();
        if (draft.Error != null) throw new StudioCommandException("invalid_argument", draft.Error);
        token.ThrowIfCancellationRequested();
        if (draft.Document.SourceWorld != null)
        {
            // A move below the stored precision (one decimal) changes nothing: end the draft without evaluating the world.
            var stored = draft.Document.PickupEdits!.PreviewAiPositions(draft.After);
            if (stored.All(p => p.Value.Position == draft.Before[p.Key]))
            {
                CancelAiPathDraft(); UpdateAiPathPanel();
                ViewModel.Status = "Nothing to apply: the nodes keep their stored positions (one decimal).";
                return draft.Document;
            }
            try
            {
                var next = await PrepareSourceWorldEditAsync(draft.Document, "Transforming AI path nodes", (workspace, ct) =>
                {
                    var edits = draft.Document.PickupEdits!;
                    var after = edits.PreviewAiPositions(draft.After);
                    var changes = edits.SourceChanges(after, workspace, ct).Select(change => (change.Relative, (byte[]?)change.Content));
                    return workspace.Apply("Transform AI path nodes", changes, ct);
                }, token, committingAiPath: draft);
                draft.Lifetime = AiPathDraftLifetime.Completed;
                ViewModel.Status = "AI path transformed. The rebuilt source world has left Edit mode; select its current path to continue.";
                return next;
            }
            catch
            {
                // An explicit Escape/Cancel during preparation is final. Only the acceptance path releases
                // a still-wanted draft; restore it after failed acceptance or a proven source transaction rollback.
                if (draft.Lifetime == AiPathDraftLifetime.ReleasedForAcceptance && aiPathDraft == null &&
                    shownDocument == draft.Document && scene == draft.Viewport && !draft.Document.IsDisposed &&
                    draft.Document.SourceWorld?.Owner == draft.Document && scene.AiNetworks.Id == draft.Snapshot &&
                    draft.Document.Revision == (draft.RolledBackRevision ?? draft.Revision) &&
                    draft.Before.All(p => draft.Document.PickupEdits?.Coordinate(p.Key) is { Kind: "ai" } && draft.Document.PickupEdits.Position(p.Key) == p.Value))
                {
                    draft.Revision = draft.Document.Revision; draft.RolledBackRevision = null; draft.Lifetime = AiPathDraftLifetime.Pending;
                    aiPathDraft = draft; scene.SetAiPathEditing(draft.Network, draft.Sources.Keys); PreviewAiPathDraft(); UpdateAiPathPanel();
                }
                throw;
            }
        }
        throw new StudioCommandException("read_only", "This world is read only: edit its source project instead.");
    }
    private void CancelAiPathDraft()
        => ReleaseAiPathDraft(AiPathDraftLifetime.Canceled);
    private void ReleaseAiPathDraft(AiPathDraftLifetime lifetime)
    {
        if (aiPathDraft is not { } draft) return;
        draft.Lifetime = lifetime;
        aiPathDraft = null;
        if (scene == draft.Viewport) { scene.SetAiPathPointerTransform(false); scene.SetAiPathPreview(null); }
        UpdateAiPathPanel();
        // An object shown while the draft was open is selected in the view once whatever ended the draft has finished.
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, () => ReselectShownSelection());
    }
    private bool ResolveAiPathDraft(DocumentModel? document = null)
    {
        if (aiPathDraft == null || document != null && aiPathDraft.Document != document) return true;
        if (automationRequest.Value) return false;
        if (MessageBox.Show(this, "Discard the pending AI path transform? Choose No to return to its Confirm button.", "AI path draft", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return false;
        CancelAiPathDraft(); return true;
    }
    private object AiPathState() => new
    {
        editing = scene?.AiPathEditingNetwork != null, network = scene?.AiPathEditingNetwork,
        selected = scene?.SelectedAiPathNodes.Take(256).ToArray() ?? [], selectedCount = scene?.SelectedAiPathNodes.Count ?? 0,
        selectedTruncated = scene?.SelectedAiPathNodes.Count > 256,
        draft = aiPathDraft is not { } d ? null : new { token = d.Token, document = d.Document.SessionId, revision = d.Revision, snapshot = d.Snapshot,
            mode = d.Mode, axis = d.Axis, input = d.Input, error = d.Error, count = d.Before.Count, pivot = JsonData.Vector(AiPathTransforms.Center(d.Before)) }
    };
    private void UpdateAiPathPanel()
    {
        if (AiPathPanel == null) return;
        bool editing = IsAiWorld && scene?.AiPathEditingNetwork != null;
        AiPathPanel.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        AiPathPanel.IsEnabled = !sourceWorkspaceBusy;
        AiPathHeading.Text = $"AI path · {scene?.SelectedAiPathNodes.Count ?? 0} selected";
        AiPathFields.Visibility = aiPathDraft == null ? Visibility.Collapsed : Visibility.Visible;
        synchronizingAiPath = true;
        try
        {
            AiPathX.Text = aiPathDraft?.Input[0] ?? "0"; AiPathY.Text = aiPathDraft?.Input[1] ?? "0"; AiPathZ.Text = aiPathDraft?.Input[2] ?? "0";
            AiPathAxis.SelectedIndex = aiPathDraft?.Axis switch { "x" => 1, "y" => 2, "z" => 3, _ => 0 };
            AiPathValidation.Text = aiPathDraft?.Error ?? (aiPathDraft is { Mode: "rotate" } ? "X input: degrees; Y axis unless constrained. Enter confirms; Escape cancels." : "Shift adds; Ctrl toggles; drag empty space to box-select. G / R / S transforms.");
        }
        finally { synchronizingAiPath = false; }
    }
    private async void AiPathActionClick(object sender, RoutedEventArgs e) => await ReportUi(async () =>
    {
        string action = (sender as FrameworkElement)?.Tag as string ?? "";
        if (action == "confirm") await ApplyAiPathAsync(CancellationToken.None);
        else if (action == "cancel") CancelAiPathDraft();
        else if (action == "properties") await OpenAiPathPropertiesAsync();
        else BeginAiPathTransform(action);
        UpdateAiPathPanel();
    });
    private void AiPathInputChanged(object sender, RoutedEventArgs e)
    {
        if (synchronizingAiPath || aiPathDraft == null) return;
        try
        {
            RequireAiPathDraft(); aiPathDraft.Input = [AiPathX.Text, AiPathY.Text, AiPathZ.Text];
            aiPathDraft.Axis = AiPathAxis.SelectedIndex switch { 1 => "x", 2 => "y", 3 => "z", _ => "all" };
            scene!.SetAiPathPointerTransform(false); PreviewAiPathDraft();
        }
        catch (StudioCommandException ex) { AiPathValidation.Text = ex.Message; }
    }
    private bool AiPathKeyboard(KeyEventArgs e)
    {
        if (!IsAiWorld || CameraInputIsNative(e.OriginalSource as DependencyObject) || CameraInputIsNative(System.Windows.Input.Keyboard.FocusedElement as DependencyObject)
            || System.Windows.Input.Keyboard.Modifiers != ModifierKeys.None) return false;
        if (scene?.AiPathEditingNetwork == null) return false;
        if (e.Key is not (Key.G or Key.R or Key.S or Key.X or Key.Y or Key.Z or Key.Enter or Key.Escape)) return false;
        e.Handled = true; if (e.IsRepeat) return true;
        _ = ReportUi(async () =>
        {
            if (e.Key == Key.Escape) { if (scene!.CancelAiPathBox()) return; if (HasAiPathDraft) CancelAiPathDraft(); else scene.SetAiPathEditing(null); }
            else if (e.Key == Key.Enter && HasAiPathDraft) await ApplyAiPathAsync(CancellationToken.None);
            else if (e.Key is Key.G or Key.R or Key.S) BeginAiPathTransform(e.Key == Key.G ? "move" : e.Key == Key.R ? "rotate" : "scale", true);
            else if (aiPathDraft is { } draft && e.Key is Key.X or Key.Y or Key.Z)
            { RequireAiPathDraft(); draft.Axis = e.Key.ToString().ToLowerInvariant(); PreviewAiPathDraft(); UpdateAiPathPanel(); }
        }); return true;
    }
    private async Task OpenAiPathPropertiesAsync()
    {
        RequireNoDrafts(); RequireAiPathEditable();
        if (scene!.SelectedAiPathNodes.Count == 1) { await OpenAiPropertiesAsync(scene.SelectedAiPathNodes.First(), false); return; }
        var graph = scene.AiNetworks;
        var nodes = scene.SelectedAiPathNodes.Select(id => graph.Find(id) ?? throw new StudioCommandException("stale_record", "Node unavailable.")).ToArray();
        if (nodes.Length == 0) throw new StudioCommandException("selection_required", "Select nodes before opening Properties.");
        ++propertyRequest; // Supersede older asynchronous asset/property loads, as single-node Properties does.
        var first = nodes[0];
        JsonObject data = new()
        {
            ["ai_snapshot"] = graph.Id, ["snapshot_status"] = "Pinned selection; reopen after changing selection or editing.",
            ["Selected nodes"] = nodes.Length, ["Network"] = ShortAiText(first.Network.Name), ["Type"] = ShortAiText(first.Network.Type),
            ["Member"] = first.Network.Member, ["Path width"] = first.Network.PathWidth, ["Attack strategy"] = AiStrategyText(first.Network.AttackStrategy),
            ["Raw node value"] = nodes.All(n => n.Node.RawValue == first.Node.RawValue) ? JsonValue.Create(first.Node.RawValue) : JsonValue.Create("Mixed"),
            ["X"] = nodes.All(n => n.Node.Position.X == first.Node.Position.X) ? JsonValue.Create(first.Node.Position.X) : JsonValue.Create("Mixed"),
            ["Y"] = nodes.All(n => n.Node.Position.Y == first.Node.Position.Y) ? JsonValue.Create(first.Node.Position.Y) : JsonValue.Create("Mixed"),
            ["Z"] = nodes.All(n => n.Node.Position.Z == first.Node.Position.Z) ? JsonValue.Create(first.Node.Position.Z) : JsonValue.Create("Mixed"),
            ["node_ids"] = new JsonArray(nodes.Take(256).Select(n => (JsonNode?)JsonValue.Create(n.Node.Id)).ToArray()), ["node_ids_truncated"] = nodes.Length > 256
        };
        var window = GetPropertiesWindow();
        bool accepted = window.SetReadOnly(shownDocument!, $"AI path · {nodes.Length} nodes · {first.Network.Member}", data);
        if (accepted) { aiPropertiesArchive = first.Network.Archive; aiPropertiesStamp = ReadAiStamp(first.Network.Archive); }
        PresentProperties(window, accepted);
    }
}
