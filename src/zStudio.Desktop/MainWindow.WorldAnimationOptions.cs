using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private object WorldAnimationCommandState => new
    {
        target = WorldAnimationTargetNow is { } target ? new { document = target.Document.SessionId, revision = target.Document.Revision, definition = target.Definition, entry = target.Entry } : null,
        mode = InAnimationMode,
        plan = worldAnimationPlan?.Id, provenance = worldAnimationPlan?.ProvenanceId,
        scenario = worldAnimationScenarioEpoch, configuration = WorldScenarioState(WorldAnimationConfiguration),
        state = worldAnimationSession?.State, prepared = worldAnimationSession != null, preparing = worldAnimationPreparing,
        time = worldAnimationSession?.State.Time ?? 0, playing = worldAnimationSession?.IsPlaying == true,
        range = new[] { worldAnimationSession?.RangeStart ?? worldAnimationRangeStart, worldAnimationSession?.RangeEnd ?? worldAnimationRangeEnd },
        rangeState = WorldAnimationRangeJson(), collisionMode = "terrain",
        proceduralYLimits = new { minimum = -500, maximum = 500, behavior = "stop", cameras = false, keyframes = false },
        manualActivation = "Scheduled starts ignore their activation prerequisites; every start the run makes is checked as in the game. The preview does not simulate a hit or mission startup.",
        staleRun = worldAnimationSession?.CurrentFrame == null ? worldAnimationSession?.LastRunId : null
    };

    /// <summary>The range as MCP reports it: auto or custom, the flyout's words, Auto's measurement, whether it is measuring, and the hint at a cut end.</summary>
    private object WorldAnimationRangeJson()
    {
        var range = WorldAnimationRangeState();
        return AnimationRangeJson(worldAnimationRangeAuto, worldAnimationRangeExtended, worldAnimationDuration, worldAnimationMeasuring != null, range.Text, range.Hint, range.CanExtend);
    }
    /// <summary>The range's MCP form, shared by source maps and the compiled preview.</summary>
    internal static object AnimationRangeJson(bool auto, bool extended, AnimationDuration? measured, bool measuring, string text, string? hint, bool canExtend) => new
    {
        mode = auto ? "auto" : "custom", extended = !auto && extended, text, measuring, hint, canExtend,
        auto = measured is { } d ? new
        {
            kind = d.Kind.ToString(), seconds = d.Seconds, explanation = d.Explanation, afterLastTimed = d.AfterLastTimed, lastTimed = d.LastTimedSeconds, timedOut = d.TimedOut,
        } : null,
    };

    private object WorldAnimationRuntimeData(string section, int offset, int limit, string query = "")
    {
        if (offset < 0 || limit is < 1 or > 200) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–200.");
        if (section == "status") return WorldAnimationCommandState;
        if (section == "player") return RuntimePlayer(offset, limit);
        if (section == "notices")
        {
            if (!InAnimationMode) throw new StudioCommandException("not_ready", "Notices belong to the animation opened in Animation mode.");
            return Page(AnimationNotices(), new JsonObject { ["offset"] = offset, ["limit"] = limit, ["query"] = query }, n => n.Kind + " " + n.Text, project: n => n.Json(),
                maximumRowBytes: n => 1024 + InspectionResultBudget.Text(n.Text) + InspectionResultBudget.Text(n.Node ?? "") + InspectionResultBudget.Text(n.Lane ?? "")).Data;
        }
        var frame = worldAnimationSession?.CurrentFrame ?? worldAnimationSession?.LastFrame;
        if (frame == null) return new { unavailable = true, reason = "No run evaluated; selection is passive." };
        JsonObject args = new() { ["offset"] = offset, ["limit"] = limit, ["query"] = query };
        return section switch
        {
            "events" => new { page = Page(frame.Trace, args, e => e.Name + " " + e.Status, maximumRowBytes: e => 1024 + InspectionResultBudget.Text(e.Name) + InspectionResultBudget.Text(e.Status) + InspectionResultBudget.Text(e.InstancePath)).Data, dropped = frame.TraceDropped, stale = worldAnimationSession?.CurrentFrame == null },
            "sequences" => Page(frame.Sequences, args, s => s.Name + " " + s.State, maximumRowBytes: s => 1024 + InspectionResultBudget.Text(s.Name) + InspectionResultBudget.Text(s.State) + InspectionResultBudget.Text(s.InstancePath)).Data,
            "problems" => Page(frame.Diagnostics, args, d => d, maximumRowBytes: d => 256 + InspectionResultBudget.Text(d)).Data,
            "scene" => Page(frame.Nodes, args, n => n.SourceNode.ToString(CultureInfo.InvariantCulture), maximumRowBytes: _ => 2048).Data,
            "instances" => Page(frame.Instances, args, i => i.Name, maximumRowBytes: i => 1024 + InspectionResultBudget.Text(i.Name) + InspectionResultBudget.Text(i.Path)).Data,
            "conditions" => Page(frame.Conditions, args, c => c.Event.ToString(), maximumRowBytes: c => 768 + InspectionResultBudget.Text(c.InstancePath) + InspectionResultBudget.Text(c.Unavailable ?? "")).Data,
            // Each start attempt's activation prerequisites (met, refused, or ignored by the scenario's rule): evidence only.
            "prerequisites" => new { page = Page(frame.PrerequisiteStarts, args, p => p.Name + " " + p.Outcome, maximumRowBytes: p => 1024 + InspectionResultBudget.Text(p.Name) + InspectionResultBudget.Text(p.InstancePath) + 384L * p.Result.Checks.Count).Data, dropped = frame.PrerequisiteStartsDropped },
            _ => throw new StudioCommandException("invalid_argument", "Unknown runtime section.")
        };
    }

    /// <summary>
    /// Speed, volume, mute, Follow camera and Loop range change how the run is played and heard (Loop range only replays the
    /// playback range when playback reaches its end), never the run: a pending source draft keeps them. Effects and the playback
    /// range change what the run shows and covers, so drafts are resolved first (the GUI asks, MCP refuses).
    /// </summary>
    private static bool PlaybackOnlyOption(string name) => name is "speed" or "volume" or "mute" or "followCamera" or "replay";
    /// <summary>Where an option a source map's animation_options no longer takes lives now (code moved, no aliases), or null.</summary>
    internal static string? SourceMovedOption(string name) => name switch
    {
        "playbackRange" => null,
        "range" or "autoRange" => "In a source map the playback range is animation_options playbackRange [start, end] (the transport's range flyout); its Auto and Extend +5 s are animation_transport auto and extend.",
        "view" or "labelWidth" or "fit" or "visibleRange" or "thresholds" => $"{name} is the Timing view's: animation_timing options.",
        "worldPath" or "root" => null,
        _ => AnimationEditor.MovedToScenario(name),
    };
    private Task WorldAnimationOptionAsync(string name, object value, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        WorldAnimationOptions([(name, value)]);
        return Task.CompletedTask;
    }
    /// <summary>
    /// A source map's transport options (the transport's controls and MCP animation_options): every key and value is checked (a
    /// moved key, a pending draft for one that changes the run, a value out of range) before any applies, so a refused request
    /// changes nothing. One that applies clears the transport's failure line.
    /// </summary>
    private void WorldAnimationOptions(IReadOnlyList<(string Name, object Value)> changes)
    {
        foreach (var (name, _) in changes) if (SourceMovedOption(name) is { } moved) throw new StudioCommandException("moved", moved);
        if (changes.Any(c => !PlaybackOnlyOption(c.Name))) RequireNoDrafts(shownDocument);
        var apply = changes.Select(c => WorldAnimationOption(c.Name, c.Value)).ToArray();
        foreach (var change in apply) change();
        animationFailure = null;
        RefreshWorldAnimationRun();
    }
    /// <summary>One checked option: what applies it.</summary>
    private Action WorldAnimationOption(string name, object value)
    {
        JsonNode data = value as JsonNode ?? JsonSerializer.SerializeToNode(value) ?? throw new StudioCommandException("invalid_argument", "Use an explicit value.");
        bool Flag() => TimingOption<bool>(data, $"{name} is true or false.");
        double Number(JsonNode node)
        {
            double number = TimingOption<double>(node, $"{name} takes a number.");
            return double.IsFinite(number) ? number : throw new StudioCommandException("invalid_argument", "Use a finite number.");
        }
        switch (name)
        {
            case "speed":
                double speed = Number(data); if (Array.IndexOf(AnimationPanel.SpeedChoices, speed) < 0) throw new StudioCommandException("invalid_argument", "Speed must be 0.25, 0.5, 1 or 2 (the transport's Speed choices).");
                return () => { if (worldAnimationSession != null) worldAnimationSession.Speed = speed; worldAnimationSpeed = speed; };
            case "volume":
                double volume = Number(data); if (volume is < 0 or > 1) throw new StudioCommandException("invalid_argument", "Volume must be 0–1.");
                return () => { if (worldAnimationSession != null) worldAnimationSession.Volume = volume; worldAnimationVolume = volume; };
            case "mute": bool mute = Flag(); return () => { worldAnimationMuted = mute; if (worldAnimationSession != null) worldAnimationSession.Muted = mute; };
            case "replay": bool replay = Flag(); return () => { worldAnimationReplay = replay; if (worldAnimationSession != null) worldAnimationSession.Replay = replay; };
            case "followCamera": bool follow = Flag(); return () => { worldAnimationFollowCamera = follow; if (worldAnimationSession != null) worldAnimationSession.FollowCamera = follow; };
            case "effects": bool effects = Flag(); return () => { worldAnimationEffects = effects; if (worldAnimationSession != null) worldAnimationSession.Effects = effects; ApplyWorldAnimationScreenEffects(worldAnimationSession?.CurrentFrame); };
            case "playbackRange":
                double[] range = data is JsonArray { Count: 2 } pair && pair[0] is { } a && pair[1] is { } b ? [Number(a), Number(b)]
                    : data is JsonArray ? throw new StudioCommandException("invalid_argument", "Use two range endpoints.") : [0, Number(data)];
                if (range[0] < 0 || range[1] > 3600 || range[1] <= range[0]) throw new StudioCommandException("invalid_argument", "Use an increasing range within 0–3600 seconds.");
                // An edited range is Custom until Auto.
                return () =>
                {
                    worldAnimationSession?.SetRange(range[0], range[1]); worldAnimationRangeStart = Math.Floor(range[0] * 60 + 1e-7) / 60; worldAnimationRangeEnd = Math.Ceiling(range[1] * 60 - 1e-7) / 60;
                    worldAnimationRangeAuto = false; worldAnimationRangeExtended = false;
                };
            default: throw new StudioCommandException("unsupported", "This option is not part of the animation transport. The Timing view's presentation is animation_timing options, the scenario animation_scenario; LOD, difficulty and scene presentation are the world's controls.");
        }
    }

    private void ValidateWorldCondition(int entry, string path, Guid ev)
    {
        int root = WorldAnimationTargetNow?.Entry ?? -1;
        if (root < 0 || (path.Length == 0 ? root : ValidateWorldAnimationCallPath(root, path)) != entry)
            throw new StudioCommandException("stale_record", "The condition does not belong to this preview lineage.");
        var package = shownDocument!.Document.Animations!;
        var value = package.Entries[entry].AllSequences.SelectMany(s => s.Events).FirstOrDefault(e => e.Id == ev);
        if (value?.Type is not (31 or 33)) throw new StudioCommandException("invalid_argument", "Only an IF or ELSEIF condition event accepts a scoped override.");
    }
}
