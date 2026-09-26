using System.Globalization;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;

namespace Recoil.Zbd.Desktop;

public partial class AnimationEditor
{
    private Task optionWork = Task.CompletedTask;
    internal int PreviewLod => Lod.SelectedIndex;
    internal object PreviewState() => new
    {
        entry = entryIndex, playing, time = frame?.Time, duration, range = SeekSlider.Maximum, customRange,
        options = Options, height = appliedHeight, lod = Lod.SelectedIndex, difficulty = SelectedDifficulty.ToString(),
        speed = new[] { .25, .5, 1, 2, 4 }[Math.Clamp(Speed.SelectedIndex, 0, 4)], replay = Loop.IsChecked == true,
        mute = Mute.IsChecked == true, volume = Volume.Value, phase = Phase.SelectedIndex == 0 ? "runtime" : "cleanup", seed = appliedSeed,
        condition = Condition.SelectedIndex, activationStart, activationTarget, root = context?.ResolveRoot(Entry),
        status = Diagnostics.Text, loading = LoadingPanel.Visibility == Visibility.Visible ? LoadingText.Text : null
    };
    internal object RuntimeData(string section, int offset, int limit, string query = "")
    {
        if (offset < 0 || limit is < 1 or > 200) throw new StudioCommandException("invalid_argument", "Use offset >= 0 and limit 1–200.");
        JsonObject args = new() { ["offset"] = offset, ["limit"] = limit, ["query"] = query };
        JsonObject Page<T>(IEnumerable<T> rows, Func<T, string> search) => MainWindow.Page(rows, args, search).Data.AsObject();
        if (section == "problems") { var problems = Page(CurrentProblems(), p => p.Message + " " + p.Category + " " + p.Scope); problems["approximation"] = "Preview includes game-dependent approximations."; return problems; }
        if (frame == null) return new { unavailable = true };
        return section switch
        {
            "events" => EventPage(),
            "sequences" => Page(frame.Sequences, s => s.Name + " " + s.State),
            "scene" => Page((context?.Scene.Nodes ?? []).Select(n => new { n.Index, n.Name, n.Class, n.Metadata }), n => n.Name + " " + n.Class),
            _ => PreviewState()
        };
        JsonObject EventPage() { var page = Page(frame.Trace, e => e.Name + " " + e.Status); page["dropped"] = frame.TraceDropped; return page; }
    }
    internal async Task SetPreviewOptionAsync(string name, JsonNode value)
    {
        if (HasPendingDrafts) throw new StudioCommandException("pending_drafts", "Resolve preview drafts first.");
        optionWork = Task.CompletedTask;
        double Numeric(double min, double max) { double n = value.GetValue<double>(); if (!double.IsFinite(n) || n < min || n > max) throw new StudioCommandException("invalid_argument", $"Value must be {min}–{max}."); return n; }
        int Integer(int min, int max) { int n = value.GetValue<int>(); if (n < min || n > max) throw new StudioCommandException("invalid_argument", "Integer is outside the supported range."); return n; }
        switch (name)
        {
            case "map": ShowLevel.IsChecked = value.GetValue<bool>(); break;
            case "horizon": ShowHorizon.IsChecked = value.GetValue<bool>(); break;
            case "grid": ShowGrid.IsChecked = value.GetValue<bool>(); break;
            case "collision": GroundCollision.IsChecked = value.GetValue<bool>(); break;
            case "followCamera": FollowCamera.IsChecked = value.GetValue<bool>(); break;
            case "effects": Lighting.IsChecked = value.GetValue<bool>(); break;
            case "height": PreviewHeight.Text = Numeric(-999,999).ToString(CultureInfo.InvariantCulture); break;
            case "lod": Lod.SelectedIndex = Integer(0, Math.Max(0,Lod.Items.Count - 1)); break;
            case "difficulty": if (!Enum.TryParse<MissionDifficulty>(value.GetValue<string>(), out var difficulty) || !Enum.IsDefined(difficulty)) throw new StudioCommandException("invalid_argument", "Difficulty must be Easy, Medium or Hard."); Difficulty.SelectedItem = difficulty; break;
            case "speed": int index = Array.IndexOf(new[] { .25, .5, 1, 2, 4 }, Numeric(.25,4)); if (index < 0) throw new StudioCommandException("invalid_argument", "Speed must be 0.25, 0.5, 1, 2 or 4."); Speed.SelectedIndex = index; break;
            case "replay": Loop.IsChecked = value.GetValue<bool>(); break;
            case "mute": Mute.IsChecked = value.GetValue<bool>(); break;
            case "volume": Volume.Value = Numeric(0,1); break;
            case "phase": string phase = value.GetValue<string>(); if (phase is not ("runtime" or "cleanup")) throw new StudioCommandException("invalid_argument", "Phase must be runtime or cleanup."); Phase.SelectedIndex = phase == "runtime" ? 0 : 1; break;
            case "condition": Condition.SelectedIndex = Integer(0,2); break;
            case "seed": Seed.Text = value.GetValue<int>().ToString(CultureInfo.InvariantCulture); await SeedChangedAsync(this,new()); break;
            case "range": customRange = true; ApplyRange(Math.Ceiling(Numeric(AnimationPlayer.StepSeconds,3600) * 60) / 60); await SeekAsync(frame?.Time ?? 0); break;
            case "autoRange": if (!value.GetValue<bool>()) throw new StudioCommandException("invalid_argument", "Use true to restore automatic range."); await AutoRangeClickAsync(this,new()); break;
            case "traceRange": Timeline.Duration = Numeric(AnimationPlayer.StepSeconds,3600); Timeline.InvalidateVisual(); break;
            case "fitTrace": if (value.GetValue<bool>()) FitTraceClick(this,new()); break;
            case "followLog": FollowLatest.IsChecked = value.GetValue<bool>(); break;
            case "problemFilter": ProblemFilter.SelectedIndex = Integer(0,4); break;
            case "worldPath": await InitializeAsync(value.GetValue<string>()); break;
            case "root": if (context == null) throw new StudioCommandException("not_ready", "No scene is loaded."); context.RootOverrides[entryIndex] = Integer(0,context.Scene.Nodes.Count - 1); resetSimulation = true; await SeekAsync(0); break;
            case "activationOrigin": case "activationTarget":
                SetActivation(value.GetValue<string>(), name == "activationOrigin"); break;
            default: throw new StudioCommandException("unknown_option", name);
        }
        Task previous;
        do { previous = optionWork; await previous; } while (previous != optionWork);
        if (disposed) throw new StudioCommandException("context_changed", "Animation preview was replaced.");
    }
    internal async Task TransportAsync(string action, double seconds = 0)
    {
        if (HasPendingDrafts) throw new StudioCommandException("pending_drafts", "Resolve unfinished preview input first.");
        if (player == null || LoadingPanel.Visibility == Visibility.Visible) throw new StudioCommandException("not_ready", LoadingText.Text);
        switch (action)
        {
            case "play": if (!playing) TogglePlayback(); break;
            case "pause": Pause(); break;
            case "stop": pendingPlay = false; await SeekAsync(0); break;
            case "previous": await SeekAsync(Math.Max(0,(frame?.Time ?? 0) - AnimationPlayer.StepSeconds)); break;
            case "next": await SeekAsync(Math.Min(SeekSlider.Maximum,(frame?.Time ?? 0) + AnimationPlayer.StepSeconds)); break;
            case "seek": if (!double.IsFinite(seconds) || seconds < 0 || seconds > SeekSlider.Maximum) throw new StudioCommandException("invalid_argument", "Seek position must be within the preview range."); await SeekAsync(seconds); break;
            default: throw new StudioCommandException("invalid_argument", "Unknown transport action.");
        }
    }
}
