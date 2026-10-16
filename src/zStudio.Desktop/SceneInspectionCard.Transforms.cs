using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using zStudio.Automation;
using zStudio.Core;

namespace zStudio.Desktop;

internal sealed partial class SceneInspectionCard
{
    internal const string AuthoredRotation = "Authored rotation XYZ (degrees)";
    internal const string AuthoredHeading = "Heading degrees";
    private readonly StackPanel modes = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private ToggleButton moveMode = null!, rotateMode = null!;
    private Button resetTransform = null!;
    private PlacementTransform originalTransform, lastValidTransform;
    private PlacementRotationKind rotationKind;
    private string transformMode = "move";
    /// <summary>Whether the Move or Rotate handles are shown: each toggle shows its handles or hides them again.</summary>
    private bool handlesShown;
    private string[] initialAngles = [];
    private bool changingDraft;
    private ValueTextBox[] Angles => values.TryGetValue(rotationKind == PlacementRotationKind.HeadingDegrees ? AuthoredHeading : AuthoredRotation, out var angleField) ? angleField.Inputs : [];
    private IEnumerable<SceneInspectionField> EditableFields => values.Values.Where(f => f.Binding != SceneInspectionBinding.None);
    private IEnumerable<string> DraftStrings => HasDraft ? Coordinates.Concat(Angles).Select(c => c.Text) : [];

