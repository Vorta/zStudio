using System.Text;
using zStudio.Core.Animation;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

public sealed class SourceAnimationEditsTests
{
    [Fact]
    public void OptionalEventFlagsCanBeDisabledAndEnabledAfterReload()
    {
        var token = TestContext.Current.CancellationToken;
        var files = VtolDropFixture.Files();
        files.Content[VtolDropFixture.Root] = Encoding.ASCII.GetBytes("""
            ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION (
              NAME ( ground ) ACTIVATION ( ON_CALL )
              SEQUENCE_DEFINITION ( NAME ( main )
                CALL_ANIMATION ( NAME ( child ) WAIT_FOR_COMPLETION () )
                OBJECT_CYCLE_TEXTURE ( NAME ( ground ) RESET () )
              )
            ) ) )
            """);
        var model = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token);
        Assert.All(model.Records.Single(r => r.Kind == "sequence").Fields, f => Assert.Contains(f.Key, new[] { "NAME", "ACTIVATION" }));
        string[] flags = ["WAIT_FOR_COMPLETION", "RESET"];
        var removed = SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0,
            model.Records.SelectMany(r => r.Fields).Where(f => flags.Contains(f.Key)).Select(f => new SourceAnimationEdits.Change(f.Id, null)).ToArray(), token);
        files.Content[removed.File] = removed.Content;
        model = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token);
        var absent = model.Records.SelectMany(r => r.Fields).Where(f => flags.Contains(f.Key)).ToArray();
        Assert.Equal(2, absent.Length); Assert.All(absent, f => { Assert.False(f.Present); Assert.True(f.Optional); Assert.Empty(f.Values); });
        var restored = SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0, absent.Select(f => new SourceAnimationEdits.Change(f.Id, [])).ToArray(), token);
        files.Content[restored.File] = restored.Content;
        Assert.All(SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token).Records.SelectMany(r => r.Fields).Where(f => flags.Contains(f.Key)), f => Assert.True(f.Present));
        Assert.Null(AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"], token: token).EngineRejection);
    }

    [Fact]
    public void PathsBreakAtCutsInsideOneKeyframeEvent()
    {
        var token = TestContext.Current.CancellationToken;
        var files = VtolDropFixture.Files();
        files.Content["data/m1/zrdr/flight.zan"] = Encoding.ASCII.GetBytes("""
            OBJECT ground
            FRAME 0 POSITION 0 0 0
            FRAME 10 POSITION 10 0 0
            FRAME 10 POSITION 100 0 0
            FRAME 20 POSITION 110 0 0
            """);
        var package = AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"], token: token).Package;
        var paths = new AnimationPlayer(VtolDropWindowsTests.Context(package), 1).AnalyzePaths(1.5, token);
        Assert.Contains(Assert.Single(paths.Paths).Points, p => p.Time >= 1 && p.Position.X >= 200 && p.Break);
    }

    [Fact]
    public void PathsBreakAtTeleportsAndReportMissingChildren()
    {
        var token = TestContext.Current.CancellationToken;
        var files = VtolDropFixture.Files();
        files.Content[VtolDropFixture.Root] = Encoding.ASCII.GetBytes("""
            ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION (
              NAME ( ground ) ACTIVATION ( ON_CALL )
              SEQUENCE_DEFINITION ( NAME ( jump )
                OBJECT_TRANSLATE_STATE ( NAME ( ground ) STATE ( 0 0 0 ) )
                OBJECT_TRANSLATE_STATE ( NAME ( INPUT_NODE ) STATE ( 20 0 0 ) START_TIME ( ANIMATION_OFFSET 1 ) )
                CALL_ANIMATION ( NAME ( missing_child ) START_TIME ( ANIMATION_OFFSET 2 ) )
              )
            ) ) )
            """);
        var package = AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"], token: token).Package;
        var paths = new AnimationPlayer(VtolDropWindowsTests.Context(package), 1).AnalyzePaths(3, token);
        var points = Assert.Single(paths.Paths).Points;
        Assert.Contains(points, p => p.Time >= 1 && p.Position.X == 120 && p.Break);
        Assert.StartsWith("Partial:", paths.Status);
        Assert.Contains(paths.Diagnostics, d => d.Contains("missing_child", StringComparison.Ordinal));
    }

    [Fact]
    public void NestedBounceTriggersAndOptionalPrerequisitesRemainEditableAndDistinct()
    {
        var files = VtolDropFixture.Files();
        files.Content[VtolDropFixture.Root] = Encoding.ASCII.GetBytes("""
            ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION (
              NAME ( ground )
              ACTIVATION_PREREQUISITE ( OPTIONS ( MINIMUM_TO_SATISFY ( 1 ) OBJECT_ACTIVE_LIST ( ( ground ) ) ) )
              SEQUENCE_DEFINITION ( NAME ( motion ) OBJECT_MOTION (
                NAME ( ground ) RUN_TIME ( 1.0 ) BOUNCE_SOUND ( NAME ( impact ) FULL_VOLUME_VELOCITY ( 3.0 ) )
              ) )
            ) ) )
            """);
        var token = TestContext.Current.CancellationToken;
        var model = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token);
        Assert.Contains(model.Records, r => r.Name == "Optional conditions (minimum to satisfy) · active objects");
        var field = model.Records.Single(r => r.Kind == "OBJECT_MOTION").Fields.Single(f => f.Key == "FULL_VOLUME_VELOCITY");
        var edit = SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0, [new(field.Id, ["4.5"])], token);
        Assert.Equal(Encoding.ASCII.GetString(files.Content[VtolDropFixture.Root]).Replace("3.0", "4.5", StringComparison.Ordinal), Encoding.ASCII.GetString(edit.Content));
    }

    [Fact]
    public void ExtendingPrerequisiteListsPreservesRetainedCommentsAndQuoting()
    {
        var files = VtolDropFixture.Files();
        files.Content[VtolDropFixture.Root] = Encoding.ASCII.GetBytes("""
            ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION (
              NAME ( ground ) ACTIVATION_PREREQUISITE ( REQUIRED ( ANIMATION_LIST (
                # Keep the first prerequisite's explanation.
                "first"
                # Keep the last prerequisite's explanation.
                last
              ) ) )
            ) ) )
            """);
        var token = TestContext.Current.CancellationToken;
        var model = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token);
        var field = model.Records.SelectMany(r => r.Fields).Single(f => f.Key == "ANIMATION_LIST");
        var edit = SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0, [new(field.Id, ["first", "middle", "last"])], token);
        string text = Encoding.ASCII.GetString(edit.Content);
        Assert.Contains("# Keep the first prerequisite's explanation.", text);
        Assert.Contains("# Keep the last prerequisite's explanation.", text);
        Assert.Contains("\"first\"", text); Assert.Contains("last", text); Assert.Contains("middle", text);
    }

    [Fact]
    public void EventAndActivationEditsPreserveSourceAndCompileThroughTheSharedCompiler()
    {
        // Protects source integrity and the existing-event editing workflow.
        var token = TestContext.Current.CancellationToken;
        var files = VtolDropFixture.Files();
        var model = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token);
        var call = model.Records.First(r => r.Kind == "CALL_ANIMATION");
        var timing = call.Fields.Single(f => f.Key == "START_TIME");
        string before = Encoding.Latin1.GetString(files.Content[VtolDropFixture.Root]);
        var edit = SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0, [new(timing.Id, ["SEQUENCE_OFFSET", "2"])], token);
        Assert.Equal(before.Replace("SEQUENCE_OFFSET 1", "SEQUENCE_OFFSET 2.0", StringComparison.Ordinal), Encoding.Latin1.GetString(edit.Content));
        files.Content[edit.File] = edit.Content;
        var built = AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"], token: token);
        Assert.Null(built.EngineRejection);
        Assert.Equal(2, built.Package.Entries[1].Sequences[1].Events[1].Threshold);
        var activation = model.Records[0].Fields.Single(f => f.Key == "ACTIVATION");
        var activated = SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0, [new(activation.Id, ["ON_CALL"])], token);
        files.Content[activated.File] = activated.Content;
        Assert.Null(AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"], token: token).EngineRejection);
        var motion = model.Records.First(r => r.Kind == "OBJECT_MOTION_SI_SCRIPT");
        Assert.Throws<InvalidDataException>(() => SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0,
            [new(motion.Fields.First(f => f.Key == "SCRIPT_FRAME_RATE").Id, ["15"])], token));
        Assert.Throws<InvalidDataException>(() => SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0, [new(timing.Id, ["EVENT_OFFSET", "NaN"])], token));
    }

    [Fact]
    public void GenericRecognizedStopTimingRemainsEditableIncludingNegativeOffGridThresholds()
    {
        var token = TestContext.Current.CancellationToken;
        var files = VtolDropFixture.Files();
        var original = VtolDropFixture.Compile(files, token).Package;
        Assert.Single(VtolDropWindows.Discover(original, token).Candidates);
        var definition = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token);
        var timing = definition.Records.Where(record => record.Kind == "STOP_ANIMATION")
            .SelectMany(record => record.Fields).First(field => field.Key == "START_TIME" && field.Values[0] == "EVENT_OFFSET");
        var edit = SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0,
            [new(timing.Id, ["EVENT_OFFSET", "-0.1234567"])], token);
        files.Content[edit.File] = edit.Content;
        var read = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token);
        var after = read.Records.SelectMany(record => record.Fields).Single(field => field.Id == timing.Id);
        Assert.Equal("EVENT_OFFSET", after.Values[0]);
        Assert.Equal(-0.1234567f, float.Parse(after.Values[1], System.Globalization.CultureInfo.InvariantCulture));
        var compiled = VtolDropFixture.Compile(files, token).Package;
        Assert.Empty(VtolDropWindows.Discover(compiled, token).Candidates);
    }

    [Fact]
    public void GenericPathsUseRuntimeMotionWithoutChangingTheContext()
    {
        var token = TestContext.Current.CancellationToken;
        var files = VtolDropFixture.Files();
        var package = AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"], token: token).Package;
        var context = VtolDropWindowsTests.Context(package);
        var player = new AnimationPlayer(context, 1, 1, false, token);
        var paths = player.AnalyzePaths(5, token);
        var path = Assert.Single(paths.Paths);
        Assert.True(path.Points.Count > 2);
        Assert.True(path.Points[1].Break);
        Assert.NotEqual(path.Points[1].Position, path.Points[^1].Position);
        Assert.Equal(0, new AnimationPlayer(context, 1).Time);
    }
}
