using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
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

    private string WorldAnimationScopedPath(JsonObject args)
    {
        string path = Text(args, "path");
        if (!args.ContainsKey("activation")) return path;
        Guid id = GuidArg(args, "activation");
        var activation = WorldAnimationActivations().FirstOrDefault(s => s.Id == id)
            ?? throw new StudioCommandException("stale_record", "The scheduled activation is unavailable.");
        if (!path.StartsWith("/@", StringComparison.Ordinal)) return activation.Path + path;
        if (activation.Path.Length == 0 || path != activation.Path && !path.StartsWith(activation.Path + "/", StringComparison.Ordinal))
            throw new StudioCommandException("invalid_argument", "The path belongs to a different scheduled activation.");
        return path;
    }

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

    private Task MoveWorldAnimationActivation(Guid id, double seconds)
    {
        var starts = WorldAnimationActivations();
        if (!starts.Any(s => s.Id == id)) throw new StudioCommandException("stale_record", "The scheduled activation is unavailable.");
        SetWorldAnimationActivations(starts.Select(s => s.Id == id ? s with { Seconds = seconds } : s).ToArray());
        return Task.CompletedTask;
    }

    private void ReadWorldAnimationActivations(JsonNode data)
    {
        if (data is not JsonArray values || values.Count > AnimationSchedule.MaximumStarts)
            throw new StudioCommandException("invalid_argument", "Use at most 32 scheduled activations, including the primary.");
        List<AnimationScheduledStart> starts = [];
        foreach (var value in values)
        {
            var item = value!.AsObject();
            starts.Add(new(GuidArg(item, "id"), Int(item, "entry"), Number(item, "seconds")));
        }
        SetWorldAnimationActivations(starts);
    }

    private async Task FitWorldAnimationScenario()
    {
        double end = Math.Min(3600, WorldAnimationActivations().Max(s => s.Seconds) + 30);
        await WorldAnimationOptionAsync("range", new[] { 0d, end });
        WorldTimeline.SetVisibleRange(0, end);
        RefreshWorldAnimationTimeline();
    }
}
