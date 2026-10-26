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
        return worldAnimationConfiguration.Activations.Count == 0 ? [new(Guid.Empty, target.Entry, 0)] : worldAnimationConfiguration.Activations;
    }

    private bool IsWorldAnimationActivationPath(string path) => path.Length == 0 ||
        worldAnimationConfiguration.Activations.Any(s => s.Id != Guid.Empty && s.Path == path);

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
        var policy = worldAnimationConfiguration.Policy;
        SetWorldAnimationScenario(worldAnimationConfiguration with
        {
            Activations = Array.AsReadOnly(starts.ToArray()),
            Policy = new(policy.IncludedCalls.Where(Keep), policy.ConditionOverrides.Where(c => Keep(c.InstancePath)))
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
    }

    private FrameworkElement WorldAnimationSchedulePanel()
    {
        StackPanel panel = new();
        panel.Children.Add(new TextBlock { Text = "Animations on the shared timeline", FontWeight = FontWeights.SemiBold, Margin = new(0, 8, 0, 4) });
        var plan = GetWorldAnimationStructurePlan();
        foreach (var start in WorldAnimationActivations())
        {
            var entry = plan.Entry(start.Entry);
            panel.Children.Add(new TextBlock { Text = entry.Name + $" · entry #{start.Entry}" + (start.Id == Guid.Empty ? " · primary" : ""), TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 3) });
            Grid row = new(); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            TextBox seconds = new() { Text = start.Seconds.ToString("G9", CultureInfo.InvariantCulture), MinHeight = 28, ToolTip = "Start time in seconds", Margin = new(0, 0, 4, 0) };
            TextBox frame = new() { Text = AnimationTimelineFrames.Frame(start.Seconds).ToString("G9", CultureInfo.InvariantCulture), MinHeight = 28, ToolTip = "40 fps frame; frame 1 is zero seconds", Margin = new(0, 0, 4, 0) };
            AutomationProperties.SetName(seconds, $"Animation {start.Id} start seconds"); AutomationProperties.SetName(frame, $"Animation {start.Id} start frame");
            row.Children.Add(seconds); Grid.SetColumn(frame, 1); row.Children.Add(frame);
            double applied = start.Seconds;
            void Apply(TextBox box, bool frames) => _ = ReportUi(async () =>
            {
                if (!double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)) throw new StudioCommandException("invalid_argument", "Enter a finite start time.");
                double time = frames ? AnimationTimelineFrames.Seconds(value) : value;
                if (time == applied) return;
                await MoveWorldAnimationActivation(start.Id, time); applied = time;
                seconds.Text = time.ToString("G9", CultureInfo.InvariantCulture); frame.Text = AnimationTimelineFrames.Frame(time).ToString("G9", CultureInfo.InvariantCulture);
            });
            seconds.LostKeyboardFocus += (_, _) => Apply(seconds, false); frame.LostKeyboardFocus += (_, _) => Apply(frame, true);
            if (start.Id != Guid.Empty)
            {
                Button remove = new() { Content = "Remove", MinHeight = 28 }; Grid.SetColumn(remove, 2); row.Children.Add(remove);
                remove.Click += (_, _) => _ = ReportUi(async () => { SetWorldAnimationActivations(WorldAnimationActivations().Where(s => s.Id != start.Id).ToArray()); await OpenWorldAnimationScenario(); });
            }
            panel.Children.Add(row);
        }
        panel.Children.Add(new TextBlock { Text = "Start seconds                         Start frame (40 fps)", Margin = new(0, 3, 0, 6), TextWrapping = TextWrapping.Wrap });
        TextBox search = new() { MinHeight = 28, ToolTip = "Find an animation by name, root, or entry number" };
        AutomationProperties.SetName(search, "Find animation to schedule"); panel.Children.Add(search);
        ComboBox choices = new() { MinHeight = 28, MaxDropDownHeight = 280, DisplayMemberPath = "Label", Margin = new(0, 4, 0, 4) };
        AutomationProperties.SetName(choices, "Animation binding to schedule"); panel.Children.Add(choices);
        void Filter()
        {
            var matches = Enumerable.Range(1, plan.EntryCount - 1).Select(plan.Entry)
                .Where(e => string.IsNullOrWhiteSpace(search.Text) || e.Name.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || e.Root.Contains(search.Text, StringComparison.OrdinalIgnoreCase) || e.Entry.ToString(CultureInfo.InvariantCulture) == search.Text)
                .Take(100).Select(e => new ScheduledBindingChoice(e.Entry, e.Name + $" · {e.Root} · #{e.Entry}")).ToArray();
            choices.ItemsSource = matches; if (matches.Length > 0) choices.SelectedIndex = 0;
        }
        search.TextChanged += (_, _) => Filter(); Filter();
        Button add = new() { Content = "Add animation", MinHeight = 28, IsEnabled = WorldAnimationActivations().Count < AnimationSchedule.MaximumStarts };
        add.Click += (_, _) => _ = ReportUi(async () =>
        {
            if (choices.SelectedItem is not ScheduledBindingChoice choice) throw new StudioCommandException("not_ready", "Choose an exact animation binding.");
            SetWorldAnimationActivations([.. WorldAnimationActivations(), new(Guid.NewGuid(), choice.Entry, 0)]); await OpenWorldAnimationScenario();
        });
        panel.Children.Add(add);
        Button fit = new() { Content = "Fit scenario", MinHeight = 28, Margin = new(0, 4, 0, 0) }; fit.Click += (_, _) => _ = ReportUi(FitWorldAnimationScenario); panel.Children.Add(fit);
        panel.Children.Add(new TextBlock { Text = "Called animations remain opt-in. Expand each animation in the timeline to include its calls. Start times affect this preview only.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 4) });
        return panel;
    }
    private sealed record ScheduledBindingChoice(int Entry, string Label);
}
