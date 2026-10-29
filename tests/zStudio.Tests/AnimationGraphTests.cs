using System.Numerics;
using System.Text;
using zStudio.Core;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using Xunit;

namespace zStudio.Tests;

/// <summary>The Structure graph, its run overlay and the persisted scenario (docs/agents/preview.md, "Animation structure" and "Animation scenario").</summary>
public sealed class AnimationGraphTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly string[] Nodes = ["bridge", "healthy", "piece", "rail_1", "rail_2", "rail_3"];

    // A small destroy_tr_bridge: prerequisites on the rails it calls, which call it back; an "if not"; an ELSEIF; a release
    // into a waiting, looping sequence; and an invalidate of itself.
    private static string Bridge(bool thirdCall = true, string root = "bridge") => $$"""
        ANIMATION_DEFINITIONS ( ANIMATION_LIST (
          ANIMATION_DEFINITION (
            NAME ( {{root}} ) ANIMATION_NAME ( destroy_bridge ) ANIMATION_ROOT_NAME ( healthy ) ACTIVATION ( ON_CALL ) RESET_TIME ( -1 )
            ACTIVATION_PREREQUISITE ( OPTIONS ( MINIMUM_TO_SATISFY ( 2 ) ANIMATION_LIST ( rail_1 rail_2 rail_3 ) ) )
            RESET_STATE ( OBJECT_ACTIVE_STATE ( NAME ( healthy ) STATE ( ACTIVE ) ) )
            SEQUENCE_DEFINITION ( NAME ( healthy_to_destroyed )
              OBJECT_ACTIVE_STATE ( NAME ( healthy ) STATE ( INACTIVE ) )
              INVALIDATE_ANIMATION ( NAME ( destroy_bridge ) )
              CALL_ANIMATION ( NAME ( rail_1 ) ) CALL_ANIMATION ( NAME ( rail_2 ) ) {{(thirdCall ? "CALL_ANIMATION ( NAME ( rail_3 ) )" : "")}} )
            SEQUENCE_DEFINITION ( NAME ( flare_sequence )
              IF ( PLAYER_RANGE ( 64 ) ) ELSE () OBJECT_ACTIVE_STATE ( NAME ( piece ) STATE ( INACTIVE ) ) ENDIF () )
            SEQUENCE_DEFINITION ( NAME ( chance )
              IF ( RANDOM_WEIGHT ( 0.4 ) ) CALLBACK ( VALUE ( 14 ) )
              ELSEIF ( ANIMATION_LOD ( MEDIUM ) START_TIME ( EVENT_OFFSET 0.5 ) ) CALLBACK ( VALUE ( 15 ) )
              ELSE () CALLBACK ( VALUE ( 99 ) ) ENDIF () )
            SEQUENCE_DEFINITION ( NAME ( move_piece )
              CALL_SEQUENCE ( NAME ( mainsplash ) )
              OBJECT_ACTIVE_STATE ( NAME ( piece ) STATE ( INACTIVE ) START_TIME ( EVENT_OFFSET 0.1 ) ) )
            SEQUENCE_DEFINITION ( NAME ( mainsplash ) ACTIVATION ( ON_CALL )
              OBJECT_ACTIVE_STATE ( NAME ( piece ) STATE ( ACTIVE ) )
              LOOP ( LOOP_COUNT ( 4 ) START_TIME ( EVENT_OFFSET 0.1 ) ) ) )
          ANIMATION_DEFINITION (
            NAME ( "rail_*" ) ANIMATION_NAME ( "rail_*" ) HEALTH ( 1 ) RESET_TIME ( -1 )
            SEQUENCE_DEFINITION ( NAME ( fall ) CALL_ANIMATION ( NAME ( destroy_bridge ) ) ) )
        ) )
        """;
    private static AnimationCompiler.Evaluation Compile(string text, string[]? nodes = null)
    {
        var files = new VtolDropFixture.MemoryFiles(new(StringComparer.OrdinalIgnoreCase) { [VtolDropFixture.Root] = Encoding.Latin1.GetBytes(text) });
        var evaluated = AnimationCompiler.Evaluate(files, VtolDropFixture.Root, nodes ?? Nodes, null, FormatRegistry.MaximumDocumentBytes, Token, captureSourceBindings: true);
        Assert.Null(evaluated.EngineRejection);
        return evaluated;
    }
    private static AnimationSequence Sequence(AnimationPackage package, string name) => package.Entries[1].AllSequences.Single(s => s.Name == name);

    // Must-have: the graph is the one model the Structure view, MCP and the compiled viewer read (blocks, edges, back-links, layout).
    [Fact]
    public void TheGraphShowsBlocksEdgesBackLinksAndAStableLayout()
    {
        var evaluated = Compile(Bridge()); var package = evaluated.Package;
        Assert.Equal(["destroy_bridge", "rail_1", "rail_2", "rail_3"], package.Entries.Skip(1).Select(e => e.Name));
        var index = AnimationGraphIndex.Create(package, evaluated.Provenance, token: Token);
        var graph = AnimationGraph.Create(index, 1, token: Token);
        var nodes = graph.Nodes().Rows;
        Assert.Equal((AnimationGraphNodeKind.Activation, 0), (nodes[0].Kind, nodes[0].Column));
        var reference = Assert.Single(nodes, n => n.Kind == AnimationGraphNodeKind.Reference);
        Assert.Equal((2, "rail_*"), (reference.Column, reference.Reference!.Name));
        Assert.Equal([2, 3, 4], reference.Reference.Entries);
        var sequences = nodes.Where(n => n.Kind == AnimationGraphNodeKind.Sequence).ToArray();
        Assert.Equal(["Cleanup", "healthy_to_destroyed", "flare_sequence", "chance", "move_piece", "mainsplash"], sequences.Select(s => s.Label));
        Assert.All(sequences, s => Assert.Equal((1, AnimationGraph.FrameId(1)), (s.Column, s.Parent)));

        // destroy_bridge ↔ rail_N: one bundled back-link to the opened binding's activation; the prerequisites as edges too.
        var back = Assert.Single(graph.Edges().Rows, e => e.BackLink);
        Assert.Equal((AnimationGraphRelation.Call, reference.Id, AnimationGraph.ActivationId(1), 3, "calls back (cycle) ×3"), (back.Relation, back.From, back.To, back.Count, back.Label));
        Assert.Contains(graph.Edges().Rows, e => e.Relation == AnimationGraphRelation.Prerequisite && e.From == reference.Id && e.Count == 3);
        var invalidate = Assert.Single(graph.Edges().Rows, e => e.Relation == AnimationGraphRelation.InvalidateAnimation);
        Assert.Equal((AnimationGraph.ActivationId(1), false), (invalidate.To, invalidate.BackLink));
        var destroy = Sequence(package, "healthy_to_destroyed");
        var calls = graph.Rows(1, destroy.Id).Rows.Single(r => r.Type == 24);
        Assert.Equal(("Call rail_1 … 3 (3)", 3), (calls.Label, calls.Members.Count));
        var call = Assert.Single(graph.EdgesOf(calls.Id));
        Assert.Equal((reference.Id, 3), (call.To, call.Count));
        Assert.Equal(destroy.Events.Skip(2).Select((e, i) => AnimationExecutionPolicy.ChildPath("", 1, e.Id, 2 + i)), call.Paths);
        // Release edge into the waiting sequence, whose loop waits for a new release on every pass.
        var splash = Sequence(package, "mainsplash");
        var release = Assert.Single(graph.Edges().Rows, e => e.Relation == AnimationGraphRelation.Release);
        Assert.Equal(AnimationGraph.SequenceId(1, splash.Id), release.To);
        Assert.Equal("Waits for release", graph.Node(release.To)!.Sequence!.StateLine);
        var loop = Assert.Single(graph.Blocks(1, splash.Id)).Loop!;
        Assert.Equal(("count", (int?)4, true, "×4"), (loop.Mode, loop.Count, loop.WaitsForRelease, loop.Label));
        // The shipped "if not": both branches labelled.
        var flare = Assert.Single(graph.Blocks(1, Sequence(package, "flare_sequence").Id));
        Assert.Equal(["if true (nothing)", "if false"], flare.Branches.Select(b => b.Label));
        Assert.Equal(("playerRange", "Player within 64 of healthy?", (double?)64), (flare.Branches[0].Condition!.Operand, flare.Branches[0].Condition!.Question, flare.Branches[0].Condition!.Radius));
        // ELSEIF: its own question, its START_TIME ignored after a false IF.
        var chance = Sequence(package, "chance");
        var block = Assert.Single(graph.Blocks(1, chance.Id));
        Assert.Equal(["ifTrue", "elseIf", "ifFalse"], block.Branches.Select(b => b.Kind));
        var elseIf = block.Branches[1].Condition!;
        Assert.Equal(("effectsDetail", "medium", true, "else, effects detail Medium or higher?"), (elseIf.Operand, elseIf.EffectsDetail, elseIf.ThresholdIgnored, elseIf.Question));
        Assert.Equal(0.4, block.Branches[0].Condition!.Probability!.Value, 1e-6);
        Assert.Equal("ignored after a false IF", graph.Rows(1, chance.Id).Rows.Single(r => r.Type == 33).ThresholdNote);
        Assert.Equal("Game callback 14 · takes the controls away", graph.Rows(1, chance.Id).Rows.First(r => r.Type == 35).Label);
        // Activation: triggers, the "at least 2 of 3" group, the callers that are also callees with their back-link.
        var activation = nodes[0].Activation!;
        Assert.Contains(activation.Triggers, t => t.Kind == "whenCalled");
        Assert.Contains(activation.Triggers, t => t.Kind == "poseKept");
        var group = Assert.Single(activation.Prerequisites);
        Assert.Equal("At least 2 of 3", group.Header);
        Assert.Equal([2, 3, 4], group.Items.Select(i => i.Entry));
        var callers = Assert.Single(activation.Callers);
        Assert.Equal((3, reference.Id, back.Id), (callers.Bindings, callers.Node, callers.BackLink));
        Assert.Equal(4, graph.Callers(1).Total);

        // Expanding the rails adds one frame for the group at the next column, on its first binding (one against the budget);
        // its call back is a back-link from its row. The member switcher shows another binding in the same frame.
        var expanded = graph.WithExpanded(reference.Reference.Group, true, Token);
        var frame = Assert.Single(expanded.Nodes(0, 100).Rows, n => n.Kind == AnimationGraphNodeKind.Frame && n.Entry != 1);
        Assert.Equal((2, 3, reference.Id), (frame.Entry!.Value, frame.Column, frame.Parent));
        Assert.Equal([2, 3, 4], frame.Frame!.Members);
        Assert.Equal((0, call.Paths[0]), (frame.Frame.MemberIndex, frame.Frame.Path));
        Assert.Equal(1, expanded.Edges().Rows.Count(e => e.BackLink && e.From.StartsWith("row:", StringComparison.Ordinal) && e.To == AnimationGraph.ActivationId(1)));
        var third = expanded.WithMember(reference.Reference.Group, 4, Token);
        Assert.Equal(4, Assert.Single(third.Nodes(0, 100).Rows, n => n.Kind == AnimationGraphNodeKind.Frame && n.Entry != 1).Entry);
        Assert.Equal(4, third.Node(reference.Id)!.Reference!.Member);
        // Deterministic: the same inputs give the same layout and edges.
        var again = AnimationGraph.Create(index, 1, [reference.Reference.Group], Token);
        Assert.Equal(expanded.Nodes().Rows.Select(n => (n.Id, n.Column, n.Order)), again.Nodes().Rows.Select(n => (n.Id, n.Column, n.Order)));
        Assert.Equal(expanded.Edges().Rows.Select(e => (e.Id, e.Count)), again.Edges().Rows.Select(e => (e.Id, e.Count)));
    }

    // Must-have: the Structure layout the GUI draws and MCP reports is deterministic, and no edge crosses a node.
    [Fact]
    public void TheLayoutRoutesEveryEdgeAroundTheNodes()
    {
        var evaluated = Compile(Bridge()); var package = evaluated.Package;
        var index = AnimationGraphIndex.Create(package, evaluated.Provenance, token: Token);
        var graph = AnimationGraph.Create(index, 1, token: Token);
        var reference = graph.Nodes().Rows.Single(n => n.Kind == AnimationGraphNodeKind.Reference);
        foreach (var shown in new[] { graph, graph.WithExpanded(reference.Reference!.Group, true, Token) })
        {
            var layout = AnimationGraphLayout.Create(shown, token: Token);
            Assert.Equal(shown.NodeCount, layout.Nodes.Count);
            var boxes = layout.Nodes.Where(n => n.Node.Kind != AnimationGraphNodeKind.Frame).ToArray();
            for (int i = 0; i < boxes.Length; i++)
                for (int j = i + 1; j < boxes.Length; j++) Assert.False(boxes[i].Bounds.Intersects(boxes[j].Bounds), boxes[i].Id + " overlaps " + boxes[j].Id);
            foreach (var route in layout.Routes)
                for (int i = 0; i + 1 < route.Points.Count; i++)
                {
                    var a = route.Points[i]; var b = route.Points[i + 1];
                    Assert.True(a.X == b.X || a.Y == b.Y, route.Edge.Id + " is not orthogonal");
                    var segment = new AnimationGraphRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(.01, Math.Abs(a.X - b.X)), Math.Max(.01, Math.Abs(a.Y - b.Y)));
                    foreach (var node in boxes)
                    {
                        if (i == 0 && node.Id == route.Edge.FromNode || i + 2 == route.Points.Count && node.Id == route.Edge.To) continue;
                        var inner = new AnimationGraphRect(node.Bounds.X + .5, node.Bounds.Y + .5, node.Bounds.Width - 1, node.Bounds.Height - 1);
                        Assert.False(inner.Intersects(segment), $"{route.Edge.Id} crosses {node.Id}");
                    }
                }
            var again = AnimationGraphLayout.Create(AnimationGraph.Create(index, 1, shown.Expanded, Token), token: Token);
            Assert.Equal(layout.Nodes.Select(n => (n.Id, n.Bounds)), again.Nodes.Select(n => (n.Id, n.Bounds)));
            Assert.Equal(layout.Routes.Select(r => (r.Edge.Id, string.Join(' ', r.Points))), again.Routes.Select(r => (r.Edge.Id, string.Join(' ', r.Points))));
        }
        var plain = AnimationGraphLayout.Create(graph, token: Token);
        // The back-link travels above every frame and ends on the activation's top; the release enters the waiting sequence's pill.
        var back = plain.Routes.Single(r => r.Edge.BackLink);
        Assert.True(back.Points.Min(p => p.Y) < plain.Nodes.Where(n => n.Node.Kind == AnimationGraphNodeKind.Frame).Min(n => n.Bounds.Y));
        Assert.Equal(plain.Node(AnimationGraph.ActivationId(1))!.Bounds.Y, back.EndPoint.Y);
        var release = plain.Routes.Single(r => r.Edge.Relation == AnimationGraphRelation.Release);
        var waiting = plain.Node(release.Edge.To)!;
        var pill = waiting.Items.Single(i => i.Kind == AnimationGraphItemKind.StateLine);
        Assert.Equal((waiting.Bounds.X, waiting.Bounds.Y + pill.Y + pill.Height / 2), (release.EndPoint.X, release.EndPoint.Y));
        Assert.True(release.FromPort);
        // An IF block: its question, outcome line and both labelled branches; a collapsed sequence's call leaves its header.
        var flare = plain.Node(AnimationGraph.SequenceId(1, Sequence(package, "flare_sequence").Id))!;
        Assert.Equal([AnimationGraphItemKind.Question, AnimationGraphItemKind.Outcome, AnimationGraphItemKind.Branch, AnimationGraphItemKind.Branch, AnimationGraphItemKind.Row, AnimationGraphItemKind.EndIf],
            flare.Items.Select(i => i.Kind));
        string destroy = AnimationGraph.SequenceId(1, Sequence(package, "healthy_to_destroyed").Id);
        var folded = AnimationGraphLayout.Create(graph, new() { Collapsed = new HashSet<string>([destroy]) }, Token);
        var call = folded.Routes.Single(r => r.Edge.FromNode == destroy && r.Edge.Relation == AnimationGraphRelation.Call && !r.Edge.BackLink);
        Assert.True(call.FromHidden);
        Assert.Equal(folded.Node(destroy)!.Bounds.Y + AnimationGraphLayout.Header / 2, call.Start.Y);
    }

    // Must-have: the run overlay is a pure projection, so the GUI and MCP never recompute run semantics.
    [Fact]
    public void TheRunOverlayProjectsTheFrameOntoTheGraph()
    {
        var evaluated = Compile(Bridge()); var package = evaluated.Package;
        var graph = AnimationGraph.Create(AnimationGraphIndex.Create(package, evaluated.Provenance, token: Token), 1, token: Token);
        var destroy = Sequence(package, "healthy_to_destroyed"); var flare = Sequence(package, "flare_sequence");
        var move = Sequence(package, "move_piece"); var splash = Sequence(package, "mainsplash");
        AnimationTrace Ran(AnimationSequence sequence, int index, double at, string? disposition = null, int? child = null) =>
            new(1, sequence.Id, sequence.Events[index].Id, "", at, at, "") { Entry = 1, InstancePath = "", Disposition = disposition, ChildEntry = child, ChildInstance = disposition == "launched" ? 7 : null };
        var frame = new AnimationFrame(21, [], [], [], [], null, null, default, default,
            [Ran(destroy, 1, 20, "applied", 1), Ran(destroy, 2, 20, "launched", 2), Ran(destroy, 3, 20, "excluded", 3), Ran(destroy, 4, 20, "excluded", 4),
             Ran(flare, 0, 20), Ran(move, 0, 20.5)],
            [new(1, flare.Id, "flare_sequence", 4, "Complete", 0), new(1, move.Id, "move_piece", 2, "Complete", 0),
             new(1, splash.Id, "mainsplash", 0, "Waiting for release", 1) { ReleasedBy = move.Events[0].Id, ReleasedAt = 20.5, ReleaseKind = "call" }], [])
        {
            Instances = [new(1, 1, 0, "destroy_bridge", "", null, null, 20, false, false)],
            Conditions = [new(1, flare.Events[0].Id, "", 20, true, true, 5) { Kind = "playerRange" }],
            Skipped = [new(1, flare.Id, "", flare.Events[1].Id, flare.Events[2].Id, flare.Events[2].Id, 1, 20, 5) { Reason = "taken" }],
            Bindings = [new(1, "destroy_bridge", AnimationBindingLifecycle.RunningInvalidated, 0, 1, 20, "start") { Invalidated = true }],
        };
        var overlay = AnimationRunOverlay.Create(graph, frame, token: Token);
        Assert.Equal("Running · invalidated", overlay.Binding(1).Label);
        var waiting = overlay.Sequence(AnimationGraph.SequenceId(1, splash.Id))!;
        Assert.Equal((AnimationSequenceRunState.WaitingForRelease, true, AnimationGraph.RowId(1, move.Events[0].Id)), (waiting.State, waiting.NoReleaserLeft, waiting.ReleasedBy));
        Assert.Equal(AnimationRowDisposition.SkippedByCondition, overlay.Row(AnimationGraph.RowId(1, flare.Events[2].Id))!.Disposition);
        Assert.Equal("true · forced · 20.000 s", overlay.Condition(AnimationGraph.RowId(1, flare.Events[0].Id))!.Latest!.Outcome);
        var calls = overlay.Row(AnimationGraph.RowId(1, destroy.Events[2].Id))!;
        Assert.Equal((AnimationRowDisposition.Excluded, 2), (calls.Disposition, calls.Counts.Single(c => c.Disposition == AnimationRowDisposition.Excluded).Count));
        Assert.Equal(AnimationRowDisposition.NotReached, overlay.Row(AnimationGraph.RowId(1, splash.Events[0].Id))!.Disposition);
        Assert.StartsWith("1 instance", overlay.Reference(graph.Nodes().Rows.Single(n => n.Kind == AnimationGraphNodeKind.Reference).Id)!.Label);
    }

    // Must-have: a compiled animation's isolated preview runs every call; its switches exclude a path with what lies below it.
    [Fact]
    public void TheIsolatedPreviewRunsEveryCallButTheExcludedOnes()
    {
        var package = Compile(Bridge()).Package;
        var scenario = AnimationScenario.Isolated(1);
        var reach = scenario.CallPaths(package, Token).Paths;
        var child = reach.First(p => !p.BackLink && reach.Any(q => q.Parent == p.Path));
        string below = reach.First(q => q.Parent == child.Path).Path;
        Assert.True(scenario.AllCalls); Assert.False(scenario.IsCustomized); Assert.False(AnimationScenario.Isolated(0).IsCustomized);
        Assert.All(reach, p => Assert.True(scenario.Includes(p.Path)));
        var excluded = scenario.WithInclusion(child.Path, false, package, Token);
        Assert.False(excluded.Includes(child.Path)); Assert.False(excluded.Includes(below)); Assert.True(excluded.IsCustomized);
        var policy = excluded.ToExecutionPolicy();
        Assert.True(policy.IncludesAllCalls); Assert.False(policy.Includes(below)); Assert.True(policy.Includes(reach.First(p => p.Path != child.Path && !p.Path.StartsWith(child.Path + "/", StringComparison.Ordinal)).Path));
        // Including a path below an excluded one includes the calls that lead to it; Exclude all runs the start alone, Include all every call again.
        Assert.True(excluded.WithInclusion(below, true, package, Token).Includes(child.Path));
        Assert.Empty(excluded.ExcludeAll().ToExecutionPolicy().IncludedCalls); Assert.False(excluded.ExcludeAll().Includes(child.Path));
        Assert.Equal(AnimationScenario.Isolated(1), excluded.ExcludeAll().IncludeAll(package, Token).Scenario);
        // ... and nothing else under that exclusion: the chain's other calls stay excluded, as a map's scenario adds only the chain.
        var tree = Compile("""
            ANIMATION_DEFINITIONS ( ANIMATION_LIST (
              ANIMATION_DEFINITION ( NAME ( bridge ) ANIMATION_NAME ( top ) ANIMATION_ROOT_NAME ( healthy ) ACTIVATION ( ON_CALL )
                SEQUENCE_DEFINITION ( NAME ( run ) CALL_ANIMATION ( NAME ( middle ) ) ) )
              ANIMATION_DEFINITION ( NAME ( piece ) ANIMATION_NAME ( middle ) ANIMATION_ROOT_NAME ( healthy ) ACTIVATION ( ON_CALL )
                SEQUENCE_DEFINITION ( NAME ( run ) CALL_ANIMATION ( NAME ( leaf_a ) ) CALL_ANIMATION ( NAME ( leaf_b ) ) ) )
              ANIMATION_DEFINITION ( NAME ( rail_1 ) ANIMATION_NAME ( leaf_a ) ANIMATION_ROOT_NAME ( healthy ) ACTIVATION ( ON_CALL )
                SEQUENCE_DEFINITION ( NAME ( run ) OBJECT_ACTIVE_STATE ( NAME ( piece ) STATE ( INACTIVE ) ) ) )
              ANIMATION_DEFINITION ( NAME ( rail_2 ) ANIMATION_NAME ( leaf_b ) ANIMATION_ROOT_NAME ( healthy ) ACTIVATION ( ON_CALL )
                SEQUENCE_DEFINITION ( NAME ( run ) OBJECT_ACTIVE_STATE ( NAME ( piece ) STATE ( ACTIVE ) ) ) )
            ) )
            """).Package;
        var isolated = AnimationScenario.Isolated(tree.Entries.First(e => e.Name == "top").Index);
        var calls = isolated.CallPaths(tree, Token).Paths;
        var middle = calls.Single(p => p.Target == tree.Entries.First(e => e.Name == "middle").Index);
        var leaves = calls.Where(p => p.Parent == middle.Path).ToArray();
        Assert.Equal(2, leaves.Length);
        var one = isolated.WithInclusion(middle.Path, false, tree, Token).WithInclusion(leaves[0].Path, true, tree, Token);
        Assert.True(one.Includes(middle.Path)); Assert.True(one.Includes(leaves[0].Path)); Assert.False(one.Includes(leaves[1].Path));
        Assert.False(one.ToExecutionPolicy().Includes(leaves[1].Path));
    }

    // Must-have: the scenario is kept per map in the project's settings and survives a recompile through source provenance.
    [Fact]
    public void TheScenarioRoundTripsThroughSettingsAndRemapsAfterARecompile()
    {
        var evaluated = Compile(Bridge()); var package = evaluated.Package;
        AnimationScenarioSource source = new(package, evaluated.Provenance);
        var flare = Sequence(package, "flare_sequence"); var chance = Sequence(package, "chance");
        var (scenario, result) = AnimationScenario.For(1).WithStartTime(Guid.Empty, 20).WithStart(3, 5).IncludeAll(package, Token);
        Assert.True(result.Included > 0); Assert.True(result.BackLinks > 0);
        // The Scenario tab's Called animations list every reachable path: Include all includes them all but the back-links.
        var reachable = scenario.CallPaths(package, Token);
        Assert.Equal(result.BackLinks, reachable.Paths.Count(p => p.BackLink));
        Assert.Equal(scenario.IncludedCalls.Order(StringComparer.Ordinal), reachable.Paths.Where(p => !p.BackLink).Select(p => p.Path).Order(StringComparer.Ordinal));
        Assert.All(reachable.Paths, p => Assert.True(p.Parent.Length == 0 || p.Parent.StartsWith("/@", StringComparison.Ordinal) || reachable.Paths.Any(q => q.Path == p.Parent)));
        scenario = scenario.WithCondition("", 1, flare.Events[0].Id, AnimationConditionChoice.True)
            .WithCondition("", 1, chance.Events.Single(e => e.Type == 33).Id, AnimationConditionChoice.False) with
        {
            Player = AnimationPlayerReference.Custom(new(1671.2f, 18, 1012.4f)), Seed = 7, EffectsDetail = AnimationEffectsDetail.Medium,
            Phase = AnimationScenarioPhase.Cleanup, Height = 2.5f, BeamOrigin = new Vector3(1, 2, 3), Prerequisites = AnimationPrerequisiteRule.Check,
            RangeGates = AnimationRangeGateRule.Ignore, ZoneGates = AnimationZoneGateRule.View,
        };
        _ = scenario.ToExecutionPolicy();
        string project = Path.Combine(Path.GetTempPath(), "zstudio-scenario-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            SourceProjectSettings.WriteAnimationScenario(project, "m1", AnimationScenarioRecord.Save(scenario, source));
            SourceProjectSettings.WriteDifficulty(project, MissionDifficulty.Hard);
            var restored = AnimationScenarioSession.Restore(source, SourceProjectSettings.ReadAnimationScenario(project, "m1"));
            Assert.Empty(restored.Notices);
            Assert.Equal(scenario, restored.Current);
            Assert.Equal(MissionDifficulty.Hard, SourceProjectSettings.ReadView(project).Difficulty);
            SourceProjectSettings.WriteAnimationLayout(project, 455, true);
            Assert.Equal((455d, true, MissionDifficulty.Hard), (SourceProjectSettings.ReadView(project).AnimationPanelHeight, SourceProjectSettings.ReadView(project).AnimationDiagnostics, SourceProjectSettings.ReadView(project).Difficulty));
            SourceProjectSettings.WriteAnimationScenario(project, "m1", null);
            Assert.Null(SourceProjectSettings.ReadAnimationScenario(project, "m1"));
        }
        finally { Directory.Delete(project, true); }

        // A recompile regenerates every GUID: starts, paths and switches follow their source addresses.
        var session = new AnimationScenarioSession(source, scenario);
        List<AnimationScenarioChange> changes = []; session.Changed += changes.Add;
        var recompiled = Compile(Bridge());
        Assert.Empty(session.Rebind(new(recompiled.Package, recompiled.Provenance)));
        var remapped = session.Current;
        Assert.Equal("remap", Assert.Single(changes).Reason);
        Assert.Equal(scenario.IncludedCalls.Count, remapped.IncludedCalls.Count);
        Assert.Equal(scenario.Starts, remapped.Starts);
        var events = recompiled.Package.Entries.SelectMany(e => e.AllSequences).SelectMany(s => s.Events).Select(e => e.Id).ToHashSet();
        Assert.All(remapped.Conditions, c => Assert.Contains(c.Event, events));
        Assert.All(remapped.IncludedCalls, path => Assert.All(path.Split('/', StringSplitOptions.RemoveEmptyEntries).Where(s => s[0] != '@'),
            segment => Assert.Contains(Guid.ParseExact(segment.Split(':')[1], "N"), events)));
        _ = remapped.ToExecutionPolicy();
        // An edit that removes a call keeps its paths in the scenario, marked and never run (design §12), with a notice; the
        // rest stays. They are saved with the scenario, outlive its other changes and come back with the undo.
        var edited = Compile(Bridge(thirdCall: false));
        AnimationScenarioSource editedSource = new(edited.Package, edited.Provenance);
        var notices = session.Rebind(editedSource);
        Assert.Contains(notices, n => n.Kind == "include");
        int marked = session.Marks.Paths;
        Assert.True(marked > 0);
        Assert.InRange(session.Current.IncludedCalls.Count, 1, remapped.IncludedCalls.Count - 1);
        Assert.Equal(remapped.IncludedCalls.Count, session.Current.IncludedCalls.Count + marked);
        Assert.Equal(2, session.Current.Conditions.Count);
        Assert.Equal(marked, session.Marked!.MarkedItems().Count(i => i.Kind == "paths"));
        var reopened = AnimationScenarioSession.Restore(editedSource, AnimationScenarioRecord.Save(session.Current, editedSource, null, session.Marked));
        Assert.Equal(session.Current, reopened.Current); Assert.Equal(session.Marks, reopened.Marks);
        session.Set(session.Current with { Seed = 9 });
        Assert.Equal(marked, session.Marks.Paths);
        var undone = Compile(Bridge());
        session.Rebind(new(undone.Package, undone.Provenance));
        Assert.Equal(remapped.IncludedCalls.Count, session.Current.IncludedCalls.Count);
        Assert.Equal(AnimationScenarioMarks.None, session.Marks);
        // Keep keeps them marked (and stops asking), Remove drops them for good, Reset clears them.
        var again = Compile(Bridge(thirdCall: false));
        session.Rebind(new(again.Package, again.Provenance));
        session.KeepMarked("paths");
        Assert.Equal((marked, true), (session.Marks.Paths, session.KeptMarks.Contains("paths")));
        session.RemoveMarked("paths");
        Assert.Equal(AnimationScenarioMarks.None, session.Marks);
        var redone = Compile(Bridge());
        session.Rebind(new(redone.Package, redone.Provenance));
        Assert.Equal(remapped.IncludedCalls.Count - marked, session.Current.IncludedCalls.Count);
        var last = Compile(Bridge(thirdCall: false));
        session.Rebind(new(last.Package, last.Provenance));
        session.Reset();
        Assert.Equal(AnimationScenarioMarks.None, session.Marks);

        // Marked items read differently, each naming its caller and sequence; Keep is saved with them.
        var excluding = new AnimationScenarioSession(source, scenario);
        excluding.Rebind(editedSource);
        var items = excluding.MarkedItems();
        Assert.Equal(items.Count, items.Select(i => i.Text).Distinct().Count());
        excluding.KeepMarked("paths");
        Assert.Contains("paths", AnimationScenarioSession.Restore(excluding.Source, excluding.Save()).KeptMarks);
        // An exclusion takes the marked paths below it, Exclude all every one: an undo brings none of them back.
        string caller = excluding.Current.IncludedCalls.First(p => p.StartsWith("/@", StringComparison.Ordinal) && p.Count(c => c == '/') == 2 && p.EndsWith(":1", StringComparison.Ordinal));
        int below = excluding.Marks.Paths;
        excluding.Exclude(excluding.Current.WithInclusion(caller, false, token: Token), [caller]);
        Assert.InRange(excluding.Marks.Paths, 1, below - 1);
        excluding.Exclude(excluding.Current.ExcludeAll(), null);
        Assert.Equal(0, excluding.Marks.Paths);
        excluding.Rebind(new(undone.Package, undone.Provenance));
        Assert.Empty(excluding.Current.IncludedCalls);

        // The primary's binding goes (its root renamed): the opened definition's binding stands in at the marked primary's time,
        // and the undo gives the place back: no phantom marked start, every start at its place.
        var ordered = scenario.WithStart(4, 7);
        var standing = new AnimationScenarioSession(source, ordered);
        var rooted = Compile(Bridge(root: "piece"));
        standing.Rebind(new(rooted.Package, rooted.Provenance));
        Assert.Equal((null, 1), (standing.Current.Primary, standing.Marks.Starts));
        standing.StandIn(1);
        Assert.Equal((20d, 1), (standing.Current.Start(Guid.Empty)!.Seconds, standing.Marks.Starts));
        var back = Compile(Bridge());
        standing.Rebind(new(back.Package, back.Provenance));
        Assert.Equal(AnimationScenarioMarks.None, standing.Marks);
        Assert.Equal(ordered.Starts, standing.Current.Starts);
        Assert.Equal((ordered.IncludedCalls.Count, ordered.Conditions.Count), (standing.Current.IncludedCalls.Count, standing.Current.Conditions.Count));
        // A start that comes back returns to its place among the others.
        string[] noRail2 = [.. Nodes.Where(n => n != "rail_2")];
        var unbound = Compile(Bridge(), noRail2);
        standing.Rebind(new(unbound.Package, unbound.Provenance));
        Assert.Equal(1, standing.Marks.Starts);
        standing.Rebind(new(back.Package, back.Provenance));
        Assert.Equal(ordered.Starts, standing.Current.Starts);

        // The files edited while the map was closed: the restored scenario has no primary, and opening a binding of the marked
        // primary's definition stands in too; the revert gives the place back, nothing marked, every start in its place.
        AnimationScenarioSource rootedSource = new(rooted.Package, rooted.Provenance), backSource = new(back.Package, back.Provenance);
        var closed = AnimationScenarioSession.Restore(rootedSource, new AnimationScenarioSession(source, ordered).Save());
        Assert.Equal((null, true, false), (closed.Current.Primary, closed.StandsInFor(1), closed.StandsInFor(3)));
        closed.StandIn(1);
        Assert.Equal(20d, closed.Current.Start(Guid.Empty)!.Seconds);
        var reverted = AnimationScenarioSession.Restore(backSource, closed.Save());
        Assert.Equal(AnimationScenarioMarks.None, reverted.Marks);
        Assert.Equal(ordered.Starts, reverted.Current.Starts);
        Assert.Equal(ordered.IncludedCalls.Count, reverted.Current.IncludedCalls.Count);

        // rail_2's node gone, rail_1 stands in: another definition opened makes it an ordinary start that still stands in (also
        // after a reopen); the revert there gives the place back; opening it again (Back) makes it the primary stand-in again and
        // the revert gives the place back: rail_* runs once.
        static void Open(AnimationScenarioSession session, int entry)
        {
            if (session.StandsInFor(entry)) session.StandIn(entry); else session.Set(session.Current.WithPrimary(entry));
        }
        static (bool, int, double)[] Key(AnimationScenario s) => [.. s.Starts.Select(t => (t.Id == Guid.Empty, t.Entry, t.Seconds))];
        AnimationScenarioSource unboundSource = new(unbound.Package, unbound.Provenance);
        int rail1 = unbound.Package.Entries.First(e => e.Index > 0 && e.RootName == "rail_1").Index;
        var rails = AnimationScenario.For(3).WithStartTime(Guid.Empty, 5).WithStart(1, 20).IncludeAll(package, Token).Scenario;
        var away = new AnimationScenarioSession(source, rails);
        away.Rebind(unboundSource); away.StandIn(rail1); Open(away, 1);
        var awayReopened = AnimationScenarioSession.Restore(unboundSource, away.Save());
        Assert.Equal((1, 1), (awayReopened.Current.Primary, awayReopened.Marks.Starts));
        var awayReverted = AnimationScenarioSession.Restore(backSource, awayReopened.Save());
        Assert.Equal(Key(rails.WithPrimary(1)), Key(awayReverted.Current));
        Assert.Equal(AnimationScenarioMarks.None, awayReverted.Marks);
        Open(awayReopened, rail1);
        Assert.Equal((rail1, 5d), (awayReopened.Current.Primary, awayReopened.Current.Start(Guid.Empty)!.Seconds));
        awayReopened.Rebind(backSource);
        Assert.Equal(AnimationScenarioMarks.None, awayReopened.Marks);
        Assert.Equal(Key(rails), Key(awayReopened.Current));
        Assert.Equal(rails.IncludedCalls.Count, awayReopened.Current.IncludedCalls.Count);

        // An exclusion under the stand-in takes the marked primary's same call too: the undo does not bring it back.
        var under = new AnimationScenarioSession(source, ordered);
        under.Rebind(rootedSource); under.StandIn(1);
        under.Set(under.Current.IncludeAll(rooted.Package, Token).Scenario);
        string excludedCall = under.Current.IncludedCalls.First(p => !p.StartsWith("/@", StringComparison.Ordinal) && p.Count(c => c == '/') == 1);
        string? Record(AnimationCompiler.Evaluation compiled, string path) => compiled.Provenance!.FindEvent(Guid.ParseExact(path.Split(':')[1], "N"))?.RecordId;
        int markedUnder = under.Marks.Paths;
        under.Exclude(under.Current.WithInclusion(excludedCall, false, token: Token), [excludedCall]);
        Assert.Equal(markedUnder - 1, under.Marks.Paths);
        under.Rebind(backSource);
        Assert.DoesNotContain(under.Current.IncludedCalls, p => !p.StartsWith("/@", StringComparison.Ordinal) && p.Count(c => c == '/') == 1 && Record(back, p) == Record(rooted, excludedCall));

        // This animation only excludes the other starts whole (their own paths): their marked call paths go with them.
        var only = new AnimationScenarioSession(source, scenario);
        only.Rebind(editedSource);
        int markedOnly = only.Marks.Paths;
        only.Exclude(only.Current with { IncludedCalls = AnimationScenario.For(1).IncludeAll(edited.Package, Token).Scenario.IncludedCalls },
            [.. only.Current.Starts.Where(s => s.Id != Guid.Empty).Select(s => s.Path)]);
        Assert.InRange(only.Marks.Paths, 1, markedOnly - 1);
        only.Rebind(new(undone.Package, undone.Provenance));
        Assert.DoesNotContain(only.Current.IncludedCalls, p => p.StartsWith("/@", StringComparison.Ordinal));

        // Keep, saved, then reopened on files that mark more of that kind (edited while the map was closed): it asks again.
        var keeping = new AnimationScenarioSession(source, scenario);
        keeping.Rebind(editedSource); keeping.KeepMarked("paths");
        var further = Compile(Bridge(thirdCall: false, root: "piece"));
        var reopenedMore = AnimationScenarioSession.Restore(new(further.Package, further.Provenance), keeping.Save());
        Assert.True(reopenedMore.Marks.Paths > keeping.Marks.Paths);
        Assert.DoesNotContain("paths", reopenedMore.KeptMarks);
    }

    // Must-have (retail data): destroy_tr_bridge's structure as m4 ships it.
    [Fact]
    public async Task TheShippedBridgeGraphHasItsBackLinkReleaseAndIfNot()
    {
        string? given = Environment.GetEnvironmentVariable("ZSTUDIO_SOURCE_PROJECT");
        if (string.IsNullOrEmpty(given)) return;
        string temp = Path.Combine(Path.GetTempPath(), "zstudio-graph-" + Guid.NewGuid().ToString("N"));
        try
        {
            foreach (string folder in new[] { SourceProject.DataFolder, SourceProject.GameGenFolder }) Copy(Path.Combine(given, folder), Path.Combine(temp, folder));
            var snapshot = await SourceWorlds.LoadSceneAsync(temp, "m4", token: Token);
            var package = snapshot.Animations!;
            var index = AnimationGraphIndex.Create(package, snapshot.AnimationProvenance, AnimationGraphOptions.For(snapshot.AnimationDefinitions, map: "m4"), Token);
            var bridge = package.Entries.First(e => e.Name == "destroy_tr_bridge");
            var graph = AnimationGraph.Create(index, bridge.Index, token: Token);
            var rails = graph.Nodes().Rows.Single(n => n.Reference?.Name == "rdrail_destroy0*");
            Assert.Equal(8, rails.Reference!.EntryCount);
            Assert.Contains(graph.Edges().Rows, e => e.BackLink && e.From == rails.Id && e.To == AnimationGraph.ActivationId(bridge.Index));
            var splash = bridge.Sequences.Single(s => s.Name == "mainsplash");
            Assert.Contains(graph.Edges().Rows, e => e.Relation == AnimationGraphRelation.Release && e.To == AnimationGraph.SequenceId(bridge.Index, splash.Id));
            var flare = Assert.Single(graph.Blocks(bridge.Index, bridge.Sequences.Single(s => s.Name == "flare_sequence").Id));
            Assert.Equal("if true (nothing)", flare.Branches[0].Label);
            Assert.Equal("At least 5 of 8", Assert.Single(graph.Nodes().Rows[0].Activation!.Prerequisites).Header);
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

    // Must-have: the Timing view's projection of a real run (design §9): lanes per sequence under the start's group, the
    // waiting-for-release band and the release mark pointing at the lane it released, condition and excluded-call marks,
    // threshold brackets from observed anchors, and the Log; a seek back replays the same phases.
    // The fixture's world: bridge (healthy, piece under it) and the three rails.
    private static AnimationPreviewContext Context(AnimationPackage package)
    {
        var doc = new AssetDocument("fixture", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
        doc.Scene.Models.Add(new(0, [Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [], []));
        doc.Scene.Nodes.Add(new(0, "world", "world", null, [], [1, 4, 5, 6], new(), new()));
        for (int i = 0; i < Nodes.Length; i++)
            doc.Scene.Nodes.Add(new(i + 1, Nodes[i], "object3d", 0, i is 1 or 2 ? [1] : [0], i == 0 ? [2, 3] : [],
                new System.Text.Json.Nodes.JsonObject { ["flags"] = 4 },
                new System.Text.Json.Nodes.JsonObject { ["flags"] = 0, ["opacity"] = 1f, ["scale"] = JsonData.Vector(Vector3.One), ["transform"] = new System.Text.Json.Nodes.JsonArray(1, 0, 0, 0, 1, 0, 0, 0, 1, 0, 0, 0) }));
        return new AnimationPreviewContext { Package = package, World = doc };
    }

    [Fact]
    public void TheTimingProjectionShowsTheRunOnTheSharedClock()
    {
        var evaluated = Compile(Bridge()); var package = evaluated.Package;
        var context = Context(package);
        AnimationScheduledStart[] starts = [new(Guid.Empty, 1, 0)];
        var player = new AnimationPlayer(context, 1, 1, false, Token, new AnimationSchedule(starts, package.Entries.Count))
            { ExecutionPolicy = new(rules: new() { Player = AnimationPlayerReference.None }) };
        var frame = player.AdvanceTo(1, token: Token);
        var index = AnimationGraphIndex.Create(package, evaluated.Provenance, token: Token);
        var timing = AnimationTiming.Create(frame, package, new() { Starts = starts, Index = index }, Token);
        AnimationTimingLane Lane(string name) => timing.AllLanes.Single(l => l.Kind == AnimationTimingLaneKind.Sequence && l.Label == name);
        var group = Assert.Single(timing.Lanes, l => l.Kind == AnimationTimingLaneKind.Group);
        Assert.Equal(("destroy_bridge · bridge #1", true, 0d), (group.Label, group.Expanded, group.Started));
        Assert.Equal(["Cleanup", "healthy_to_destroyed", "flare_sequence", "chance", "move_piece", "mainsplash"],
            timing.Lanes.Where(l => l.Group == group.Id).Select(l => l.Label));
        Assert.Equal(3, Lane("healthy_to_destroyed").Marks.Count(m => m.Kind == AnimationTimingMarkKind.Excluded));
        var condition = Lane("flare_sequence").Marks.Single(m => m.Kind == AnimationTimingMarkKind.Condition);
        Assert.Equal((false, false, "false · game"), (condition.Value, condition.Forced, condition.Result));
        var release = Lane("move_piece").Marks.Single(m => m.Kind == AnimationTimingMarkKind.Release);
        Assert.Equal((Lane("mainsplash").Id, "released mainsplash"), (release.TargetLane, release.Result));
        var later = Lane("move_piece").Marks.Single(m => m.Kind == AnimationTimingMarkKind.Executed);
        Assert.Equal(("≥ 0.1 s after previous", 0d, .1), (later.Bracket!.Label, later.Bracket.Anchor, later.Bracket.End), new BracketComparer());
        Assert.Contains(Lane("mainsplash").Bands, b => b.Kind == AnimationTimingBandKind.WaitingForRelease);
        Assert.Contains(Lane("mainsplash").Marks, m => m.Kind == AnimationTimingMarkKind.Loop);
        var skipped = Lane("chance").Marks.Single(m => m.Kind == AnimationTimingMarkKind.Skipped);
        Assert.Equal((3, "skipped · branch taken"), (skipped.Count, skipped.Result)); Assert.Equal(.5, skipped.Start, 6);
        Assert.Contains(timing.Log, r => r.Result == "released mainsplash" && r.Mark == release.Id);
        Assert.True(timing.Log.Zip(timing.Log.Skip(1)).All(p => p.First.Time <= p.Second.Time));
        // A seek back restores the phases from a checkpoint and replays them exactly.
        var replay = player.AdvanceTo(.2, true, Token); Assert.All(replay.Phases, p => Assert.True(p.Time <= .2 + 1e-9));
        Assert.Equal(frame.Phases, player.AdvanceTo(1, token: Token).Phases);
    }

    // Must-have: Timing's lanes and the graph's run overlay read one sequence state rule (preview.md, "Source-world animation
    // session"): a sequence still waiting when its instance ended is stopped, never complete, and one without evidence is not
    // started. A called child the player released after it finished keeps its own start: its group and its animation-clock
    // bracket are anchored at its launch, never at its first occurrence.
    [Fact]
    public void TimingAndTheRunOverlayShareOneSequenceStateRule()
    {
        var evaluated = Compile("""
            ANIMATION_DEFINITIONS ( ANIMATION_LIST (
              ANIMATION_DEFINITION (
                NAME ( bridge ) ANIMATION_NAME ( caller ) ANIMATION_ROOT_NAME ( healthy ) RESET_TIME ( -1 )
                SEQUENCE_DEFINITION ( NAME ( launch ) CALL_ANIMATION ( NAME ( faller ) START_TIME ( ANIMATION_OFFSET 0.2 ) ) )
                SEQUENCE_DEFINITION ( NAME ( idle ) ACTIVATION ( ON_CALL ) OBJECT_ACTIVE_STATE ( NAME ( piece ) STATE ( ACTIVE ) ) )
                SEQUENCE_DEFINITION ( NAME ( halt ) STOP_ANIMATION ( NAME ( caller ) START_TIME ( ANIMATION_OFFSET 2 ) ) )
                SEQUENCE_DEFINITION ( NAME ( late ) OBJECT_ACTIVE_STATE ( NAME ( piece ) STATE ( INACTIVE ) START_TIME ( ANIMATION_OFFSET 5 ) ) ) )
              ANIMATION_DEFINITION (
                NAME ( rail_1 ) ANIMATION_NAME ( faller ) RESET_TIME ( -1 )
                SEQUENCE_DEFINITION ( NAME ( fall ) OBJECT_ACTIVE_STATE ( NAME ( piece ) STATE ( INACTIVE ) START_TIME ( ANIMATION_OFFSET 0.5 ) ) ) )
            ) )
            """);
        var package = evaluated.Package;
        var index = AnimationGraphIndex.Create(package, evaluated.Provenance, token: Token);
        var graph = AnimationGraph.Create(index, 1, token: Token);
        var scenario = AnimationScenario.For(1).IncludeAll(package, Token).Scenario with { Player = AnimationPlayerReference.None };
        var player = new AnimationPlayer(Context(package), 1, scenario.Seed, false, Token, scenario.Schedule(package.Entries.Count)) { ExecutionPolicy = scenario.ToExecutionPolicy() };
        var options = new AnimationTimingOptions { Starts = scenario.Starts, Index = index };
        Dictionary<string, AnimationSequenceRunState> States(AnimationFrame frame, out AnimationTiming timing)
        {
            var projected = timing = AnimationTiming.Create(frame, package, options, Token);
            var overlay = AnimationRunOverlay.Create(graph, frame, scenario, Token);
            var group = projected.AllLanes.Single(l => l.Kind == AnimationTimingLaneKind.Group && l.Members.SequenceEqual([1]));
            Dictionary<string, AnimationSequenceRunState> states = [];
            foreach (var lane in projected.AllLanes.Where(l => l.Group == group.Id && l.Kind == AnimationTimingLaneKind.Sequence))
            {
                if (overlay.Sequence(AnimationGraph.SequenceId(1, lane.Sequences[0].Sequence)) is { } run) Assert.Equal((lane.Label, run.State), (lane.Label, lane.Run!.Value));
                states[lane.Label] = lane.Run!.Value;
            }
            return states;
        }
        var running = player.AdvanceTo(1, token: Token);
        Assert.Equal(new Dictionary<string, AnimationSequenceRunState>
        {
            ["Cleanup"] = AnimationSequenceRunState.NotReached, ["launch"] = AnimationSequenceRunState.Complete, ["idle"] = AnimationSequenceRunState.WaitingForRelease,
            ["halt"] = AnimationSequenceRunState.WaitingForThreshold, ["late"] = AnimationSequenceRunState.WaitingForThreshold,
        }, States(running, out var timing));
        Assert.Contains(timing.AllLanes.Single(l => l.Label == "idle").Bands, b => b.Open && b.Label == "waiting · no releaser left");
        // The child finished at 0.7 s and the player released it: its start is its launch at 0.2 s.
        Assert.DoesNotContain(running.Instances, i => i.Entry == 2);
        var call = timing.AllLanes.Single(l => l.Label == "launch").Marks.Single(m => m.Type == 24);
        var child = timing.AllLanes.Single(l => l.Kind == AnimationTimingLaneKind.Group && l.Members.SequenceEqual([2]));
        var fall = timing.AllLanes.Single(l => l.Group == child.Id && l.Label == "fall").Marks.Single(m => m.Type != 0 && m.Bracket != null);
        Assert.InRange(call.Start, .2 - AnimationSeconds.Tick - 1e-6, .2 + 1e-6);
        Assert.Equal((call.Start, call.Start), (child.Started!.Value, fall.Bracket!.Anchor));
        Assert.True(fall.Start > call.Start + .45);
        // halt stops the binding at 2 s: waits it left behind ended with it.
        Assert.Equal(new Dictionary<string, AnimationSequenceRunState>
        {
            ["Cleanup"] = AnimationSequenceRunState.NotReached, ["launch"] = AnimationSequenceRunState.Complete, ["idle"] = AnimationSequenceRunState.Stopped,
            ["halt"] = AnimationSequenceRunState.Complete, ["late"] = AnimationSequenceRunState.Stopped,
        }, States(player.AdvanceTo(3, token: Token), out _));
    }
    private sealed class BracketComparer : IEqualityComparer<(string, double, double)>
    {
        public bool Equals((string, double, double) a, (string, double, double) b) => a.Item1 == b.Item1 && Math.Abs(a.Item2 - b.Item2) < 1e-6 && Math.Abs(a.Item3 - b.Item3) < 1e-6;
        public int GetHashCode((string, double, double) value) => value.Item1.GetHashCode();
    }

    private static void Copy(string from, string to)
    {
        foreach (string directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        Directory.CreateDirectory(to);
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }
}
