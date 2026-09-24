using System.Globalization;
using System.IO;
using System.Text.Json;
using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class WorkspaceStateTests
{
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
    public void OldLayoutMigratesWithoutReusingObsoleteSectionHeights()
    {
        var old = JsonSerializer.Deserialize<StudioSettings>("""{"PropertiesWidth":410,"AnimationSidebarWidth":460,"AnimationSectionHeights":{"StatusSection":900},"Theme":"Dark","LastRoot":"fixture","CreateBackupOnSave":true}""")!;
        var layout = old.GetWorkspace();
        Assert.Equal(410, layout.InspectorWidth); Assert.Equal(294, layout.NavigatorWidth); Assert.Equal(180, layout.ToolsHeight);
        Assert.Equal("Dark", old.Theme); Assert.Equal("fixture", old.LastRoot); Assert.True(old.CreateBackupOnSave);
        layout.NavigatorWidth = double.NaN; layout.InspectorWidth = 1; layout.ToolsHeight = double.PositiveInfinity; layout.Preset = "corrupt"; layout.Groups = null!;
        layout.Normalize(); Assert.Equal(294, layout.NavigatorWidth); Assert.Equal(320, layout.InspectorWidth); Assert.Equal(180, layout.ToolsHeight); Assert.Equal("Edit",layout.Preset); Assert.NotNull(layout.Groups);
        Assert.Equal(layout.InspectorWidth, JsonSerializer.Deserialize<StudioSettings>(JsonSerializer.Serialize(old))!.GetWorkspace().InspectorWidth);
    }
    [Fact]
    public void InspectorOrderMigratesOnceAndFloatingBoundsIgnoreOldPaneHeight()
    {
        foreach (var (stored, expected) in new[] { (0,0), (1,2), (2,1), (3,0) })
        {
            var layout = JsonSerializer.Deserialize<WorkspaceLayout>($$"""{"Version":2,"BrowserTab":2,"InspectorTab":{{stored}},"PropertiesHeight":999,"ProgramHeight":999,"ProgramResized":true}""")!;
            layout.Normalize(); Assert.Equal(3,layout.Version); Assert.Equal(2,layout.BrowserTab);
            Assert.Equal(expected,layout.InspectorTab); Assert.Equal(720,layout.PropertiesWindow.Height);
            layout.PropertiesWindow.Height = 830; layout.PropertiesWindow.Left = -100;
            var restored = JsonSerializer.Deserialize<WorkspaceLayout>(JsonSerializer.Serialize(layout))!;
            restored.Normalize(); Assert.Equal(expected,restored.InspectorTab); Assert.Equal(830,restored.PropertiesWindow.Height); Assert.Equal(-100,restored.PropertiesWindow.Left);
        }
        var invalid = new WorkspaceLayout { PropertiesWindow = new() { Height = double.NaN, Width = double.PositiveInfinity, Left = double.NaN, Top = double.PositiveInfinity }, InspectorTab = 99 };
        invalid.Normalize(); Assert.Equal(720,invalid.PropertiesWindow.Height); Assert.Equal(640, invalid.PropertiesWindow.Width); Assert.Null(invalid.PropertiesWindow.Left); Assert.Null(invalid.PropertiesWindow.Top); Assert.Equal(2,invalid.InspectorTab);
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
