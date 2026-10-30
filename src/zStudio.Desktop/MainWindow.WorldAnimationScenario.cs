using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using zStudio.Core.Animation;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private static readonly AnimationPackage NoAnimations = new() { Prefix = [], Tail = [] };
    private readonly ConditionalWeakTable<DocumentModel, AnimationScenarioSession> worldScenarioSessions = new();
    private readonly HashSet<AnimationScenarioSession> worldScenarioSaves = [];
    private AnimationScenarioSession? noWorldScenario;

    /// <summary>The shown map's animation scenario: every view, run, analysis and MCP command reads this one object.</summary>
    private AnimationScenario worldAnimationConfiguration => WorldScenarios().Current;

    /// <summary>
    /// The shown world's scenario session, one per document: a source map's restores the scenario its project saved for the map
    /// when the map is first shown, and moves to each new compilation of the map through source provenance (what no longer
    /// matches is reported in Problems). A compiled world's scenario only carries the player its drop analysis measures from.
    /// </summary>
    private AnimationScenarioSession WorldScenarios()
    {
        if (shownDocument is not { } document) return noWorldScenario ??= new(new(NoAnimations, null));
        var map = document.Document as SourceSceneSnapshot;
        AnimationScenarioSource source = document.Document.Animations is { } package ? new(package, map?.AnimationProvenance) : new(NoAnimations, null);
        if (worldScenarioSessions.TryGetValue(document, out var session))
        {
            if (!ReferenceEquals(session.Source.Package, source.Package)) ReportScenarioNotices(document, session.Rebind(source));
            return session;
        }
        JsonObject? saved = null;
        if (map != null)
            try { saved = SourceProjectSettings.ReadAnimationScenario(map.Root, map.Mission); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        var restored = AnimationScenarioSession.Restore(source, saved);
        worldScenarioSessions.AddOrUpdate(document, restored);
        restored.Changed += change => WorldScenarioChanged(document, restored, change);
        ReportScenarioNotices(document, restored.Notices); ReportScenarioMarks(document, restored);
        return restored;
    }

    private void WorldScenarioChanged(DocumentModel document, AnimationScenarioSession session, AnimationScenarioChange change)
    {
        // A remap gives the scenario this compilation's identities: analyses of the previous ones are another scenario's.
        if (document == shownDocument && change.Reason == "remap") { worldAnimationScenarioEpoch = Guid.NewGuid(); worldAnimationDuration = null; }
        if (document.Document is SourceSceneSnapshot map) SaveWorldScenario(document, map.Mission, session);
        ReportScenarioMarks(document, session);
        // The Scenario tab, the chip and the status bar show the one scenario: any change (GUI or MCP) shows at once.
        if (document == shownDocument) RefreshScenarioPanel();
    }

    /// <summary>
    /// The scenario's own rows in Problems: what its restore, remap or save could not keep, and one row per kind of marked item
    /// still asked about ("1 override no longer matches after your edit"). A source build's report replaces the map's other
    /// rows, never these (<see cref="IsPreviewProblem"/>); they change only with the scenario.
    /// </summary>
    private readonly List<StudioProblem> scenarioProblems = [], scenarioMarkProblems = [];
    private void ReportScenarioMarks(DocumentModel document, AnimationScenarioSession session)
    {
        foreach (var old in scenarioMarkProblems.Where(p => p.File == document.Path).ToArray()) { scenarioMarkProblems.Remove(old); ViewModel.Problems.Remove(old); }
        if (document.IsDisposed) return;
        foreach (var (kind, one, many) in new[] { ("starts", "start", "starts"), ("paths", "included call path", "included call paths"), ("conditions", "override", "overrides") })
        {
            int count = session.Marks.Of(kind);
            if (count == 0 || session.KeptMarks.Contains(kind)) continue;
            string text = count == 1 ? $"Animation scenario: 1 {one} no longer matches after your edit. The Scenario tab keeps it marked (it never runs): Remove or Keep."
                : $"Animation scenario: {count:N0} {many} no longer match after your edit. The Scenario tab keeps them marked (they never run): Remove or Keep.";
            StudioProblem problem = new("Warning", "File / operation", text, document.Path);
            scenarioMarkProblems.Add(problem); ViewModel.Problems.Add(problem);
        }
    }

    /// <summary>Saves a source map's scenario in its project's settings, once per burst of changes; the defaults need no entry.</summary>
    private void SaveWorldScenario(DocumentModel document, string mission, AnimationScenarioSession session)
    {
        if (!worldScenarioSaves.Add(session)) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            worldScenarioSaves.Remove(session);
            List<AnimationScenarioNotice> notices = [];
            JsonObject? saved;
            // The marked items are saved with the scenario (they are resolved again when the map is shown).
            try { saved = session.Current.IsCustomized || session.Marked != null ? session.Save(notices) : null; }
            catch (InvalidDataException ex) { ViewModel.AddProblem("Animation scenario could not be saved: " + Bounded(ex.Message, 512), "Warning", document.Path); return; }
            ReportScenarioNotices(document, notices);
            ViewModel.RememberProjectSetting(root => SourceProjectSettings.WriteAnimationScenario(root, mission, saved));
        });
    }

    /// <summary>
    /// What a restore, remap or save of the scenario said, as the scenario's rows in Problems (the latest report of the map
    /// replaces its earlier one). Items that stopped matching are the marked rows instead (<see cref="ReportScenarioMarks"/>).
    /// </summary>
    private void ReportScenarioNotices(DocumentModel? document, IReadOnlyList<AnimationScenarioNotice> notices)
    {
        var shown = notices.Where(n => n.Kind is not ("start" or "include" or "condition")).ToArray();
        if (shown.Length == 0) return;
        foreach (var old in scenarioProblems.Where(p => p.File == document?.Path).ToArray()) { scenarioProblems.Remove(old); ViewModel.Problems.Remove(old); }
        void Add(string text)
        {
            StudioProblem problem = new("Information", "File / operation", text, document?.Path);
            scenarioProblems.Add(problem); ViewModel.Problems.Add(problem);
        }
        foreach (var notice in shown.Take(16)) Add("Animation scenario: " + Bounded(notice.Text, 512));
        if (shown.Length > 16) Add($"Animation scenario: {shown.Length - 16} more items could not be kept.");
    }

    /// <summary>The scenario as MCP reports it: starts, inclusion, switches, inputs and rules.</summary>
    private static object WorldScenarioState(AnimationScenario scenario) => new
    {
        starts = scenario.Starts.Select(s => new { id = s.Id, entry = s.Entry, seconds = s.Seconds }).ToArray(),
        includedCalls = scenario.IncludedCalls.Count,
        conditions = scenario.Conditions.Take(100).Select(c => new { instancePath = c.InstancePath, entry = c.Entry, @event = c.Event, value = c.Value }).ToArray(),
        conditionCount = scenario.Conditions.Count,
        player = new { kind = scenario.Player.Kind.ToString(), position = scenario.CustomPlayer is { } p ? new[] { p.X, p.Y, p.Z } : null },
        seed = scenario.Seed, effectsDetail = scenario.EffectsDetail.ToString(), phase = scenario.Phase.ToString(), height = scenario.Height,
        beamOrigin = scenario.BeamOrigin is { } o ? new[] { o.X, o.Y, o.Z } : null,
        prerequisites = scenario.Prerequisites.ToString(), rangeGates = scenario.RangeGates.ToString(), zoneGates = scenario.ZoneGates.ToString(),
        customized = scenario.IsCustomized,
    };
}
