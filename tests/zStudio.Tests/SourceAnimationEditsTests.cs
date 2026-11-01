using System.Text;
using zStudio.Core.Animation;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

public sealed class SourceAnimationEditsTests
{
    [Theory]
    [InlineData("blocks")]
    [InlineData("callbackOperands")]
    [InlineData("callbackTiming")]
    [InlineData("sequences")]
    [InlineData("definitions")]
    [InlineData("duplicateDefinitions")]
    public void OwnerHierarchyChangesCannotRetargetStartsCallsOrConditionSwitches(string change)
    {
        // Must-have: identical call sites must retain their condition, sequence and definition owners across recompilation.
        var token = TestContext.Current.CancellationToken;
        static string Definition(string name, string body) => $"ANIMATION_DEFINITION ( NAME ( ground ) ANIMATION_NAME ( {name} ) {body} )";
        static string Sequence(string body) => $"SEQUENCE_DEFINITION ( NAME ( main ) {body} )";
        const string first = "IF ( PLAYER_RANGE ( 10 ) ) CALL_ANIMATION ( NAME ( child ) ) ENDIF ()";
        const string second = "IF ( PLAYER_RANGE ( 20 ) ) CALL_ANIMATION ( NAME ( child ) ) ENDIF ()";
        string before, after;
        if (change == "blocks")
        {
            before = Definition("owner", Sequence(first + second));
            after = Definition("owner", Sequence(second + first));
        }
        else if (change is "callbackOperands" or "callbackTiming")
        {
            string early = "IF ( PLAYER_RANGE ( 10 ) ) CALLBACK ( VALUE ( 10 ) START_TIME ( ANIMATION_OFFSET 1 ) ) "
                + "CALL_ANIMATION ( NAME ( child ) START_TIME ( EVENT_OFFSET 0 ) ) ENDIF ()";
            string late = "IF ( PLAYER_RANGE ( 10 ) ) CALLBACK ( VALUE ( " + (change == "callbackOperands" ? "20" : "10")
                + " ) START_TIME ( ANIMATION_OFFSET 2 ) ) CALL_ANIMATION ( NAME ( child ) START_TIME ( EVENT_OFFSET 0 ) ) ENDIF ()";
            before = Definition("owner", Sequence(early + late)); after = Definition("owner", Sequence(late + early));
        }
        else if (change == "sequences")
        {
            string early = Sequence(first.Replace("NAME ( child )", "NAME ( child ) START_TIME ( ANIMATION_OFFSET 1 )", StringComparison.Ordinal));
            string late = Sequence(first.Replace("NAME ( child )", "NAME ( child ) START_TIME ( ANIMATION_OFFSET 2 )", StringComparison.Ordinal));
            before = Definition("owner", early + late); after = Definition("owner", late + early);
        }
        else if (change == "definitions")
        {
            before = Definition("first", Sequence(first)) + Definition("second", Sequence(first));
            after = Definition("second", Sequence(first));
        }
        else
        {
            before = Definition("owner", Sequence(first)) + Definition("owner", Sequence(second));
            after = Definition("owner", Sequence(second)) + Definition("owner", Sequence(first));
        }
        string Pack(string owners) => "ANIMATION_DEFINITIONS ( ANIMATION_LIST ( " + owners
            + Definition("child", Sequence("CALLBACK ( VALUE ( 9 ) )")) + " ) )";
        var files = VtolDropFixture.Files();
        files.Content[VtolDropFixture.Root] = Encoding.Latin1.GetBytes(Pack(before));
        AnimationCompiler.Evaluation Evaluate() => AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"], null,
            zStudio.Core.Formats.FormatRegistry.MaximumDocumentBytes, token, captureSourceBindings: true);
        var original = Evaluate();
        var events = original.Package.Entries[1].Sequences[0].Events;
        var scenario = AnimationScenario.For(1).WithCondition("", 1, events.First(e => e.Type == 31).Id, AnimationConditionChoice.True);
        scenario = scenario.WithInclusion(scenario.CallPaths(original.Package, token).Paths.Single(p => p.Event == events.First(e => e.Type == 24).Id).Path, true, original.Package, token);
        var session = new AnimationScenarioSession(new(original.Package, original.Provenance), scenario);
        files.Content[VtolDropFixture.Root] = Encoding.Latin1.GetBytes(Pack(after));
        var edited = Evaluate(); session.Rebind(new(edited.Package, edited.Provenance));
        Assert.Empty(session.Current.IncludedCalls); Assert.Empty(session.Current.Conditions);
        Assert.Equal(new AnimationScenarioMarks(change is "definitions" or "duplicateDefinitions" ? 1 : 0, 1, 1), session.Marks);
        if (session.Marks.Starts > 0) Assert.Empty(session.Current.Starts);
        session = AnimationScenarioSession.Restore(session.Source, session.Save());
        Assert.Empty(session.Current.IncludedCalls); Assert.Empty(session.Current.Conditions);
        files.Content[VtolDropFixture.Root] = Encoding.Latin1.GetBytes(Pack(before));
        var undone = Evaluate(); session.Rebind(new(undone.Package, undone.Provenance));
        Assert.Equal(1, session.Current.Primary); Assert.Single(session.Current.IncludedCalls); Assert.Single(session.Current.Conditions);
        Assert.Equal(AnimationScenarioMarks.None, session.Marks);
        Assert.Equal(undone.Package.Entries[1].Sequences[0].Events.First(e => e.Type == 31).Id, Assert.Single(session.Current.Conditions).Event);
    }

    [Fact]
    public void OptionalSettingRemovalCannotRetargetScenarioEventsAndUndoRestoresTheirOwnership()
    {
        // Must-have source integrity: removing HEALTH used to put sibling B's IF and call at A's saved source addresses.
        var token = TestContext.Current.CancellationToken;
        var files = VtolDropFixture.Files();
        files.Content[VtolDropFixture.Root] = Encoding.Latin1.GetBytes("""
            ANIMATION_DEFINITIONS ( ANIMATION_LIST (
              ANIMATION_DEFINITION ( NAME ( ground ) HEALTH ( 2 ) RESET_TIME ( -1 )
                SEQUENCE_DEFINITION ( NAME ( A ) IF ( PLAYER_RANGE ( 10 ) ) CALL_ANIMATION ( NAME ( child ) ) ENDIF () )
                SEQUENCE_DEFINITION ( NAME ( B ) IF ( PLAYER_RANGE ( 20 ) ) CALL_ANIMATION ( NAME ( child ) ) ENDIF () ) )
              ANIMATION_DEFINITION ( NAME ( ground ) ANIMATION_NAME ( child ) ACTIVATION ( ON_CALL )
                SEQUENCE_DEFINITION ( NAME ( main ) CALLBACK ( VALUE ( 9 ) ) ) )
            ) )
            """);
        byte[] original = files.Content[VtolDropFixture.Root];
        AnimationCompiler.Evaluation Evaluate() => AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"],
            effects: null, maximumBytes: zStudio.Core.Formats.FormatRegistry.MaximumDocumentBytes, token: token, captureSourceBindings: true);
        var initial = Evaluate();
        var sequence = initial.Package.Entries[1].Sequences.Single(s => s.Name == "A");
        var condition = sequence.Events.Single(e => e.Type == 31);
        var call = sequence.Events.Single(e => e.Type == 24);
        var scenario = AnimationScenario.For(1).WithCondition("", 1, condition.Id, AnimationConditionChoice.True);
        string included = scenario.CallPaths(initial.Package, token).Paths.Single(p => p.Event == call.Id).Path;
        scenario = scenario.WithInclusion(included, true, initial.Package, token);
        var session = new AnimationScenarioSession(new(initial.Package, initial.Provenance), scenario);
        string savedRecord = initial.Provenance!.FindEvent(condition.Id)!.RecordId!;

        var model = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token);
        var health = model.Records.Single(r => r.Kind == "settings").Fields.Single(f => f.Key == "HEALTH");
        var removed = SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0, [new(health.Id, null)], token);
        files.Content[removed.File] = removed.Content;
        var edited = Evaluate();
        var sibling = edited.Package.Entries[1].Sequences.Single(s => s.Name == "B").Events.Single(e => e.Type == 31);
        Assert.Equal(savedRecord, edited.Provenance!.FindEvent(sibling.Id)!.RecordId);
        Assert.Contains(session.Rebind(new(edited.Package, edited.Provenance)), n => n.Kind == "condition");
        Assert.Empty(session.Current.Conditions); Assert.Empty(session.Current.IncludedCalls);
        Assert.Equal(new AnimationScenarioMarks(0, 1, 1), session.Marks);

        session = AnimationScenarioSession.Restore(session.Source, session.Save());
        Assert.Empty(session.Current.Conditions); Assert.Empty(session.Current.IncludedCalls);
        Assert.Equal(new AnimationScenarioMarks(0, 1, 1), session.Marks);
        files.Content[VtolDropFixture.Root] = original;
        var undone = Evaluate(); session.Rebind(new(undone.Package, undone.Provenance));
        var restoredA = undone.Package.Entries[1].Sequences.Single(s => s.Name == "A");
        Assert.Equal(restoredA.Events.Single(e => e.Type == 31).Id, Assert.Single(session.Current.Conditions).Event);
        Assert.Equal(AnimationScenarioMarks.None, session.Marks);
        Assert.Equal(restoredA.Events.Single(e => e.Type == 24).Id,
            session.Current.CallPaths(undone.Package, token).Paths.Single(p => session.Current.Includes(p.Path)).Event);

        // Unique event ownership survives ordinary operands and an added timing field through the same real edit path.
        model = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token);
        var conditionField = model.Records.Single(r => r.Id == savedRecord).Fields.Single(f => f.Key == "PLAYER_RANGE");
        string callRecord = undone.Provenance!.FindEvent(restoredA.Events.Single(e => e.Type == 24).Id)!.RecordId!;
        var timing = model.Records.Single(r => r.Id == callRecord).Fields.Single(f => f.Key == "START_TIME");
        var scalar = SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0,
            [new(conditionField.Id, ["30"]), new(timing.Id, ["ANIMATION_OFFSET", "1.5"])], token);
        files.Content[scalar.File] = scalar.Content;
        var retimed = Evaluate();
        Assert.Empty(session.Rebind(new(retimed.Package, retimed.Provenance)));
        Assert.Single(session.Current.Conditions); Assert.Single(session.Current.IncludedCalls);
        Assert.Equal(AnimationScenarioMarks.None, session.Marks);
    }

    [Fact]
    public void AmbiguousSiblingEventsRequireTheirSemanticContentsButAllowTimingEdits()
    {
        // Must-have: same-kind siblings can share a target; names and source ordinals alone cannot prove which one survived.
        var token = TestContext.Current.CancellationToken;
        var files = VtolDropFixture.Files();
        files.Content[VtolDropFixture.Root] = Encoding.Latin1.GetBytes("""
            ANIMATION_DEFINITIONS ( ANIMATION_LIST (
              ANIMATION_DEFINITION ( NAME ( ground )
                SEQUENCE_DEFINITION ( NAME ( main )
                  IF ( PLAYER_RANGE ( 10 ) ) CALL_ANIMATION ( NAME ( child ) LOCAL_NAME ( first ) ) ENDIF ()
                  IF ( PLAYER_RANGE ( 20 ) ) CALL_ANIMATION ( NAME ( child ) LOCAL_NAME ( second ) ) ENDIF () ) )
              ANIMATION_DEFINITION ( NAME ( ground ) ANIMATION_NAME ( child ) ACTIVATION ( ON_CALL )
                SEQUENCE_DEFINITION ( NAME ( main ) CALLBACK ( VALUE ( 9 ) ) ) )
            ) )
            """);
        AnimationCompiler.Evaluation Evaluate() => AnimationCompiler.Evaluate(files, VtolDropFixture.Root, ["ground"],
            effects: null, maximumBytes: zStudio.Core.Formats.FormatRegistry.MaximumDocumentBytes, token: token, captureSourceBindings: true);
        var initial = Evaluate();
        var events = initial.Package.Entries[1].Sequences[0].Events;
        var condition = events.First(e => e.Type == 31); var call = events.First(e => e.Type == 24);
        var scenario = AnimationScenario.For(1).WithCondition("", 1, condition.Id, AnimationConditionChoice.True);
        scenario = scenario.WithInclusion(scenario.CallPaths(initial.Package, token).Paths.Single(p => p.Event == call.Id).Path, true, initial.Package, token);
        var session = new AnimationScenarioSession(new(initial.Package, initial.Provenance), scenario);
        var model = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token);
        string conditionRecord = initial.Provenance!.FindEvent(condition.Id)!.RecordId!;
        string callRecord = initial.Provenance.FindEvent(call.Id)!.RecordId!;
        var retime = SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0,
            model.Records.Where(r => r.Id == conditionRecord || r.Id == callRecord).Select(r =>
                new SourceAnimationEdits.Change(r.Fields.Single(f => f.Key == "START_TIME").Id, ["ANIMATION_OFFSET", "1"])).ToArray(), token);
        files.Content[retime.File] = retime.Content;
        var retimed = Evaluate(); Assert.Empty(session.Rebind(new(retimed.Package, retimed.Provenance)));
        Assert.Single(session.Current.Conditions); Assert.Single(session.Current.IncludedCalls);

        // An external same-shape sibling swap looks exactly like ordinary scalar edits. Ambiguous ownership is marked.
        string text = Encoding.Latin1.GetString(retime.Content);
        files.Content[VtolDropFixture.Root] = Encoding.Latin1.GetBytes(text.Replace("( 10 )", "( swap )", StringComparison.Ordinal)
            .Replace("( 20 )", "( 10 )", StringComparison.Ordinal).Replace("( swap )", "( 20 )", StringComparison.Ordinal)
            .Replace("( first )", "( swap )", StringComparison.Ordinal).Replace("( second )", "( first )", StringComparison.Ordinal)
            .Replace("( swap )", "( second )", StringComparison.Ordinal));
        var swapped = Evaluate(); session.Rebind(new(swapped.Package, swapped.Provenance));
        Assert.Empty(session.Current.Conditions); Assert.Empty(session.Current.IncludedCalls);
        Assert.Equal(new AnimationScenarioMarks(0, 1, 1), session.Marks);
        files.Content[VtolDropFixture.Root] = retime.Content;
        var back = Evaluate(); session.Rebind(new(back.Package, back.Provenance));
        Assert.Single(session.Current.Conditions); Assert.Single(session.Current.IncludedCalls);
        Assert.Equal(AnimationScenarioMarks.None, session.Marks);

        // Identical call operands need a timing-order witness: a normal move retains ownership, crossing siblings cannot.
        files.Content[VtolDropFixture.Root] = Encoding.Latin1.GetBytes(text.Replace("LOCAL_NAME ( first )", "", StringComparison.Ordinal)
            .Replace("LOCAL_NAME ( second )", "", StringComparison.Ordinal));
        var twins = Evaluate(); var twinCall = twins.Package.Entries[1].Sequences[0].Events.First(e => e.Type == 24);
        var twinScenario = AnimationScenario.For(1);
        twinScenario = twinScenario.WithInclusion(twinScenario.CallPaths(twins.Package, token).Paths.Single(p => p.Event == twinCall.Id).Path, true, twins.Package, token);
        var twinSession = new AnimationScenarioSession(new(twins.Package, twins.Provenance), twinScenario);
        model = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token);
        var twinTimings = model.Records.Where(r => r.Kind == "CALL_ANIMATION").Select(r => r.Fields.Single(f => f.Key == "START_TIME")).ToArray();
        var movedTwin = SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0, [new(twinTimings[0].Id, ["ANIMATION_OFFSET", "2"])], token);
        files.Content[movedTwin.File] = movedTwin.Content;
        var moved = Evaluate(); Assert.Empty(twinSession.Rebind(new(moved.Package, moved.Provenance)));
        Assert.Single(twinSession.Current.IncludedCalls);
        model = SourceAnimationEdits.Read(files, VtolDropFixture.Root, 0, token);
        twinTimings = model.Records.Where(r => r.Kind == "CALL_ANIMATION").Select(r => r.Fields.Single(f => f.Key == "START_TIME")).ToArray();
        var crossed = SourceAnimationEdits.Plan(files, VtolDropFixture.Root, 0,
            [new(twinTimings[0].Id, ["ANIMATION_OFFSET", "0"]), new(twinTimings[1].Id, ["ANIMATION_OFFSET", "2"])], token);
        files.Content[crossed.File] = crossed.Content;
        var exchanged = Evaluate(); twinSession.Rebind(new(exchanged.Package, exchanged.Provenance));
        Assert.Empty(twinSession.Current.IncludedCalls); Assert.Equal(1, twinSession.Marks.Paths);
        files.Content[VtolDropFixture.Root] = movedTwin.Content;
        var sameOrder = Evaluate(); twinSession.Rebind(new(sameOrder.Package, sameOrder.Provenance));
        Assert.Single(twinSession.Current.IncludedCalls); Assert.Equal(AnimationScenarioMarks.None, twinSession.Marks);
    }

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
