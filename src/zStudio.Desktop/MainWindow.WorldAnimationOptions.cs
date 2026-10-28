using System.Globalization;
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
        target = WorldAnimationTargetNow is { } target ? new { document = target.Document.SessionId, revision = target.Revision, definition = target.Definition, entry = target.Entry, pinned = worldAnimationPinned != null } : null,
        candidate = worldAnimationCandidate is { } candidate ? new { definition = candidate.Definition, entry = candidate.Entry } : null,
        plan = worldAnimationPlan?.Id, provenance = worldAnimationPlan?.ProvenanceId,
        scenario = worldAnimationScenarioEpoch, configuration = WorldAnimationConfiguration,
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
        switch (name)
        {
            case "view": WorldTimeline.SetView(data.GetValue<string>()); return;

            case "labelWidth": WorldTimeline.SetLabelWidth(NumberValue()); return;
            case "followPlayhead": WorldTimeline.FollowPlayhead = FlagValue(); return;
            case "fit": WorldTimeline.Fit(data.GetValue<string>()); return;
            case "visibleRange": var visible = Pair(); WorldTimeline.SetVisibleRange(visible[0], visible[1]); return;
            case "traceRange": WorldTimeline.SetVisibleRange(0, NumberValue()); return;
            case "fitTrace": if (FlagValue()) WorldTimeline.Fit("evaluated"); return;
        }
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
            case "range":
                double[] range = data is JsonArray ? Pair() : [0, NumberValue()];
                if (!range.All(double.IsFinite) || range[0] < 0 || range[1] > 3600 || range[1] <= range[0]) throw new StudioCommandException("invalid_argument", "Use an increasing range within 0–3600 seconds.");
                worldAnimationSession?.SetRange(range[0], range[1]); worldAnimationRangeStart = Math.Floor(range[0] * 60 + 1e-7) / 60; worldAnimationRangeEnd = Math.Ceiling(range[1] * 60 - 1e-7) / 60; break;
            case "phase":
                string phase = data.GetValue<string>(); if (phase is not ("runtime" or "cleanup")) throw new StudioCommandException("invalid_argument", "Phase must be runtime or cleanup.");
                SetWorldAnimationScenario(worldAnimationConfiguration with { Cleanup = phase == "cleanup" }); break;
            case "activations": ReadWorldAnimationActivations(data); break;
            case "fitScenario": if (FlagValue()) await FitWorldAnimationScenario(); break;
            case "seed": SetWorldAnimationScenario(worldAnimationConfiguration with { Seed = data.GetValue<int>() }); break;
            case "height": SetWorldAnimationScenario(worldAnimationConfiguration with { Height = (float)NumberValue() }); break;
            case "effectLevel": SetWorldAnimationScenario(worldAnimationConfiguration with { EffectLevel = data.GetValue<int>() }); break;
            case "referencePosition": case "activationTarget": SetWorldAnimationScenario(worldAnimationConfiguration with { ReferencePosition = WorldAnimationPosition(data.GetValue<string>()) }); break;
            case "activationStart": case "activationOrigin": SetWorldAnimationScenario(worldAnimationConfiguration with { ActivationStart = WorldAnimationPosition(data.GetValue<string>()) }); break;
            case "includedCalls":
                if (data is not JsonArray paths || paths.Count > AnimationExecutionPolicy.MaximumPaths) throw new StudioCommandException("invalid_argument", "Use a finite list of call paths.");
                int root = WorldAnimationTargetNow?.Entry ?? -1;
                if (root < 0) throw new StudioCommandException("not_ready", "Choose an exact root binding.");
                var included = paths.Select(p => p!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);
                foreach (string path in included)
                {
                    if (IsWorldAnimationActivationPath(path)) throw new StudioCommandException("invalid_argument", "The root is always included.");
                    ValidateWorldAnimationCallPath(root, path);
                    string parent = path[..Math.Max(0, path.LastIndexOf('/'))];
                    if (!IsWorldAnimationActivationPath(parent) && !included.Contains(parent)) throw new StudioCommandException("invalid_argument", "Every included call requires its ancestor calls.");
                }
                SetWorldAnimationScenario(worldAnimationConfiguration with { Policy = new(included, worldAnimationConfiguration.Policy.ConditionOverrides) }); break;
            case "conditions":
                if (data is not JsonArray conditions || conditions.Count > AnimationExecutionPolicy.MaximumPaths) throw new StudioCommandException("invalid_argument", "Use a finite list of condition overrides.");
                List<AnimationConditionOverride> overrides = [];
                foreach (var item in conditions)
                {
                    var condition = item!.AsObject(); string path = condition["instancePath"]!.GetValue<string>(); int entry = condition["entry"]!.GetValue<int>();
                    if (!Guid.TryParse(condition["event"]!.GetValue<string>(), out Guid ev)) throw new StudioCommandException("invalid_argument", "Use an event GUID.");
                    ValidateWorldCondition(entry, path, ev);
                    overrides.Add(new(path, entry, ev, condition["value"]!.GetValue<bool>()));
                }
                SetWorldAnimationScenario(worldAnimationConfiguration with { Policy = new(worldAnimationConfiguration.Policy.IncludedCalls, overrides) }); break;
            default: throw new StudioCommandException("unsupported", "This option is not part of the source-world animation session. Use the shared world controls for LOD, difficulty and scene presentation.");
        }
        RefreshWorldAnimationTimeline(); await Task.CompletedTask;
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

    private async Task OpenWorldAnimationScenario()
    {
        var target = WorldAnimationTargetNow is { Entry: >= 0 } active ? active : throw new StudioCommandException("not_ready", "Choose an exact animation binding.");
        await EnsureAnimationCardForEntryAsync(target.Document, target.Entry, CancellationToken.None);
        var panel = WorldAnimationScenarioPanel();
        SectionCard().SetAnimationSetup(panel, true);
        await Dispatcher.InvokeAsync(() =>
        {
            if (WorldAnimationTargetNow == target) SectionCard().FocusAnimationSetup(panel);
        }, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

    private FrameworkElement WorldAnimationScenarioPanel(int? selectedEntry = null, string path = "", Guid selectedEvent = default)
    {
        StackPanel panel = new();
        if (WorldAnimationTargetNow is { Entry: >= 0 }) panel.Children.Add(WorldAnimationSchedulePanel());
        panel.Children.Add(new TextBlock { Text = "Scheduled starts ignore their activation prerequisites; animations the run starts are checked as in the game. Range tests measure from the player at start unless a reference position is set. The preview does not simulate a hit or mission startup. Scenario changes stop playback and restore the world.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 8) });
        ComboBox phase = new() { ItemsSource = new[] { "Runtime", "Cleanup" }, SelectedIndex = worldAnimationConfiguration.Cleanup ? 1 : 0, MinHeight = 28 };
        Field("Phase", phase); phase.SelectionChanged += (_, _) => _ = ReportUi(() => WorldAnimationOptionAsync("phase", phase.SelectedIndex == 1 ? "cleanup" : "runtime"));
        Text("Seed", worldAnimationConfiguration.Seed.ToString(CultureInfo.InvariantCulture), "seed", true);
        Text("Height", worldAnimationConfiguration.Height.ToString("G9", CultureInfo.InvariantCulture), "height");
        Text("Effect level (0–2)", worldAnimationConfiguration.EffectLevel.ToString(CultureInfo.InvariantCulture), "effectLevel", true);
        Text("Reference position (XYZ)", Position(worldAnimationConfiguration.ReferencePosition), "referencePosition");
        Text("Activation start (XYZ)", Position(worldAnimationConfiguration.ActivationStart), "activationStart");
        if (selectedEntry is int entry && selectedEvent != Guid.Empty && shownDocument?.Document.Animations?.Entries[entry].AllSequences.SelectMany(s => s.Events).Any(e => e.Id == selectedEvent && e.Type is 31 or 33) == true)
        {
            var existing = worldAnimationConfiguration.Policy.ConditionOverrides.FirstOrDefault(c => c.Entry == entry && c.InstancePath == path && c.Event == selectedEvent);
            ComboBox condition = new() { ItemsSource = new[] { "Game-dependent", "Force true", "Force false" }, SelectedIndex = existing == null ? 0 : existing.Value ? 1 : 2, MinHeight = 28 };
            Field("This condition in this call path", condition);
            condition.SelectionChanged += (_, _) => _ = ReportUi(() =>
            {
                ValidateWorldCondition(entry, path, selectedEvent);
                var overrides = worldAnimationConfiguration.Policy.ConditionOverrides.Where(c => c.Entry != entry || c.InstancePath != path || c.Event != selectedEvent).ToList();
                if (condition.SelectedIndex > 0) overrides.Add(new(path, entry, selectedEvent, condition.SelectedIndex == 1));
                SetWorldAnimationScenario(worldAnimationConfiguration with { Policy = new(worldAnimationConfiguration.Policy.IncludedCalls, overrides) }); return Task.CompletedTask;
            });
        }
        return panel;
        void Field(string label, FrameworkElement control)
        {
            panel.Children.Add(new TextBlock { Text = label, Margin = new(0, 6, 0, 3) }); panel.Children.Add(control); AutomationProperties.SetName(control, "Animation scenario " + label);
        }
        void Text(string label, string initial, string key, bool integer = false)
        {
            TextBox box = new() { Text = initial, MinHeight = 28 }; Field(label, box);
            string applied = initial;
            box.LostKeyboardFocus += (_, _) => _ = ReportUi(async () =>
            {
                if (box.Text == applied) return;
                object value = box.Text;
                if (integer) { if (!int.TryParse(box.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)) throw new StudioCommandException("invalid_argument", "Use an integer."); value = number; }
                else if (key == "height") { if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) throw new StudioCommandException("invalid_argument", "Use a number."); value = number; }
                await WorldAnimationOptionAsync(key, value); applied = box.Text;
            });
        }
        static string Position(Vector3? position) => position is { } p ? string.Create(CultureInfo.InvariantCulture, $"{p.X:G9}, {p.Y:G9}, {p.Z:G9}") : "";
    }
}
