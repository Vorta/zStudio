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
        ReportScenarioNotices(document, restored.Notices);
        return restored;
    }

    private void WorldScenarioChanged(DocumentModel document, AnimationScenarioSession session, AnimationScenarioChange change)
    {
        // A remap gives the scenario this compilation's identities: analyses of the previous ones are another scenario's.
        if (document == shownDocument && change.Reason == "remap") { worldAnimationScenarioEpoch = Guid.NewGuid(); worldAnimationDuration = null; }
        if (document.Document is SourceSceneSnapshot map) SaveWorldScenario(document, map.Mission, session);
        // The Scenario tab, the chip and the status bar show the one scenario: any change (GUI or MCP) shows at once.
        if (document == shownDocument) RefreshScenarioPanel();
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
            try { saved = session.Current.IsCustomized ? AnimationScenarioRecord.Save(session.Current, session.Source, notices) : null; }
            catch (InvalidDataException ex) { ViewModel.AddProblem("Animation scenario could not be saved: " + Bounded(ex.Message, 512), "Warning", document.Path); return; }
            ReportScenarioNotices(document, notices);
            ViewModel.RememberProjectSetting(root => SourceProjectSettings.WriteAnimationScenario(root, mission, saved));
        });
    }

    private void ReportScenarioNotices(DocumentModel? document, IReadOnlyList<AnimationScenarioNotice> notices)
    {
        foreach (var notice in notices.Take(16)) ViewModel.AddProblem("Animation scenario: " + Bounded(notice.Text, 512), "Information", document?.Path);
        if (notices.Count > 16) ViewModel.AddProblem($"Animation scenario: {notices.Count - 16} more items could not be kept.", "Information", document?.Path);
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
