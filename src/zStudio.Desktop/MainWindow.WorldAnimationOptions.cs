using System.Globalization;
using System.IO;
using System.Numerics;
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
        target = WorldAnimationTargetNow is { } target ? new { document = target.Document.SessionId, revision = target.Revision, definition = target.Definition, entry = target.Entry } : null,
        mode = InAnimationMode,
        plan = worldAnimationPlan?.Id, provenance = worldAnimationPlan?.ProvenanceId,
        scenario = worldAnimationScenarioEpoch, configuration = WorldScenarioState(WorldAnimationConfiguration),
        state = worldAnimationSession?.State, prepared = worldAnimationSession != null, preparing = worldAnimationPreparing,
        time = worldAnimationSession?.State.Time ?? 0, playing = worldAnimationSession?.IsPlaying == true,
        range = new[] { worldAnimationSession?.RangeStart ?? worldAnimationRangeStart, worldAnimationSession?.RangeEnd ?? worldAnimationRangeEnd },
        duration = worldAnimationDuration, collisionMode = "terrain",
        proceduralYLimits = new { minimum = -500, maximum = 500, behavior = "stop", cameras = false, keyframes = false },
        manualActivation = "Scheduled starts ignore their activation prerequisites; every start the run makes is checked as in the game. The preview does not simulate a hit or mission startup.",
        staleRun = worldAnimationSession?.CurrentFrame == null ? worldAnimationSession?.LastRunId : null
    };

    private object WorldAnimationRuntimeData(string section, int offset, int limit, string query = "")
    {
        if (offset < 0 || limit is < 1 or > 200) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–200.");
        if (section == "status") return WorldAnimationCommandState;
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

    private async Task WorldAnimationOptionAsync(string name, object value, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        JsonNode data = value as JsonNode ?? JsonSerializer.SerializeToNode(value)!;
        bool FlagValue() => data.GetValue<bool>();
        double NumberValue() { double number = data.GetValue<double>(); if (!double.IsFinite(number)) throw new StudioCommandException("invalid_argument", "Use a finite number."); return number; }
        double[] Pair()
        {
            if (data is not JsonArray { Count: 2 } array) throw new StudioCommandException("invalid_argument", "Use two range endpoints.");
            return [array[0]!.GetValue<double>(), array[1]!.GetValue<double>()];
        }
        if (name is "view" or "labelWidth" or "fit" or "visibleRange" or "thresholds")
            throw new StudioCommandException("unsupported", $"{name} is the Timing view's: animation_timing options.");
        RequireNoDrafts();
        switch (name)
        {
            case "speed":
                double speed = NumberValue(); if (speed is not (.25 or .5 or 1 or 2 or 4)) throw new StudioCommandException("invalid_argument", "Speed must be 0.25, 0.5, 1, 2 or 4.");
                if (worldAnimationSession != null) worldAnimationSession.Speed = speed; worldAnimationSpeed = speed; break;
            case "volume":
                double volume = NumberValue(); if (volume is < 0 or > 1) throw new StudioCommandException("invalid_argument", "Volume must be 0–1.");
                if (worldAnimationSession != null) worldAnimationSession.Volume = volume; worldAnimationVolume = volume; break;
            case "mute": worldAnimationMuted = FlagValue(); if (worldAnimationSession != null) worldAnimationSession.Muted = worldAnimationMuted; break;
            case "replay": worldAnimationReplay = FlagValue(); if (worldAnimationSession != null) worldAnimationSession.Replay = worldAnimationReplay; break;
            case "followCamera": worldAnimationFollowCamera = FlagValue(); if (worldAnimationSession != null) worldAnimationSession.FollowCamera = worldAnimationFollowCamera; break;
            case "effects": worldAnimationEffects = FlagValue(); if (worldAnimationSession != null) worldAnimationSession.Effects = worldAnimationEffects; ApplyWorldAnimationScreenEffects(worldAnimationSession?.CurrentFrame); break;
            case "range" or "playbackRange":
                double[] range = data is JsonArray ? Pair() : [0, NumberValue()];
                if (!range.All(double.IsFinite) || range[0] < 0 || range[1] > 3600 || range[1] <= range[0]) throw new StudioCommandException("invalid_argument", "Use an increasing range within 0–3600 seconds.");
                worldAnimationSession?.SetRange(range[0], range[1]); worldAnimationRangeStart = Math.Floor(range[0] * 60 + 1e-7) / 60; worldAnimationRangeEnd = Math.Ceiling(range[1] * 60 - 1e-7) / 60; break;
            default: throw new StudioCommandException("unsupported", "This option is not part of the animation transport. The Timing view's presentation is animation_timing options, the scenario animation_scenario; LOD, difficulty and scene presentation are the world's controls.");
        }
        RefreshWorldAnimationRun();
        await Task.CompletedTask;
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

    private static Vector3? WorldAnimationPosition(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string[] parts = text.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3 || !float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z)
            || !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)) throw new StudioCommandException("invalid_argument", "Enter three finite XYZ numbers, or leave blank for default.");
        return new(x, y, z);
    }
}
