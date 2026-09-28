using System.Globalization;
using System.Numerics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

internal sealed partial class SceneInspectionCard
{
    internal const string AuthoredRotation = "Authored rotation XYZ (degrees)";
    internal const string AuthoredHeading = "Heading degrees";
    private readonly StackPanel modes = new() { Orientation = Orientation.Horizontal, Margin = new(0, 3, 0, 0) };
    private ToggleButton moveMode = null!, rotateMode = null!;
    private PlacementTransform originalTransform, lastValidTransform;
    private PlacementRotationKind rotationKind;
    private string transformMode = "move";
    private string[] initialAngles = [];
    private bool changingDraft;
    private ValueTextBox[] Angles => values.TryGetValue(rotationKind == PlacementRotationKind.HeadingDegrees ? AuthoredHeading : AuthoredRotation, out var angleField) ? angleField.Inputs : [];
    private IEnumerable<SceneInspectionField> EditableFields => values.Values.Where(f => f.Binding != SceneInspectionBinding.None);
    private IEnumerable<string> DraftStrings => HasDraft ? Coordinates.Concat(Angles).Select(c => c.Text) : [];

    private void InitializeTransformControls()
    {
        ToggleButton Mode(string label, string kind, string mode)
        {
            ToggleButton button = new() { Content = new PreviewIcon { Kind = kind }, ToolTip = label };
            button.SetResourceReference(StyleProperty, "PreviewIconToggle");
            AutomationProperties.SetName(button, label);
            button.Click += (_, _) => { Try(() => SetDraft(DraftToken, mode: mode)); RefreshModeControls(); };
            modes.Children.Add(button); return button;
        }
        moveMode = Mode("Move object", "Move", "move"); rotateMode = Mode("Rotate object", "Rotate", "rotate");
        viewport.TransformDraftChanged += ReceiveTransformDrag;
    }
    private void RefreshModeControls()
    {
        modes.Visibility = !values.ContainsKey(AuthoredPlacement) ? Visibility.Collapsed : HasDraft ? Visibility.Visible : Visibility.Hidden;
        moveMode.IsEnabled = HasDraft;
        rotateMode.IsEnabled = HasDraft && rotationKind != PlacementRotationKind.None;
        rotateMode.ToolTip = rotationKind == PlacementRotationKind.None ? "This placement has no authored rotation" :
            rotationKind == PlacementRotationKind.HeadingDegrees ? "Rotate object · Y heading only" : "Rotate object · X, Y and Z";
        ToolTipService.SetShowOnDisabled(rotateMode, true);
        moveMode.IsChecked = transformMode == "move"; rotateMode.IsChecked = transformMode == "rotate";
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
        if (!HasDraft) return;
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
        if (headingDegrees?.Length > 64 || mode != null && mode is not ("move" or "rotate")) throw new StudioCommandException("invalid_argument", "Invalid heading or transform mode.");
        if (mode == "rotate" && rotationKind == PlacementRotationKind.None) throw new StudioCommandException("read_only", "This placement has no authored rotation.");
        changingDraft = true;
        try
        {
            if (xyz != null) for (int i = 0; i < 3; i++) if (Coordinates[i].Text != xyz[i]) Coordinates[i].Text = xyz[i];
            if (rotationDegrees != null) for (int i = 0; i < 3; i++) if (Angles[i].Text != rotationDegrees[i]) Angles[i].Text = rotationDegrees[i];
            if (headingDegrees != null && Angles[0].Text != headingDegrees) Angles[0].Text = headingDegrees;
            if (mode != null) transformMode = mode;
        }
        finally { changingDraft = false; }
        RefreshDraftPreview();
    }
}
