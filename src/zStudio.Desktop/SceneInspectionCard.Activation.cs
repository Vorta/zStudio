using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>
/// What an AI vehicle card's Activation section shows (design/activation-radius.md §6): the vehicle's state, its network (ID,
/// stem, number), <c>activate_rad</c> as stored (null when not stored), the shown difficulty's vehicles on that network, whether
/// its ring shows and can show, and why the field is read only (the network's file), or null.
/// </summary>
internal sealed record ActivationCardInfo(AiActivationState State, string? NetworkId, string? NetworkStem, int? NetworkNumber,
    string? Stored, int Vehicles, bool Shown, bool CanShow, string? Locked);

/// <summary>
/// An AI vehicle's Activation section: its ring's toggle, its network (a link to the network's card) and the network's Activation
/// Radius, a value of the placement draft (typed, or set by the ring handle's drag), which every shown ring of the network previews.
/// </summary>
internal sealed partial class SceneInspectionCard
{
    /// <summary>The selection's Activation section (null: none: not an AI vehicle, MW3, a compiled world).</summary>
    internal Func<SceneInspection, ActivationCardInfo?>? ActivationInfo { get; set; }
    /// <summary>The header toggle: shows (true) or hides the selected vehicle's ring.</summary>
    internal Action<SceneInspection, bool>? ActivationShownChanged { get; set; }
    /// <summary>The network link: selects the network's row (its card).</summary>
    internal Action<string>? ActivationNetworkInvoked { get; set; }

