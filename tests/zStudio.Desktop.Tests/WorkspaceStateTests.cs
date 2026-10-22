using System.Globalization;
using System.IO;
using System.Text.Json;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Desktop.Tests;

public sealed class WorkspaceStateTests
{
    [Fact]
    public void InspectionPanelPreferenceDefaultsAndSurvivesSettingsRoundTrip()
    {
        var layout = JsonSerializer.Deserialize<WorkspaceLayout>("""{"Version":4,"ToolsHeight":260}""")!;
        layout.Normalize(); Assert.Equal(432, layout.InspectionPanelHeight); Assert.Equal(260, layout.ToolsHeight);
        layout.InspectionPanelHeight = 620;
        var restored = JsonSerializer.Deserialize<WorkspaceLayout>(JsonSerializer.Serialize(layout))!;
        restored.Normalize(); Assert.Equal(620, restored.InspectionPanelHeight);
        restored.InspectionPanelHeight = 12; restored.Normalize(); Assert.Equal(216, restored.InspectionPanelHeight);
        foreach (double invalid in new[] { double.NaN, double.NegativeInfinity, double.PositiveInfinity })
        {
            restored.InspectionPanelHeight = invalid; restored.Normalize(); Assert.Equal(432, restored.InspectionPanelHeight);
        }
    }
    [Fact]
    public void InvalidDraftNeverBecomesTheCommittedBaseline()
    {
        float stored = 1.2345678f; int commits = 0;
        string original = stored.ToString("R", CultureInfo.InvariantCulture);
        FieldDraft draft = new(original, text => { float value = float.Parse(text, CultureInfo.InvariantCulture); if (!float.IsFinite(value)) throw new InvalidDataException("Finite values only"); stored = value; commits++; });
        draft.Text = "1e"; Assert.False(draft.Commit()); Assert.Equal(original, draft.Committed); Assert.NotNull(draft.Error);
        draft.Refresh("0"); Assert.Equal("1e", draft.Text); Assert.Equal(1.2345678f, stored);
        draft.Discard(); Assert.Equal(original, draft.Text); Assert.Null(draft.Error);
        Assert.True(draft.Commit()); Assert.Equal(0, commits);
        draft.Text = "NaN"; Assert.False(draft.Commit()); draft.Text = "2.25";
        Assert.True(draft.Commit()); Assert.Equal(2.25f, stored); Assert.False(draft.IsPending); Assert.Null(draft.Error);
    }
    [Fact]
    public void CompoundDraftMakesOneCommitAndKeepsPartialInputLocal()
    {
        int commits = 0; string stored = "1, 2, 3";
        FieldDraft draft = new(stored, text => { var record = new AnimationRecord(new byte[12]); new AnimationField("Position", 0, AnimationFieldKind.Vector).Write(record, text); stored = text; commits++; });
        draft.Text = "4, , 3"; Assert.False(draft.Commit()); Assert.Equal("1, 2, 3", stored); Assert.Equal(0, commits);
        draft.Text = "4, 5, 6"; Assert.True(draft.Commit()); Assert.Equal(1, commits);
        Assert.True(draft.Commit()); Assert.Equal(1, commits);
    }
    [Fact]
    public void UnsupportedWorkspaceUsesDefaultsWithoutDiscardingCurrentGlobalSettings()
    {
        foreach (string workspace in new[] { "null", "{}", "{\"Version\":3,\"InspectorWidth\":410}", "{\"Version\":5,\"InspectorWidth\":410}" })
        {
            var settings = JsonSerializer.Deserialize<StudioSettings>($$"""{"Workspace":{{workspace}},"Theme":"Dark","LastRoot":"fixture","CreateBackupOnSave":true}""")!;
            var defaults = settings.GetWorkspace();
            Assert.Equal(4, defaults.Version); Assert.Equal(352, defaults.InspectorWidth);
            Assert.Equal("Dark", settings.Theme); Assert.Equal("fixture", settings.LastRoot);
            Assert.False(settings.McpEnabled);
        }
        var current = new StudioSettings(); var layout = current.GetWorkspace();
        Assert.False(current.McpEnabled);
        Assert.Equal(Path.Combine(zStudio.Automation.StudioProfile.Directory, "settings.json"), StudioSettings.SettingsPath);
        // Tests never write the user's own profile (PrivateProfile).
        Assert.True(zStudio.Automation.StudioProfile.IsPrivate);
        layout.NavigatorWidth = double.NaN; layout.InspectorWidth = 1; layout.ToolsHeight = double.PositiveInfinity; layout.Preset = "corrupt"; layout.Groups = null!;
        layout.Normalize(); Assert.Equal(294, layout.NavigatorWidth); Assert.Equal(320, layout.InspectorWidth); Assert.Equal(180, layout.ToolsHeight); Assert.Equal("Edit",layout.Preset); Assert.NotNull(layout.Groups);
        Assert.Equal(layout.InspectorWidth, JsonSerializer.Deserialize<StudioSettings>(JsonSerializer.Serialize(current))!.GetWorkspace().InspectorWidth);
    }
    [Fact]
    public void CurrentFloatingBoundsAndInspectorTabSurviveRoundTrip()
    {
        var layout = new WorkspaceLayout { InspectorTab = 2, PropertiesWindow = new() { Height = 830, Left = -100 } };
        var restored = JsonSerializer.Deserialize<WorkspaceLayout>(JsonSerializer.Serialize(layout))!;
        restored.Normalize(); Assert.Equal(2, restored.InspectorTab); Assert.Equal(830,restored.PropertiesWindow.Height); Assert.Equal(-100,restored.PropertiesWindow.Left);
        var invalid = new WorkspaceLayout { PropertiesWindow = new() { Height = double.NaN, Width = double.PositiveInfinity, Left = double.NaN, Top = double.PositiveInfinity }, InspectorTab = 99 };
        invalid.Normalize(); Assert.Equal(720,invalid.PropertiesWindow.Height); Assert.Equal(640, invalid.PropertiesWindow.Width); Assert.Null(invalid.PropertiesWindow.Left); Assert.Null(invalid.PropertiesWindow.Top); Assert.Equal(2,invalid.InspectorTab);
    }
    [Fact]
    public void ResponsiveFilesPreferencesRetainIndependentWidths()
    {
        var layout = JsonSerializer.Deserialize<WorkspaceLayout>("""{"Version":4,"NavigatorWidth":350,"BrowserTab":3,"ContentBrowserTab":3}""")!;
        layout.Normalize(); Assert.Equal(240,layout.FilesWidth); Assert.Equal(350,layout.NavigatorWidth); Assert.Equal(3,layout.ContentBrowserTab);
        layout.FilesWidth = 275; layout.BrowserTab = 0;
        var restored = JsonSerializer.Deserialize<WorkspaceLayout>(JsonSerializer.Serialize(layout))!;
        restored.Normalize(); Assert.Equal(275,restored.FilesWidth); Assert.Equal(350,restored.NavigatorWidth); Assert.Equal(0,restored.BrowserTab); Assert.Equal(3,restored.ContentBrowserTab);
        restored.FilesWidth = double.NaN; restored.Normalize(); Assert.Equal(240,restored.FilesWidth);
        restored.FilesWidth = 20; restored.Normalize(); Assert.Equal(200,restored.FilesWidth);
        restored.FilesWidth = 900; restored.Normalize(); Assert.Equal(650,restored.FilesWidth);
    }
    [Fact]
    public void EveryCatalogFieldHasAReachablePresentationGroup()
    {
        foreach (var spec in AnimationCatalog.Events)
        foreach (var field in spec.Fields)
        {
            Assert.False(string.IsNullOrWhiteSpace(AnimationFieldPresentation.Group(spec.Type,field)));
            if (field.ReadOnly) Assert.Equal("Serialized state / provenance",AnimationFieldPresentation.Group(spec.Type,field));
            if (field.Kind == AnimationFieldKind.Vector) Assert.Equal(3, AnimationFieldPresentation.Components(spec.Type,field).Length);
        }
        Assert.Equal("Additional stored fields", AnimationFieldPresentation.Group(255,new("Unmapped",12,AnimationFieldKind.Float)));
        Assert.Equal(new[] { "Start","End","Rate" },AnimationFieldPresentation.Components(21,AnimationCatalog.Find(21)!.Fields.Single(f => f.Offset == 20)));
    }
    [Fact]
    public void GridAndCollisionAreIndependentDefaults()
    {
        AnimationPreviewOptions options = new(); Assert.True(options.Grid); Assert.True(options.Collision);
        options.Grid = false; Assert.True(options.Collision);
        options.Collision = false; options.Grid = true; Assert.False(options.Collision);
    }
}
