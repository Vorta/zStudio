using zStudio.Automation;
using zStudio.Core.Animation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private IReadOnlyList<AnimationScheduledStart> WorldAnimationActivations()
    {
        var target = WorldAnimationTargetNow ?? throw new StudioCommandException("not_ready", "Choose an animation binding.");
        return worldAnimationConfiguration.Starts.Count == 0 ? [new(Guid.Empty, target.Entry, 0)] : worldAnimationConfiguration.Starts;
    }

    private bool IsWorldAnimationActivationPath(string path) => path.Length == 0 ||
        worldAnimationConfiguration.Starts.Any(s => s.Id != Guid.Empty && s.Path == path);

    private void SetWorldAnimationActivations(IReadOnlyList<AnimationScheduledStart> starts)
    {
        var target = WorldAnimationTargetNow ?? throw new StudioCommandException("not_ready", "Choose an animation binding.");
        _ = new AnimationSchedule(starts, target.Document.Document.Animations!.Entries.Count);
        if (starts.Any(s => s.Entry == 0))
            throw new StudioCommandException("invalid_argument", "Choose an animation binding; entry 0 is the compiler's empty placeholder.");
        var removed = WorldAnimationActivations().Where(old => old.Id != Guid.Empty && !starts.Any(s => s.Id == old.Id && s.Entry == old.Entry)).Select(s => s.Path).ToArray();
        bool Keep(string path) => !removed.Any(p => path == p || path.StartsWith(p + "/", StringComparison.Ordinal));
        var scenario = worldAnimationConfiguration;
        SetWorldAnimationScenario(scenario with
        {
            Starts = starts.ToArray(), IncludedCalls = scenario.IncludedCalls.Where(Keep).ToArray(), Conditions = scenario.Conditions.Where(c => Keep(c.InstancePath)).ToArray(),
        });
    }

    private async Task FitWorldAnimationScenario()
    {
        double end = Math.Min(3600, WorldAnimationActivations().Max(s => s.Seconds) + 30);
        await WorldAnimationOptionAsync("playbackRange", new[] { 0d, end });
        TimingView.SetVisibleRange(0, end);
        RefreshWorldAnimationRun();
    }
}
