using System.Globalization;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using zStudio.Automation;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>
/// The map's animation scenario as the Scenario tab and MCP animation_scenario change it (design/animation-redesign.md §10, §12):
/// both call these operations, which all go through <see cref="SetWorldAnimationScenario"/> on the one session, so every view
/// shows the same scenario at once.
/// </summary>
public partial class MainWindow
{
    private string? lastIncludeAll;
    /// <summary>Pick in view: the next left click on a surface places the player or the beam origin; Esc or a right click cancels.</summary>
    private string? scenarioPick;
    private ViewportChip? scenarioPickBanner;

    private void RequireScenarioMap()
    {
        if (!sourceChromeActive || shownDocument?.Document is not SourceSceneSnapshot) throw new StudioCommandException("not_ready", "Open a source map first.");
    }
    private WorldAnimationTarget RequireOpenedBinding()
    {
        RequireScenarioMap();
        return animationMode is { Entry: > 0 } target ? target
            : throw new StudioCommandException("not_ready", animationMode == null ? "Open an animation in Animation mode first." : "The opened definition has no binding in this map.");
    }

    // ---------------------------------------------------------------- operations (GUI and MCP)

    private void ScenarioStarts(IReadOnlyList<AnimationScheduledStart> starts)
    {
        RequireOpenedBinding();
        try { SetWorldAnimationActivations(starts); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
    }
    private void ScenarioAddStart(int entry, double seconds = 0)
    {
        var target = RequireOpenedBinding();
        if (entry <= 0 || entry >= target.Document.Document.Animations!.Entries.Count) throw new StudioCommandException("invalid_argument", "Choose a binding of this map.");
        var starts = WorldAnimationActivations();
        if (starts.Count >= AnimationSchedule.MaximumStarts) throw new StudioCommandException("limit_exceeded", $"Remove a start first ({AnimationSchedule.MaximumStarts} starts).");
        ScenarioStarts([.. starts, new(Guid.NewGuid(), entry, seconds)]);
    }
    private void ScenarioRemoveStart(Guid id)
    {
        if (id == Guid.Empty) throw new StudioCommandException("invalid_argument", "The primary start is the opened binding; it cannot be removed.");
        var starts = WorldAnimationActivations();
        if (!starts.Any(s => s.Id == id)) throw new StudioCommandException("stale_record", "The start is not in the scenario.");
        ScenarioStarts(starts.Where(s => s.Id != id).ToArray());
    }
    /// <summary>Includes call paths with the calls that lead to them, or excludes them with every path below them.</summary>
    private void ScenarioInclude(IReadOnlyList<string> paths, bool value)
    {
        var target = RequireOpenedBinding();
        if (paths.Count > AnimationExecutionPolicy.MaximumPaths) throw new StudioCommandException("invalid_argument", "Use a finite list of call paths.");
        var scenario = worldAnimationConfiguration;
        foreach (string path in paths)
        {
            if (IsWorldAnimationActivationPath(path)) throw new StudioCommandException("invalid_argument", "A start always runs; choose a call path.");
            ValidateWorldAnimationCallPath(target.Entry, path);
            if (!value) { scenario = scenario.WithInclusion(path, false); continue; }
            for (string at = path; at.Length > 0 && !IsWorldAnimationActivationPath(at); at = at[..Math.Max(0, at.LastIndexOf('/'))])
                scenario = scenario.WithInclusion(at, true);
        }
        lastIncludeAll = null;
        try { SetWorldAnimationScenario(scenario); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
    }
    /// <summary>
    /// Include all: every call path the starts reach but back-links (<paramref name="opened"/> false), or the opened binding's
    /// own (every path below the primary start; the other starts' calls are excluded), as Include ▾ → This animation only.
    /// </summary>
    private AnimationIncludeAllResult ScenarioIncludeAll(CancellationToken token = default, bool opened = false)
    {
        var target = RequireOpenedBinding();
        var package = target.Document.Document.Animations!;
        AnimationScenario scenario; AnimationIncludeAllResult result;
        if (opened)
        {
            var (own, ownResult) = AnimationScenario.For(target.Entry).IncludeAll(package, token);
            (scenario, result) = (worldAnimationConfiguration with { IncludedCalls = own.IncludedCalls }, ownResult);
        }
        else (scenario, result) = worldAnimationConfiguration.IncludeAll(package, token);
        SetWorldAnimationScenario(scenario);
        lastIncludeAll = result.Summary; RefreshScenarioPanel();
        return result;
    }
    private void ScenarioExcludeAll()
    {
        RequireOpenedBinding();
        lastIncludeAll = null;
        SetWorldAnimationScenario(worldAnimationConfiguration.ExcludeAll());
    }
    private void ScenarioCondition(string instancePath, int entry, Guid ev, AnimationConditionChoice choice) => ScenarioConditions([(instancePath, entry, ev, choice)]);
    /// <summary>Sets condition switches in order as one scenario change (one stop and one re-evaluation to the playhead).</summary>
    private void ScenarioConditions(IReadOnlyList<(string InstancePath, int Entry, Guid Event, AnimationConditionChoice Choice)> switches)
    {
        RequireOpenedBinding();
        var next = worldAnimationConfiguration;
        foreach (var (instancePath, entry, ev, choice) in switches)
        {
            ValidateWorldCondition(entry, instancePath, ev);
            next = next.WithCondition(instancePath, entry, ev, choice);
        }
        SetWorldAnimationScenario(next);
    }
    private void ScenarioInputs(AnimationScenario next)
    {
        RequireScenarioMap();
        try { SetWorldAnimationScenario(next); }
        catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
    }
    private void ScenarioReset()
    {
        RequireScenarioMap();
        lastIncludeAll = null;
        // The opened binding (after Exit, the kept one) stays the primary start; everything else returns to its default.
        int? primary = animationMode is { Entry: > 0 } target ? target.Entry
            : KeptAnimation() is { Entry: > 0 } kept ? kept.Entry : null;
        SetWorldAnimationScenario(primary is int entry ? AnimationScenario.For(entry) : AnimationScenario.Default, "reset");
    }
    /// <summary>The player or beam origin placed on the surface under <paramref name="point"/> (DIP in the 3D view).</summary>
    private Vector3 ScenarioPick(string target, Point point)
    {
        RequireScenarioMap();
        if (target is not ("player" or "beamOrigin")) throw new StudioCommandException("invalid_argument", "Pick the player or the beam origin.");
        if (scene is not { } viewport || SceneHost.Visibility != Visibility.Visible) throw new StudioCommandException("not_ready", "The 3D view is not shown.");
        var surface = viewport.ProbeInspection(point)?.Surface ?? throw new StudioCommandException("not_found", "No surface under that point of the view.");
        ScenarioInputs(target == "player" ? worldAnimationConfiguration with { Player = AnimationPlayerReference.Custom(surface) } : worldAnimationConfiguration with { BeamOrigin = surface });
        return surface;
    }

    // ---------------------------------------------------------------- the Scenario tab

    private AnimationScenarioPanel CreateScenarioPanel()
    {
        AnimationScenarioPanel panel = new() { BindingChoices = ScenarioBindingChoices };
        panel.Requested += (action, value) => _ = ReportUi(async () =>
        {
            // A refused change leaves the tab showing the scenario as it is, not the toggle the click made.
            try { await ApplyScenarioRequest(action, value); }
            catch { RefreshScenarioPanel(); throw; }
        });
        return panel;
    }
    /// <summary>One Scenario tab request, named as animation_scenario's actions.</summary>
    private async Task ApplyScenarioRequest(string action, object? value)
    {
        {
            switch (action)
            {
                case "startTime": var (id, seconds) = ((Guid, double))value!; ScenarioStarts(WorldAnimationActivations().Select(s => s.Id == id ? s with { Seconds = seconds } : s).ToArray()); break;
                case "removeStart": ScenarioRemoveStart((Guid)value!); break;
                case "addStart": ScenarioAddStart((int)value!); break;
                case "fitRange": await FitWorldAnimationScenario(); break;
                case "includeAll": ScenarioIncludeAll(); break;
                case "excludeAll": ScenarioExcludeAll(); break;
                case "include": var (paths, include) = ((string[], bool))value!; ScenarioInclude(paths, include); break;
                case "player":
                    var kind = (AnimationPlayerReferenceKind)value!;
                    // Custom needs a point: it is picked in the view.
                    if (kind == AnimationPlayerReferenceKind.Custom) BeginScenarioPick("player");
                    else ScenarioInputs(worldAnimationConfiguration with { Player = kind == AnimationPlayerReferenceKind.None ? AnimationPlayerReference.None : AnimationPlayerReference.AtStart });
                    break;
                case "pick": BeginScenarioPick((string)value!); break;
                case "seed": ScenarioInputs(worldAnimationConfiguration with { Seed = (int)value! }); break;
                case "effectsDetail": ScenarioInputs(worldAnimationConfiguration with { EffectsDetail = (AnimationEffectsDetail)value! }); break;
                case "removeOverride": var removed = (AnimationConditionOverride)value!; ScenarioCondition(removed.InstancePath, removed.Entry, removed.Event, AnimationConditionChoice.Game); break;
                case "goTo": await GoToScenarioCondition((AnimationConditionOverride)value!); break;
                case "prerequisites": ScenarioInputs(worldAnimationConfiguration with { Prerequisites = PrerequisiteRule((string)value!) }); break;
                case "rangeGates": ScenarioInputs(worldAnimationConfiguration with { RangeGates = (string)value! == "ignore" ? AnimationRangeGateRule.Ignore : AnimationRangeGateRule.Game }); break;
                case "zoneGates": ScenarioInputs(worldAnimationConfiguration with { ZoneGates = (string)value! == "view" ? AnimationZoneGateRule.View : AnimationZoneGateRule.Ignore }); break;
                case "phase": ScenarioInputs(worldAnimationConfiguration with { Phase = (string)value! == "cleanup" ? AnimationScenarioPhase.Cleanup : AnimationScenarioPhase.Runtime }); break;
                case "height": ScenarioInputs(worldAnimationConfiguration with { Height = (float)value! }); break;
                case "clearBeamOrigin": ScenarioInputs(worldAnimationConfiguration with { BeamOrigin = null }); break;
                case "reset": ScenarioReset(); break;
            }
        }
    }
    private static AnimationPrerequisiteRule PrerequisiteRule(string value) => value switch
    {
        "check" => AnimationPrerequisiteRule.Check, "ignore" => AnimationPrerequisiteRule.Ignore, "ignoreStarts" => AnimationPrerequisiteRule.IgnoreForScenarioStarts,
        _ => throw new StudioCommandException("invalid_argument", "Prerequisites are check, ignoreStarts or ignore."),
    };
    private IReadOnlyList<ScenarioBindingChoice> ScenarioBindingChoices()
    {
        if (shownDocument?.Document.Animations is not { } package) return [];
        var plan = GetWorldAnimationStructurePlan();
        return Enumerable.Range(1, Math.Max(0, plan.EntryCount - 1)).Select(plan.Entry)
            .Select(e => new ScenarioBindingChoice(e.Entry, e.Name, $"{e.Root} · #{e.Entry}")).ToArray();
    }
    private async Task GoToScenarioCondition(AnimationConditionOverride condition)
    {
        var package = shownDocument?.Document.Animations;
        var sequence = package?.Entries.ElementAtOrDefault(condition.Entry)?.AllSequences.FirstOrDefault(s => s.Events.Any(e => e.Id == condition.Event))
            ?? throw new StudioCommandException("stale_record", "The condition is no longer in this map.");
        await WorldAnimationSelectSourceAsync(sequence.Id, condition.Event, CancellationToken.None, condition.Entry, condition.InstancePath, preserveTimeline: true);
        ShowAnimationRightTab("inspector");
    }

    /// <summary>Shows the scenario in the Scenario tab and the command bar's chip (every change of the map's scenario comes here).</summary>
    private void RefreshScenarioPanel()
    {
        RefreshScenarioChip();
        if (scenarioPanel == null || !sourceChromeActive) return;
        try { scenarioPanel.SetView(BuildScenarioView()); }
        catch (StudioCommandException) { }
        RefreshScenarioLive();
    }
    private void RefreshScenarioLive()
    {
        if (scenarioPanel?.View == null || !sourceChromeActive) return;
        var frame = worldAnimationSession?.CurrentFrame;
        string player = frame?.PlayerReference is { Position: { } at } reference
            ? string.Create(CultureInfo.InvariantCulture, $"Now {at.X:0.0}, {at.Y:0.0}, {at.Z:0.0} · {reference.Source}") + (reference.Frozen ? " · frozen" : "")
            : scene?.Mission?.PlayerStart is { } start ? string.Create(CultureInfo.InvariantCulture, $"At {start.X:0.0}, {start.Y:0.0}, {start.Z:0.0} in this map")
            : "This map has no player start on this difficulty.";
        string prerequisites = frame == null ? "" : frame.Refusals.Count(r => r.Prerequisites != null) is int refused and > 0 ? $"{refused} refused" : "none refused";
        string range = frame == null ? "" : $"{frame.Bindings.Count(b => b.PausedBy == "out of range")} paused";
        string zone = frame == null ? "" : $"{frame.Bindings.Count(b => b.PausedBy == "zone")} paused";
        scenarioPanel.SetLive(new(player, prerequisites, range, zone));
    }

    /// <summary>The scenario as the tab shows it, with what the starts reach grouped by caller and called definition.</summary>
    private AnimationScenarioView BuildScenarioView()
    {
        RequireScenarioMap();
        var source = (SourceSceneSnapshot)shownDocument!.Document;
        var scenario = worldAnimationConfiguration;
        bool opened = animationMode is { Entry: > 0 };
        List<ScenarioStartRow> starts = [];
        List<ScenarioGroupRow> groups = [];
        int included = 0, reachable = 0, unresolved = 0; bool truncated = false;
        List<ScenarioOverrideRow> overrides = [];
        // The scenario's starts and what they reach are the map's, in the map layout too (animation_scenario read).
        if (source.Animations is { } package)
        {
            var plan = GetWorldAnimationStructurePlan();
            foreach (var start in (opened ? WorldAnimationActivations() : scenario.Starts).Where(s => s.Entry > 0 && s.Entry < plan.EntryCount))
            {
                var binding = plan.Entry(start.Entry);
                starts.Add(new(start.Id, start.Entry, binding.Name, binding.Root, start.Seconds, start.Id == Guid.Empty));
            }
            var paths = scenario.CallPaths(package);
            unresolved = paths.Unresolved; truncated = paths.Truncated; reachable = paths.Paths.Count;
            included = paths.Paths.Count(p => scenario.Includes(p.Path));
            var names = source.AnimationDefinitions.Definitions.ToDictionary(d => d.Index, d => d.Name);
            string Group(int entry) => source.AnimationProvenance?.FindEntry(entry) is { } at && names.TryGetValue(at.Definition, out var name) ? name : package.Entries[entry].Name;
            foreach (var group in paths.Paths.GroupBy(p => (Caller: Group(p.Caller), Target: Group(p.Target), p.BackLink)))
            {
                var rows = group.Select(p => new ScenarioPathRow(p.Path, $"{package.Entries[p.Caller].Name} #{p.Caller} → {package.Entries[p.Target].Name} #{p.Target}" + (p.BackLink ? " ↩" : ""),
                    scenario.Includes(p.Path), p.BackLink)).ToArray();
                // The key is the group's identity in animation_scenario read (group): caller > target, ">back" for back-links.
                groups.Add(new($"{group.Key.Caller}>{group.Key.Target}{(group.Key.BackLink ? ">back" : "")}", $"{group.Key.Caller} → {group.Key.Target}" + (group.Key.BackLink ? " ↩ cycle" : ""),
                    rows.Count(r => r.Included), rows.Length, group.Key.BackLink, rows));
            }
            int primary = scenario.Primary ?? -1;
            foreach (var condition in scenario.Conditions)
            {
                var entry = package.Entries.ElementAtOrDefault(condition.Entry);
                var sequence = entry?.AllSequences.FirstOrDefault(s => s.Events.Any(e => e.Id == condition.Event));
                var ev = sequence?.Events.First(e => e.Id == condition.Event);
                string label = entry == null || ev == null ? "A condition no longer in this map"
                    : $"{(sequence!.Name.Length == 0 ? "Cleanup" : sequence.Name)} › {AnimationPhrases.Event(entry, ev)}" + (condition.Entry != primary ? $" · {entry.Name}" : "");
                overrides.Add(new(condition, label));
            }
        }
        return new(source.Mission ?? shownDocument.SourceWorld?.Mission ?? "this map", scenario, opened, starts, groups, included, reachable, unresolved, truncated, lastIncludeAll, overrides);
    }

    // ---------------------------------------------------------------- Pick in view

    private void BeginScenarioPick(string target)
    {
        RequireOpenedBinding();
        if (scene == null || SceneHost.Visibility != Visibility.Visible) throw new StudioCommandException("not_ready", "The 3D view is not shown.");
        scenarioPick = target;
        if (scenarioPickBanner == null)
        {
            scenarioPickBanner = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 12, 0, 0), MinHeight = 36, Padding = new(12, 0, 4, 0), FontSize = 13 };
            AutomationProperties.SetName(scenarioPickBanner, "Pick in view");
            AutomationProperties.SetLiveSetting(scenarioPickBanner, AutomationLiveSetting.Polite);
            StackPanel content = new() { Orientation = Orientation.Horizontal };
            var pin = new PreviewIcon { Kind = "Pin", Width = 15, Height = 15, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
            pin.SetResourceReference(PreviewIcon.ForegroundProperty, "AccentFillColorDefaultBrush");
            content.Children.Add(pin);
            content.Children.Add(new TextBlock { Name = "PickText", VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 10, 0) });
            Button cancel = new() { Content = "Cancel", Height = 28, Padding = new(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(cancel, "Cancel picking");
            cancel.Click += (_, _) => EndScenarioPick();
            content.Children.Add(cancel);
            scenarioPickBanner.Content = content;
            PreviewOverlayChrome.Children.Add(scenarioPickBanner);
            SceneHost.PreviewMouseDown += ScenarioPickMouseDown;
        }
        ((TextBlock)((StackPanel)scenarioPickBanner.Content).Children[1]).Text = target == "player" ? "Click a surface to place the player · Esc cancels" : "Click a surface to place the beam origin · Esc cancels";
        scenarioPickBanner.Visibility = Visibility.Visible;
        SceneHost.Focus();
    }
    /// <summary>Ends Pick in view (placed, cancelled, or the mode or map ended); the tab shows the scenario as it is (a Custom radio chosen for the pick goes back).</summary>
    private void EndScenarioPick()
    {
        if (scenarioPick == null && scenarioPickBanner?.Visibility != Visibility.Visible) return;
        scenarioPick = null;
        if (scenarioPickBanner != null) scenarioPickBanner.Visibility = Visibility.Collapsed;
        RefreshScenarioPanel();
    }
    private void ScenarioPickMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (scenarioPick is not { } target) return;
        // Picking belongs to Animation mode: outside it (or without a view) it is over.
        if (!InAnimationMode || !sourceChromeActive || scene is not { } viewport) { EndScenarioPick(); return; }
        // A right click cancels; the middle button still orbits.
        if (e.ChangedButton == MouseButton.Right) { e.Handled = true; EndScenarioPick(); return; }
        if (e.ChangedButton != MouseButton.Left) return;
        e.Handled = true;
        var point = e.GetPosition(viewport.RenderSurface);
        if (viewport.ProbeInspection(point)?.Surface == null) return; // Nothing there: keep waiting for a surface.
        scenarioPick = null;
        _ = ReportUi(() => { try { ScenarioPick(target, point); } finally { EndScenarioPick(); } return Task.CompletedTask; });
    }
    /// <summary>Escape ends Pick in view first (the existing Escape order then goes on); Shift+Esc goes on to exit the mode, which ends it too.</summary>
    private bool ScenarioPickKey(KeyEventArgs e)
    {
        if (scenarioPick == null || e.Key != Key.Escape || System.Windows.Input.Keyboard.Modifiers != ModifierKeys.None) return false;
        EndScenarioPick(); e.Handled = true; return true;
    }

    // ---------------------------------------------------------------- MCP state

    /// <summary>
    /// The scenario as animation_scenario read reports it: the groups paged by offset and limit, each with its first 16 paths
    /// (pathsNextOffset when it has more); with group, that group's paths paged by offset and limit.
    /// </summary>
    private object ScenarioReadState(System.Text.Json.Nodes.JsonObject? a = null)
    {
        var view = BuildScenarioView();
        var scenario = view.Scenario;
        int offset = a?.ContainsKey("offset") == true ? Int(a, "offset") : 0, limit = a?.ContainsKey("limit") == true ? Int(a, "limit") : 100;
        if (offset < 0 || limit is < 1 or > 100) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–100.");
        const int FirstPaths = 16;
        object Path(ScenarioPathRow p) => new { path = p.Path, label = p.Label, included = p.Included, backLink = p.BackLink };
        object? groupPage = null;
        if (a?.ContainsKey("group") == true)
        {
            string key = Text(a, "group");
            var group = view.Groups.FirstOrDefault(g => g.Key == key) ?? throw new StudioCommandException("stale_record", "The scenario's starts reach no such group of calls; read the scenario again.");
            var rows = group.Paths.Skip(offset).Take(limit).Select(Path).ToArray();
            groupPage = new { group = group.Key, caller = group.Label, total = group.Paths.Count, offset, nextOffset = offset + rows.Length < group.Paths.Count ? (int?)(offset + rows.Length) : null, paths = rows };
        }
        var shownGroups = groupPage != null ? view.Groups.Take(0).ToArray() : view.Groups.Skip(offset).Take(limit).ToArray();
        return new
        {
            mission = view.Mission, summary = ScenarioSummary(scenario), customized = scenario.IsCustomized, opened = view.Opened,
            starts = view.Starts.Select(s => new { id = s.Id, entry = s.Entry, name = s.Name, root = s.Root, seconds = s.Seconds, primary = s.Primary }).ToArray(),
            calls = new
            {
                included = view.Included, reachable = view.Reachable, unresolved = view.Unresolved, truncated = view.Truncated, includeAll = view.IncludeResult,
                totalGroups = view.Groups.Count,
                groups = shownGroups.Select(g => new
                {
                    group = g.Key, caller = g.Label, included = g.Included, total = g.Total, backLink = g.BackLink,
                    paths = g.Paths.Take(FirstPaths).Select(Path).ToArray(), pathsNextOffset = g.Paths.Count > FirstPaths ? (int?)FirstPaths : null,
                }).ToArray(),
                nextOffset = groupPage == null && offset + shownGroups.Length < view.Groups.Count ? (int?)(offset + shownGroups.Length) : null,
                groupPage,
            },
            conditions = view.Overrides.Take(100).Select(o => new { instancePath = o.Override.InstancePath, entry = o.Override.Entry, @event = o.Override.Event, value = o.Override.Value ? "true" : "false", label = o.Label }).ToArray(),
            conditionCount = scenario.Conditions.Count,
            inputs = new
            {
                player = new { kind = scenario.Player.Kind switch { AnimationPlayerReferenceKind.Custom => "custom", AnimationPlayerReferenceKind.None => "none", _ => "atStart" }, position = scenario.CustomPlayer is { } p ? new[] { p.X, p.Y, p.Z } : null },
                seed = scenario.Seed, effectsDetail = scenario.EffectsDetail.ToString(), phase = scenario.Phase == AnimationScenarioPhase.Cleanup ? "cleanup" : "runtime", height = scenario.Height,
                beamOrigin = scenario.BeamOrigin is { } o ? new[] { o.X, o.Y, o.Z } : null,
            },
            rules = new { prerequisites = AnimationScenarioPanel.RuleName(scenario.Prerequisites), rangeGates = scenario.RangeGates == AnimationRangeGateRule.Game ? "game" : "ignore", zoneGates = scenario.ZoneGates == AnimationZoneGateRule.View ? "view" : "ignore" },
            picking = scenarioPick,
        };
    }
}