    /// <summary>What a refusal during a handle drag (<see cref="SceneViewport.IsHandleDragging"/>) says: <paramref name="message"/>, a transform drag's (or Fly's), unless the activation radius is dragged.</summary>
    internal static string DragRefusal(SceneViewport viewport, string message) => viewport.IsActivationRadiusDragging ? ActivationDragRefusal : message;
    internal const string ActivationDragRefusal = "Finish or cancel the activation radius drag first.";
    internal const string NoActivationRadius = "This placement has no activation radius: only a RECOIL AI vehicle on an AI network has one.";
    /// <summary>The details scene_inspect reports for an AI vehicle; the Activation section shows them, not Details.</summary>
    internal const string ActivationNetworkDetail = "AI network", ActivationRadiusDetail = "Activation radius";
    private const double ActivationLabelWidth = 116;
    private readonly CardSection activationSection = new("Activation") { Visibility = Visibility.Collapsed };
    private readonly ToggleButton activationToggle = new() { Content = new PreviewIcon { Kind = "ActivationRadius", Width = 16, Height = 16 }, Width = 30, Height = 30 };
    private readonly Grid activationNetworkRow = new() { MinHeight = 28, Margin = new(0, 0, 0, 4) }, activationFieldRow = new() { MinHeight = 32 };
    private readonly TextBlock activationNetwork = new() { FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly ValueTextBox activationField = new() { IsReadOnly = true, Height = 32, MaxLength = 64, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Center, Padding = new(10, 0, 10, 0) };
    private readonly TextBlock activationHint = new() { Text = "Not stored", IsHitTestVisible = false, Margin = new(11, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock activationHelp = ActivationLine("Wakes when the player comes this close (horizontal distance). 0 or not stored: only when alerted."),
        activationScope = ActivationLine(""), activationLocked = ActivationLine(""), activationMessage = ActivationLine("");
    /// <summary>The footer's note while the draft changes the radius: the edit changes the whole network.</summary>
    private readonly TextBlock footerScope = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 5), Visibility = Visibility.Collapsed };
    private ActivationCardInfo? activation;
    /// <summary>Why the field is read only (the network's file, or the placement), or null.</summary>
    private string? activationLock;
    /// <summary>The draft holds the field: the stored text it started from and its network.</summary>
    private bool activationPinned;
    private string activationInitial = "";
    private string? activationDraftNetwork;
    private (string Network, float Radius)? activationPreview;
    /// <summary>A handle drag: the text and preview before it and whether it started the draft.</summary>
    private string? activationDragStart;
    private (string Network, float Radius)? activationDragPreview;
    private bool activationDragDraft, changingActivation;
    private string? activationError;

    /// <summary>The section's field takes input (its ring carries the edit handle while shown).</summary>
    internal bool ActivationEditable => activation is { State: AiActivationState.Radius or AiActivationState.Asleep, NetworkId: not null } && activationLock == null;
    /// <summary>The selected vehicle's network (its shown rings are emphasised), or null.</summary>
    internal string? ActivationNetwork => activation?.NetworkId;
    /// <summary>The draft radius the shown rings of its network draw, or null.</summary>
    internal (string Network, float Radius)? ActivationPreview => activationPreview;
    /// <summary>The radius the draft changes: the network's ID and the text to store; null while it is as stored.</summary>
    internal (string Network, string Text)? ActivationDraft => ActivationChanged && activationDraftNetwork is { } network ? (network, activationField.Text.Trim()) : null;
    private bool ActivationChanged => HasDraft && activationPinned && activationField.Text.Trim() != activationInitial;

    private static TextBlock ActivationLine(string text)
    {
        TextBlock line = new() { Text = text, FontSize = 11.5, LineHeight = 15, TextWrapping = TextWrapping.Wrap, Margin = new(0, 3, 0, 4) };
        line.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        return line;
    }
    private void InitializeActivationControls()
    {
        activationToggle.SetResourceReference(StyleProperty, "CardSegmentToggle");
        ToolTipService.SetInitialShowDelay(activationToggle, 0);
        activationToggle.Click += (_, _) =>
        {
            if (Selection is { } selected && activation is { CanShow: true } shown) Try(() => ActivationShownChanged?.Invoke(selected, !shown.Shown));
            Refresh();
        };
        activationSection.Trailing = activationToggle;
        Grid Row(Grid row, string caption, UIElement value)
        {
            row.ColumnDefinitions.Add(new() { Width = new(ActivationLabelWidth) }); row.ColumnDefinitions.Add(new());
            TextBlock label = new() { Text = caption, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 8, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            row.Children.Add(label); Grid.SetColumn(value, 1); row.Children.Add(value);
            return row;
        }
        activationHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
        Grid box = new(); box.Children.Add(activationField); box.Children.Add(activationHint);
        AutomationProperties.SetName(activationField, "Activation Radius");
        ToolTipService.SetShowOnDisabled(activationField, true);
        activationField.TextChanged += (_, _) =>
        {
            activationHint.Visibility = activationField.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (changingActivation) return;
            // Typing into an editable vehicle's radius starts its placement draft, keeping what was typed.
            if (!HasDraft) { if (!refreshingRows && ActivationEditable && details["Editable"]?.GetValue<bool>() == true) BeginActivationFromInput(); return; }
            ActivationEdited();
        };
        activationField.PreviewKeyDown += (_, e) =>
        {
            if (!HasDraft || activationField.IsReadOnly) return;
            // Escape restores what is stored (ending a draft that then changes nothing); Enter applies the draft.
            if (e.Key == Key.Escape) { SetActivationText(activationInitial); ActivationEdited(); if (!HasChanges) CancelDraft(); e.Handled = true; }
            else if (e.Key == Key.Enter) { Try(Apply); e.Handled = true; }
        };
        activationSection.Body.Children.Add(activationMessage);
        activationSection.Body.Children.Add(Row(activationNetworkRow, "AI network", activationNetwork));
        activationSection.Body.Children.Add(Row(activationFieldRow, "Activation Radius", box));
        activationSection.Body.Children.Add(activationHelp); activationSection.Body.Children.Add(activationScope); activationSection.Body.Children.Add(activationLocked);
        activationSection.Body.Margin = new(12, 2, 12, 10);
        footerScope.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        viewport.ActivationRadiusDragged += ReceiveActivationDrag;
        viewport.ActivationRadiusDragEnded += ActivationDragEnded;
    }

    /// <summary>Shows the selection's Activation section (or none); a draft keeps the field as typed.</summary>
    private void RefreshActivation(SceneInspection? selected)
    {
        var info = selected == null || ObjectEditor != null || selected.AiNode != null ? null : ActivationInfo?.Invoke(selected);
        activation = info;
        activationSection.Visibility = info == null ? Visibility.Collapsed : Visibility.Visible;
        if (info == null) { activationLock = null; activationField.IsReadOnly = true; return; }
        bool field = info.State is AiActivationState.Radius or AiActivationState.Asleep && info.NetworkId != null;
        string stem = info.NetworkStem ?? "";
        activationMessage.Visibility = field ? Visibility.Collapsed : Visibility.Visible;
        activationMessage.Text = info.State == AiActivationState.NoNetwork ? $"Network {info.NetworkNumber}: this vehicle has no AI network, so it has no activation radius."
            : $"{NetworkFile(info.NetworkNumber)} is not in this mission: the vehicle stays inert.";
        foreach (var part in new FrameworkElement[] { activationNetworkRow, activationFieldRow, activationHelp, activationScope }) part.Visibility = field ? Visibility.Visible : Visibility.Collapsed;
        activationToggle.Visibility = field && info.CanShow ? Visibility.Visible : Visibility.Collapsed;
        activationSection.Summary = "";
        if (!field) { activationLock = null; activationField.IsReadOnly = true; activationLocked.Visibility = Visibility.Collapsed; return; }
        float stored = ParseRadius(info.Stored) is float value ? Math.Abs(value) : 0;
        activationToggle.IsChecked = info.Shown;
        string tip = SceneTreeItem.ActivationToolTip(info.Shown, info.State, stem, stored);
        activationToggle.ToolTip = tip; AutomationProperties.SetName(activationToggle, tip);
        // The link, then the network's attack strategy (its rings' colour); built again only when they change.
        string networkId = info.NetworkId!;
        var network = viewport.AiNetworks.Networks.FirstOrDefault(n => n.Id == networkId);
        var kind = network == null ? (AiAttackStrategyKind?)null : network.AttackStrategy.Kind == AiAttackStrategyKind.Unknown ? AiAttackStrategyKind.HEA : network.AttackStrategy.Kind;
        if (activationLinkKey != (networkId, stem, kind)) ShowActivationLink(networkId, stem, kind);
        activationScope.Text = $"Stored in {stem}: every vehicle of that network uses it ({info.Vehicles.ToString("N0", CultureInfo.CurrentCulture)} here).";
        // Read only while the network's file refuses the edit or the placement cannot be edited; the toggle still works.
        activationLock = info.Locked ?? (HasDraft || details["Editable"]?.GetValue<bool>() == true ? null : details["Placement editing"] is { } reason ? Display(reason)
            : "This vehicle's placement can't be edited now.");
        activationField.IsReadOnly = activationLock != null; activationField.Opacity = activationLock != null ? .75 : 1;
        activationField.ToolTip = activationLock;
        activationLocked.Text = $"Edit it on {stem}'s card."; activationLocked.Visibility = activationLock != null ? Visibility.Visible : Visibility.Collapsed;
        if (!(HasDraft && activationPinned)) SetActivationText(info.Stored ?? "");
        RefreshActivationSummary();
    }
    private (string Network, string Stem, AiAttackStrategyKind? Kind)? activationLinkKey;
    private void ShowActivationLink(string networkId, string stem, AiAttackStrategyKind? kind)
    {
        activationLinkKey = (networkId, stem, kind);
        activationNetwork.Inlines.Clear();
        Hyperlink link = new(new Run(stem)) { ToolTip = $"Select {stem}: its card edits the whole network" };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
        AutomationProperties.SetName(link, "Select " + stem);
        link.Click += (_, _) => ActivationNetworkInvoked?.Invoke(networkId);
        activationNetwork.Inlines.Add(link);
        if (kind is not { } strategy) return;
        Run text = new("  · " + OutlinerKinds.StrategyName(strategy).ToLowerInvariant()) { FontSize = 12 };
        text.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        activationNetwork.Inlines.Add(text);
    }
    private static string NetworkFile(int? number) => $"net_{(number ?? 0).ToString("00", CultureInfo.InvariantCulture)}.zrd";
    private static float? ParseRadius(string? text) => float.TryParse(text?.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) && float.IsFinite(value) ? value : null;
    /// <summary>The collapsed summary: the radius (the field's, as typed) and the network, or that it wakes only when alerted.</summary>
    private void RefreshActivationSummary()
    {
        if (activation is not { NetworkStem: { } stem } info) return;
        float radius = ParseRadius(activationField.Text) is float typed ? Math.Abs(typed) : info.State == AiActivationState.Radius ? Math.Abs(ParseRadius(info.Stored) ?? 0) : 0;
        activationSection.Summary = radius > 0 ? $"{SceneTreeItem.RadiusText(radius)} · {stem}" : $"wakes only when alerted · {stem}";
    }
    private void SetActivationText(string text)
    {
        changingActivation = true;
        try { if (activationField.Text != text) activationField.Text = text; }
        finally { changingActivation = false; }
    }
    /// <summary>The first input into the radius starts the placement draft, keeping what was typed.</summary>
    private void BeginActivationFromInput()
    {
        string typed = activationField.Text;
        Try(() =>
        {
            begin(this);
            if (!HasDraft || !activationPinned) { if (!HasDraft) SetActivationText(activation?.Stored ?? ""); return; }
            SetActivationText(typed); ActivationEdited();
        });
    }
    /// <summary>The draft is started (<see cref="StartDraft"/>): the field belongs to it from now on.</summary>
    private void PinActivation()
    {
        activationPinned = ActivationEditable; activationDraftNetwork = activationPinned ? activation!.NetworkId : null;
        activationInitial = activationPinned ? (activation!.Stored ?? "").Trim() : "";
    }
    /// <summary>The draft ended: the field shows what is stored again and the rings their stored radius.</summary>
    private void UnpinActivation()
    {
        // The handle's gesture belongs to the draft: discarding it (Revert, scene_card cancel, resolve_drafts discard) ends the drag
        // too, as it ends a held transform drag. Forgetting the gesture first keeps the cancel from restoring into a closed draft.
        if (viewport.IsActivationRadiusDragging) { activationDragStart = null; viewport.CancelActivationRadiusDrag(); }
        activationPinned = false; activationDraftNetwork = null; activationDragStart = null; activationDragPreview = null;
        ShowActivationProblem(null);
        if (activationPreview != null) { activationPreview = null; viewport.SetActivationRadiusPreview(null, null); }
        footerScope.Visibility = Visibility.Collapsed;
    }
    /// <summary>
    /// The radius changed in the draft: the shown rings of its network preview a valid number, a refused one says why. A field
    /// back at its stored text previews nothing (the rings draw what is stored); a changed text that does not parse yet (<c>-</c>,
    /// <c>3.</c>) keeps the last preview.
    /// </summary>
    private void ActivationEdited()
    {
        if (!HasDraft || !activationPinned || activationDraftNetwork is not { } network) return;
        string text = activationField.Text.Trim();
        var before = activationPreview;
        if (!ActivationChanged) activationPreview = null;
        else if (ParseRadius(text) is float value) activationPreview = (network, Math.Abs(value));
        if (activationPreview != before) PushActivationPreview();
        ShowActivationProblem(ActivationProblem());
        RefreshActivationSummary();
        RefreshFooter(true);
    }
    /// <summary>Why Apply would refuse the changed radius (the network card's <c>activate_rad</c> rule), or null.</summary>
    private string? ActivationProblem()
    {
        if (!ActivationChanged) return null;
        try { SourceAiNetworkEdits.Check(SourceAiNetworkEdits.FieldOf("activate_rad")!, [activationField.Text.Trim()]); return null; }
        catch (System.IO.InvalidDataException ex) { return ex.Message; }
    }
    /// <summary>
    /// After a card action that cleared the error line and succeeded (the Activation toggle, the Move/Rotate toggles, a copy):
    /// a refused radius still in the draft says why again.
    /// </summary>
    private void RestoreActivationProblem()
    {
        if (error.Visibility == Visibility.Visible) return;
        activationError = null;
        ShowActivationProblem(ActivationProblem());
    }
    private void PushActivationPreview() => viewport.SetActivationRadiusPreview(activationPreview?.Network, activationPreview?.Radius);
    private void ShowActivationProblem(string? problem)
    {
        if (problem != null) { error.Text = problem; error.Visibility = Visibility.Visible; activationError = problem; }
        else if (activationError != null) { if (error.Text == activationError) { error.Text = ""; error.Visibility = Visibility.Collapsed; } activationError = null; }
    }
    /// <summary>Apply refuses a radius the network's edit would refuse, with its message.</summary>
    internal void RequireValidActivation()
    {
        if (ActivationDraft is not { } draft) return;
        try { SourceAiNetworkEdits.Check(SourceAiNetworkEdits.FieldOf("activate_rad")!, [draft.Text]); }
        catch (System.IO.InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
    }
    /// <summary>The footer's note while the radius is changed.</summary>
    private void RefreshActivationFooter()
    {
        bool changed = ActivationChanged && activation?.NetworkStem is not null;
        footerScope.Text = changed ? $"Also changes {activation!.NetworkStem} (every vehicle of that network)." : "";
        footerScope.Visibility = changed ? Visibility.Visible : Visibility.Collapsed;
    }
    /// <summary>MCP set: the radius text (bounded), only on a vehicle whose field is editable.</summary>
    private void CheckActivationInput(string? text)
    {
        if (text == null) return;
        if (text.Length > 64) throw new StudioCommandException("invalid_argument", "Supply an activation radius of at most 64 characters.");
        if (!activationPinned) throw new StudioCommandException(activation is { State: AiActivationState.Radius or AiActivationState.Asleep } && activationLock != null ? "read_only" : "unsupported",
            activationLock ?? NoActivationRadius);
    }
    private object? DescribeActivationDraft() => activation is { State: AiActivationState.Radius or AiActivationState.Asleep, NetworkId: { } network } info ? new
    {
        network = JsonData.ShownText(activationPinned ? activationDraftNetwork! : network, 512), value = activationField.Text,
        stored = info.Stored is { } stored ? JsonData.ShownText(stored, 256) : null, changed = ActivationChanged, editable = ActivationEditable,
    } : null;

    // ---------------------------------------------------------------- the ring handle's drag

    private void ReceiveActivationDrag(float radius)
    {
        if (!ActivationEditable || Selection == null) return;
        if (activationDragStart == null) { activationDragStart = activationField.Text; activationDragPreview = activationPreview; activationDragDraft = !HasDraft; }
        if (!HasDraft)
        {
            Try(() => begin(this));
            if (!HasDraft || !activationPinned) { activationDragStart = null; viewport.CancelActivationRadiusDrag(); return; }
        }
        // Written as the shipped files write distances: one decimal, invariant.
        SetActivationText(Math.Round((double)radius, 1, MidpointRounding.AwayFromZero).ToString("0.0", CultureInfo.InvariantCulture));
        ActivationEdited();
    }
    /// <summary>
    /// A released drag keeps what it wrote; a cancelled one restores the text and the preview from before it (an incomplete text
    /// keeps the preview it had; the stored text has none) and ends a draft it started that changes nothing.
    /// </summary>
    private void ActivationDragEnded(bool released)
    {
        if (activationDragStart is not { } before) return;
        activationDragStart = null;
        if (released || !HasDraft) return;
        SetActivationText(before);
        activationPreview = activationDragPreview is { } kept && kept.Network == activationDraftNetwork ? kept : null;
        PushActivationPreview(); ActivationEdited();
        if (activationDragDraft && !HasChanges) CancelDraft();
    }
}