    private void InitializeTransformControls()
    {
        ToggleButton Mode(string label, string kind, string mode)
        {
            ToggleButton button = new() { Content = new PreviewIcon { Kind = kind, Width = 16, Height = 16 }, ToolTip = label };
            button.SetResourceReference(StyleProperty, "CardSegmentToggle");
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => { Try(() => ToggleHandles(mode)); RefreshModeControls(); };
            modes.Children.Add(button); return button;
        }
        moveMode = Mode("Move object", "Move", "move"); rotateMode = Mode("Rotate object", "Rotate", "rotate");
        Button reset = new() { Content = new PreviewIcon { Kind = "Reset", Width = 14, Height = 14 }, ToolTip = "Reset rotation and scale", Margin = new(4, 0, 0, 0) };
        reset.SetResourceReference(StyleProperty, "CardIconButton");
        reset.Width = reset.Height = 30;
        AutomationProperties.SetName(reset, "Reset rotation and scale");
        reset.Click += (_, _) => Try(() => { if (ObjectEditor is { IsEnabled: true } editor && !ObjectApplying) editor.ResetRotationAndScale(); });
        transformTools.Children.Add(reset); resetTransform = reset;
        viewport.TransformDraftChanged += ReceiveTransformDrag;
        viewport.ObjectDraftDragged += ReceiveObjectDrag;
        viewport.ObjectGestureEnded += ObjectGestureEnded;
    }
    /// <summary>
    /// The transform header's toggles: Move or Rotate shows those handles, the same one again hides them. A placement's handles
    /// belong to a draft, which showing them starts (and hiding them ends while it changes nothing).
    /// </summary>
    internal void ToggleHandles(string mode)
    {
        if (mode is not ("move" or "rotate" or "none")) throw new StudioCommandException("invalid_argument", "Invalid transform mode.");
        bool show = mode != "none" && !(handlesShown && transformMode == mode);
        if (ObjectEditor != null) { SetObjectMode(show ? mode : "none"); return; }
        if (show && mode == "rotate" && rotationKind == PlacementRotationKind.None && HasDraft) throw new StudioCommandException("read_only", "This placement has no authored rotation.");
        if (viewport.IsPickupDragging) throw new StudioCommandException("busy", "Finish or cancel the active transform drag.");
        if (!show)
        {
            handlesShown = false; viewport.TransformHandlesEnabled = false;
            if (HasDraft && !HasChanges) CancelDraft();
            RefreshModeControls(); return;
        }
        if (!HasDraft) begin(this);
        if (!HasDraft) return;
        if (mode == "rotate" && rotationKind == PlacementRotationKind.None) throw new StudioCommandException("read_only", "This placement has no authored rotation.");
        handlesShown = true; transformMode = mode; viewport.TransformHandlesEnabled = true;
        RefreshDraftPreview();
    }
    /// <summary>The first input into an editable placement starts its draft, keeping what was typed.</summary>
    private void BeginFromInput()
    {
        string[] typed = Coordinates.Select(c => c.Text).ToArray(), turned = Angles.Select(c => c.Text).ToArray();
        var kind = rotationKind;
        Try(() =>
        {
            begin(this);
            if (!HasDraft) return;
            SetDraft(DraftToken, typed, rotationKind == PlacementRotationKind.EulerRadians && turned.Length == 3 ? turned : null,
                rotationKind == PlacementRotationKind.HeadingDegrees && turned.Length == 1 ? turned[0] : null);
        });
    }
    private void RefreshModeControls()
    {
        // The toggles are in the transform (or details) header for anything with handles; rotation needs authored angles.
        // Before a draft, an authored rotation or heading field says whether the placement can turn.
        bool sourceObject = ObjectEditor != null, rotatable = sourceObject || (HasDraft ? rotationKind != PlacementRotationKind.None : values.ContainsKey(AuthoredRotation) || values.ContainsKey(AuthoredHeading));
        bool editable = sourceObject ? ObjectEditor!.CanEdit && ObjectEditor.State.Transform != null : HasDraft || details["Editable"]?.GetValue<bool>() == true;
        modes.Visibility = sourceObject ? (editable ? Visibility.Visible : Visibility.Collapsed)
            : !values.ContainsKey(AuthoredPlacement) ? Visibility.Collapsed : editable ? Visibility.Visible : Visibility.Hidden;
        modesFrame.Visibility = modes.Visibility;
        moveMode.IsEnabled = editable && !ObjectApplying;
        resetTransform.IsEnabled = sourceObject && editable && !ObjectApplying && ObjectEditor!.IsEnabled;
        // A placement without authored rotation has only Move: the segmented control shrinks to it.
        rotateMode.Visibility = !editable || rotatable ? Visibility.Visible : Visibility.Collapsed;
        rotateMode.IsEnabled = editable && rotatable && !ObjectApplying;
        rotateMode.ToolTip = sourceObject ? "Rotate object · X, Y and Z" : rotationKind == PlacementRotationKind.None ? "This placement has no authored rotation" :
            rotationKind == PlacementRotationKind.HeadingDegrees ? "Rotate object · Y heading only" : "Rotate object · X, Y and Z";
        ToolTipService.SetShowOnDisabled(rotateMode, true);
        moveMode.IsChecked = handlesShown && transformMode == "move"; rotateMode.IsChecked = handlesShown && transformMode == "rotate";
        viewport.TransformHandlesEnabled = handlesShown;
    }
    internal PlacementTransform DraftTransform()
    {
        var rotation = originalTransform.Rotation;
        for (int i = 0; i < Angles.Length; i++)
        {
            if (Angles[i].Text == initialAngles[i]) continue; // Never round untouched radians through displayed degrees.
            if (!double.TryParse(Angles[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double angle) || !double.IsFinite(angle))
                throw new StudioCommandException("invalid_argument", "Rotation must contain finite angles in degrees.");
            float stored = (float)(rotationKind == PlacementRotationKind.EulerRadians ? angle * (Math.PI / 180) : angle);
            if (!float.IsFinite(stored)) throw new StudioCommandException("invalid_argument", "Rotation must fit finite game floats.");
            rotation[rotationKind == PlacementRotationKind.HeadingDegrees ? 1 : i] = stored;
        }
        return new(DraftPosition(), rotation);
    }
    private void RefreshDraftPreview()
    {
        if (!HasDraft || changingDraft) return;
        try
        {
            if (DraftDocument?.Revision != DraftRevision) throw new InvalidOperationException("The document changed.");
            var value = DraftTransform();
            viewport.PreviewTransformDraft(DraftSource!, value, rotationKind, transformMode, true);
            lastValidTransform = value;
        }
        catch (Exception ex) when (ex is StudioCommandException or InvalidOperationException or System.IO.InvalidDataException)
        { viewport.PreviewTransformDraft(DraftSource!, lastValidTransform, rotationKind, transformMode, false); }
        RefreshModeControls();
    }
    private static string AngleText(float value, PlacementRotationKind kind) =>
        (kind == PlacementRotationKind.EulerRadians ? value * (180 / Math.PI) : value).ToString("R", CultureInfo.InvariantCulture);

    private void ReceiveTransformDrag(PlacementTransform value)
    {
        if (DraftSource == null) return;
        changingDraft = true;
        try
        {
            for (int i = 0; i < 3; i++)
                if (value.Position[i] != lastValidTransform.Position[i]) Coordinates[i].Text = value.Position[i].ToString("R", CultureInfo.InvariantCulture);
            for (int i = 0; i < Angles.Length; i++)
            {
                int axis = rotationKind == PlacementRotationKind.HeadingDegrees ? 1 : i;
                if (value.Rotation[axis] != lastValidTransform.Rotation[axis]) Angles[i].Text = AngleText(value.Rotation[axis], rotationKind);
            }
        }
        finally { changingDraft = false; }
        RefreshDraftPreview();
    }

    internal void SetDraft(string token, IReadOnlyList<string>? xyz = null, IReadOnlyList<string>? rotationDegrees = null, string? headingDegrees = null, string? mode = null)
    {
        RequireDraft(token);
        if (viewport.IsPickupDragging) throw new StudioCommandException("busy", "Finish or cancel the active transform drag.");
        if (xyz == null && rotationDegrees == null && headingDegrees == null && mode == null)
            throw new StudioCommandException("invalid_argument", "Supply position, a supported rotation, or transformMode.");
        static void Bounded(IReadOnlyList<string>? values)
        { if (values != null && (values.Count != 3 || values.Any(v => v == null || v.Length > 64))) throw new StudioCommandException("invalid_argument", "Supply three bounded input strings."); }
        Bounded(xyz); Bounded(rotationDegrees);
        if (rotationDegrees != null && rotationKind != PlacementRotationKind.EulerRadians || headingDegrees != null && rotationKind != PlacementRotationKind.HeadingDegrees)
            throw new StudioCommandException("read_only", "The selected placement does not support those rotation axes.");
        if (headingDegrees?.Length > 64 || mode != null && mode is not ("move" or "rotate" or "none")) throw new StudioCommandException("invalid_argument", "Invalid heading or transform mode.");
        if (mode == "rotate" && rotationKind == PlacementRotationKind.None) throw new StudioCommandException("read_only", "This placement has no authored rotation.");
        changingDraft = true;
        try
        {
            if (xyz != null) for (int i = 0; i < 3; i++) if (Coordinates[i].Text != xyz[i]) Coordinates[i].Text = xyz[i];
            if (rotationDegrees != null) for (int i = 0; i < 3; i++) if (Angles[i].Text != rotationDegrees[i]) Angles[i].Text = rotationDegrees[i];
            if (headingDegrees != null && Angles[0].Text != headingDegrees) Angles[0].Text = headingDegrees;
            if (mode == "none") handlesShown = false;
            else if (mode != null) { transformMode = mode; handlesShown = true; }
        }
        finally { changingDraft = false; }
        RefreshDraftPreview();
    }
}
