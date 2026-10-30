using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

public partial class AnimationEditor
{
    private Task optionWork = Task.CompletedTask;
    private string? previewOperationFailure;
    internal int PreviewLod => Lod.SelectedIndex;
    /// <summary>The compiled preview's options and transport, as preview_state.animation and animation_options report them.</summary>
    internal object PreviewState() => new
    {
        entry = entryIndex, playing, time = frame?.Time, range = new[] { 0, rangeEnd },
        rangeState = MainWindow.AnimationRangeJson(!customRange, extendedRange, duration, measuring != null, RangeDescription, RangeHint, CanExtend),
        options = Options, height = scenario.Height, lod = Lod.SelectedIndex, difficulty = Mw3WorldPath == null ? SelectedDifficulty.ToString() : null, mission = MissionArchive,
        collisionMode = GroundCollision.IsChecked == true ? "plane" : "none", camera = frame?.Camera is { } shownCamera ? new { name = shownCamera.Name, node = shownCamera.SourceNode, fieldOfView = shownCamera.FieldOfView } : null,
        proceduralYLimits = new { minimum = -500, maximum = 500, behavior = "stop", cameras = false, keyframes = false },
        speed, replay, mute = muted, volume, scenario = scenario.Summary, root = context?.ResolveRoot(Entry), rootOverridden = RootOverridden,
        status, failure = PreviewFailure, loading = LoadingPanel.Visibility == Visibility.Visible ? LoadingText.Text : null,
    };
    internal object RuntimeData(string section, int offset, int limit, string query = "")
    {
        if (offset < 0 || limit is < 1 or > 200) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–200.");
        JsonObject args = new() { ["offset"] = offset, ["limit"] = limit, ["query"] = query };
        JsonObject Page<T>(IEnumerable<T> rows, Func<T, string> search, Func<T, long> maximumRowBytes, Func<T, object>? project = null)
            => MainWindow.Page(rows, args, search, project, maximumRowBytes: maximumRowBytes).Data.AsObject();
        if (section == "problems")
        {
            var problems = Page(CurrentProblems(), p => p.Message + " " + p.Category + " " + p.Scope, p =>
                1024 + Text(p.Severity) + Text(p.Category) + Text(p.Scope) + Text(p.Message) + Text(p.Details)
                // FileProblem is serialized too, including its computed Scope and Details. Bound their lengths
                // from the retained path instead of constructing them again to measure the result.
                + (p.FileProblem is { } file ? InspectionResultBudget.Problem(file) : 4));
            problems["approximation"] = "Preview includes game-dependent approximations."; return problems;
        }
        if (frame == null) return new { unavailable = true, failure = PreviewFailure };
        return section switch
        {
            "events" => EventPage(),
            "sequences" => Page(frame.Sequences, s => s.Name + " " + s.State, s => 512 + Text(s.Name) + Text(s.State)),
            // Match scene_nodes: reserve the escaped row before projecting bounded metadata, retaining the
            // original node identity and the shared inspection_truncated disclosure. Full metadata is export-only.
            "scene" => Page(context?.Scene.Nodes ?? [], n => n.Name + " " + n.Class,
                n => InspectionResultBudget.SceneNode(n.Name, n.Class, null),
                n => new { n.Index, n.Name, n.Class, Metadata = JsonData.PreviewObject(n.Metadata, 64, 1024) }),
            _ => PreviewState()
        };
        JsonObject EventPage()
        {
            var page = Page(frame.Trace, e => e.Name + " " + e.Status + (e.Stopped is { } stopped ? " " + stopped : ""), e => 768 + Text(e.Name) + Text(e.Status) + Text(e.Stopped));
            page["dropped"] = frame.TraceDropped; return page;
        }
        static long Text(string? value) => InspectionResultBudget.Text(value);
    }
    /// <summary>One compiled animation_options key (<see cref="SetPreviewOptionsAsync"/>).</summary>
    internal Task SetPreviewOptionAsync(string name, JsonNode value) => SetPreviewOptionsAsync([(name, value)]);
    /// <summary>
    /// animation_options on a compiled animation: the view's presentation (lod, difficulty, faceOrientation, map, grid, height,
    /// collision, horizon, effects, followCamera) and the transport's speed, replay, mute and volume. What the run is made of
    /// (seed, phase, conditions, the player, the beam origin, the range, the world, the bound root) is animation_scenario's.
    /// Every key and value is checked before any applies, so a refused request changes nothing; then each applies in order,
    /// after <paramref name="beforeEach"/> (the caller's own check that its preview is still the one shown).
    /// </summary>
    internal async Task SetPreviewOptionsAsync(IReadOnlyList<(string Name, JsonNode Value)> changes, Action? beforeEach = null)
    {
        if (HasPendingDrafts) throw new StudioCommandException("pending_drafts", "Resolve preview drafts first.");
        var apply = changes.Select(c => PreviewOption(c.Name, c.Value)).ToArray();
        foreach (var change in apply) { beforeEach?.Invoke(); await ApplyPreviewOptionAsync(change); }
    }
    /// <summary>One checked option: what applies it.</summary>
    private Action PreviewOption(string name, JsonNode value)
    {
        bool Flag()
        {
            try { return value.GetValue<bool>(); }
            catch (Exception e) when (e is InvalidOperationException or FormatException) { throw new StudioCommandException("invalid_argument", $"{name} is true or false."); }
        }
        double Numeric(double min, double max)
        {
            double n;
            try { n = value.GetValue<double>(); }
            catch (Exception e) when (e is InvalidOperationException or FormatException) { throw new StudioCommandException("invalid_argument", $"{name} takes a number."); }
            if (!double.IsFinite(n) || n < min || n > max) throw new StudioCommandException("invalid_argument", $"Value must be {min}–{max}.");
            return n;
        }
        int Integer(int min, int max)
        {
            int n;
            try { n = value.GetValue<int>(); }
            catch (Exception e) when (e is InvalidOperationException or FormatException) { throw new StudioCommandException("invalid_argument", $"{name} takes an integer."); }
            if (n < min || n > max) throw new StudioCommandException("invalid_argument", "Integer is outside the supported range.");
            return n;
        }
        switch (name)
        {
            case "map": { bool shown = Flag(); return () => ShowLevel.IsChecked = shown; }
            case "horizon": { bool shown = Flag(); return () => ShowHorizon.IsChecked = shown; }
            case "grid": { bool shown = Flag(); return () => ShowGrid.IsChecked = shown; }
            case "faceOrientation": { bool shown = Flag(); return () => ShowFaceOrientation.IsChecked = shown; }
            case "collision": { bool on = Flag(); return () => GroundCollision.IsChecked = on; }
            case "followCamera": { bool on = Flag(); return () => FollowCamera.IsChecked = on; }
            case "effects": { bool on = Flag(); return () => Lighting.IsChecked = on; }
            case "height": { string height = Numeric(-999, 999).ToString(CultureInfo.InvariantCulture); return () => PreviewHeight.Text = height; }
            case "lod": { int lod = Integer(0, Math.Max(0, Lod.Items.Count - 1)); return () => Lod.SelectedIndex = lod; }
            case "difficulty":
                {
                    string? text = null;
                    try { text = value.GetValue<string>(); } catch (Exception e) when (e is InvalidOperationException or FormatException) { }
                    if (!Enum.TryParse<MissionDifficulty>(text, out var difficulty) || !Enum.IsDefined(difficulty)) throw new StudioCommandException("invalid_argument", "Difficulty must be Easy, Medium or Hard.");
                    if (Mw3WorldPath != null) throw new StudioCommandException("unsupported", "MechWarrior 3 previews use missions instead of difficulty.");
                    return () => Difficulty.SelectedItem = difficulty;
                }
            case "speed":
                {
                    double chosen = Numeric(.25, 2);
                    if (Array.IndexOf(new[] { .25, .5, 1, 2 }, chosen) < 0) throw new StudioCommandException("invalid_argument", "Speed must be 0.25, 0.5, 1 or 2.");
                    return () => SetTransport(newSpeed: chosen);
                }
            case "replay": { bool replay = Flag(); return () => SetTransport(newReplay: replay); }
            case "mute": { bool mute = Flag(); return () => SetTransport(newMuted: mute); }
            case "volume": { double volume = Numeric(0, 1); return () => SetTransport(newVolume: volume); }
            default:
                if (MovedToScenario(name) is { } moved) throw new StudioCommandException("moved", moved);
                throw new StudioCommandException("unknown_option", name);
        }
    }
    /// <summary>Applies one checked option and waits for the work it started (a scene reload, a seek), reporting what superseded it.</summary>
    private async Task ApplyPreviewOptionAsync(Action apply)
    {
        if (HasPendingDrafts) throw new StudioCommandException("pending_drafts", "Resolve preview drafts first.");
        optionWork = Task.CompletedTask;
        previewOperationFailure = null;
        long initialSeek = seekGeneration;
        long initialLod = lodGeneration, initialLevel = levelGeneration;
        var operationToken = PreviewOperation.Current;
        apply();
        Task previous;
        do { previous = optionWork; await previous; } while (previous != optionWork);
        // The job wrapper reports originating cancellation after GUI recovery.
        // It must take precedence over recovery's replacement seek identity.
        if (operationToken.IsCancellationRequested) return;
        if (disposed) throw new StudioCommandException("context_changed", "Animation preview was replaced.");
        if ((previewOperationFailure ?? renderFailure) is { } failure) throw new StudioCommandException("preview_unavailable", failure);
        if (seekGeneration != initialSeek && (seekOperationToken != operationToken || publishedSeekGeneration != seekGeneration))
            throw new StudioCommandException("context_changed", "The requested animation update was superseded by another seek.");
        if (lodGeneration != initialLod && (lodOperationToken != operationToken || publishedLodGeneration != lodGeneration) ||
            levelGeneration != initialLevel && (levelOperationToken != operationToken || publishedLevelGeneration != levelGeneration))
            throw new StudioCommandException("context_changed", "The requested animation scene options were superseded.");
    }
    /// <summary>Where an option the compiled preview took before the redesign lives now (no aliases), or null.</summary>
    internal static string? MovedToScenario(string name) => name switch
    {
        "seed" => "The random seed is the scenario's: animation_scenario inputs {seed}.",
        "condition" => "Conditions are switched one by one: animation_scenario conditions {instancePath, entry, event, value} (the graph's Game · True · False).",
        "activationOrigin" => "The beam origin is the scenario's: animation_scenario inputs {beamOrigin: [x, y, z]} ([] is the root).",
        "activationTarget" => "The player is the scenario's: animation_scenario inputs {player: {kind: custom, x, y, z}}.",
        "range" or "autoRange" or "playbackRange" => "The playback range is the scenario's: animation_scenario inputs {range: [0, end]} ([] is Auto); Extend +5 s and Auto are animation_transport extend and auto.",
        "worldPath" => "The mission scene is the scenario's World: animation_scenario inputs {world: path} (\"\" is automatic).",
        "root" => "The bound root is the scenario's: animation_scenario inputs {root: node} (null clears the override).",
        "phase" => "The phase is the scenario's: animation_scenario inputs {phase}.",
        "traceRange" or "fitTrace" or "followLog" or "problemFilter" => "The Dispatch, Event log and Problems tabs are gone: animation_timing (lanes, marks, log, options) and animation_runtime notices replace them.",
        _ => null,
    };
    internal async Task TransportAsync(string action, double seconds = 0)
    {
        if (HasPendingDrafts) throw new StudioCommandException("pending_drafts", "Resolve unfinished preview input first.");
        bool canRebuild = renderFailure != null && context != null && action is "seek" or "stop" or "previous" or "next" or "start";
        if (player == null && !canRebuild || LoadingPanel.Visibility == Visibility.Visible) throw new StudioCommandException("not_ready", renderFailure ?? LoadingText.Text);
        previewOperationFailure = null;
        switch (action)
        {
            case "play": if (!playing) TogglePlayback(); break;
            case "pause": Pause(); break;
            case "stop": pendingPlay = false; await SeekTransportAsync(0); break;
            case "start": await SeekTransportAsync(0); break;
            case "previous": await SeekTransportAsync(Math.Max(0, (frame?.Time ?? 0) - AnimationPlayer.StepSeconds)); break;
            case "next": await SeekTransportAsync(Math.Min(rangeEnd, (frame?.Time ?? 0) + AnimationPlayer.StepSeconds)); break;
            case "seek": if (!double.IsFinite(seconds) || seconds < 0 || seconds > rangeEnd) throw new StudioCommandException("invalid_argument", "Seek position must be within the preview range."); await SeekTransportAsync(seconds); break;
            default: throw new StudioCommandException("invalid_argument", "Unknown transport action.");
        }
        if (disposed) throw new StudioCommandException("context_changed", "Animation preview was replaced during transport.");
        if (previewOperationFailure != null) throw new StudioCommandException("preview_unavailable", previewOperationFailure);
    }
    private async Task SeekTransportAsync(double seconds)
    {
        long requested = seekGeneration + 1;
        await SeekAsync(seconds);
        PreviewOperation.Current.ThrowIfCancellationRequested();
        if (previewOperationFailure != null) throw new StudioCommandException("preview_unavailable", previewOperationFailure);
        if (disposed || seekGeneration != requested || publishedSeekGeneration != requested)
            throw new StudioCommandException("context_changed", "The requested animation seek was superseded.");
    }
    /// <summary>A scenario change from MCP: applied as the GUI's (stop, re-evaluate to the playhead), its failure reported.</summary>
    internal async Task ApplyScenarioAsync(AnimationScenario next)
    {
        if (HasPendingDrafts) throw new StudioCommandException("pending_drafts", "Resolve unfinished preview input first.");
        previewOperationFailure = null;
        await SetScenarioAsync(next);
        await AwaitOptionWorkAsync();
        if (disposed) throw new StudioCommandException("context_changed", "Animation preview was replaced.");
        if (PreviewFailure is { } failure && !Loading) throw new StudioCommandException("preview_unavailable", failure);
    }
}
