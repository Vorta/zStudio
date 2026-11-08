using System.IO;
using System.Windows;
using System.Windows.Controls;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private sealed class SourceMissionPlayback(SourceWorldSession source, WorldAnimationTarget target, double time, bool playing, bool restore)
    {
        internal SourceWorldSession Source { get; } = source;
        internal WorldAnimationTarget Target { get; } = target;
        internal double Time { get; set; } = time;
        internal bool Playing { get; set; } = playing;
        internal bool Restore { get; set; } = restore;
        internal long Version { get; set; }
    }
    private SourceMissionPlayback? sourceMissionPlayback;

    private void RecordSourceMissionTransport(string action, double seconds)
    {
        if (sourceMissionPlayback is not { } pending || animationMode?.Document.SourceWorld != pending.Source) return;
        double current = worldAnimationSession?.State.Time ?? pending.Time;
        switch (action)
        {
            case "stop": pending.Restore = false; pending.Playing = false; pending.Time = 0; break;
            case "pause": pending.Time = current; pending.Playing = false; break;
            case "play": pending.Time = current; pending.Restore = pending.Playing = true; break;
            case "seek": pending.Time = seconds; pending.Restore = true; pending.Playing = false; break;
            case "start": pending.Time = worldAnimationSession?.RangeStart ?? worldAnimationRangeStart; pending.Restore = true; pending.Playing = false; break;
            case "previous": case "next": pending.Time = Math.Max(0, current + (action == "next" ? 1 : -1) / 60.0); pending.Restore = true; pending.Playing = false; break;
            default: return;
        }
        pending.Version++;
    }
    private bool PopulateSourceMissions(DocumentModel document)
    {
        if (document.SourceWorld is not { } session || document.Document.Game != GameVariant.MechWarrior3) return false;
        bool prior = updating; updating = true;
        try
        {
            var choices = SourceGameProfile.Missions(session.Workspace, session.Mission);
            string? current = (document.Document as SourceSceneSnapshot)?.SourceMission;
            session.SourceMission = current;
            WorldMission.ItemsSource = choices;
            WorldMission.SelectedItem = choices.FirstOrDefault(c => c.SourceMission.Equals(current, StringComparison.OrdinalIgnoreCase));
            WorldMission.Visibility = Visibility.Visible;
            WorldMission.ToolTip = "Authored source mission";
        }
        finally { updating = prior; }
        return true;
    }

    private async Task<DocumentModel> SelectSourceMissionAsync(DocumentModel document, string sourceMission, CancellationToken token)
    {
        var session = SourceWorldOf(document);
        if (document.Document.Game != GameVariant.MechWarrior3) throw new StudioCommandException("unsupported", "RECOIL uses difficulty layouts rather than source missions.");
        RequireSourceWorldIdle(session); RequireNoDrafts();
        if (operation != null) throw new StudioCommandException("busy", "Wait for the current operation before switching missions.");
        var choices = SourceGameProfile.Missions(session.Workspace, session.Mission, token);
        var selected = choices.SingleOrDefault(c => c.SourceMission.Equals(sourceMission, StringComparison.OrdinalIgnoreCase))
            ?? throw new StudioCommandException("invalid_argument", "Choose a sourceMission returned by missions for this campaign.");
        if ((document.Document as SourceSceneSnapshot)?.SourceMission == selected.SourceMission) return document;
        long generation = ViewModel.WorkspaceGeneration;
        var previousTarget = animationMode?.Document == document ? animationMode : null;
        var playback = previousTarget == null ? null : new SourceMissionPlayback(session, previousTarget,
            worldAnimationSession?.State.Time ?? 0, worldAnimationSession?.IsPlaying == true, worldAnimationSession != null);
        sourceMissionPlayback = playback;
        string? previous = session.SourceMission;
        SetSourceRebuilding(session, true);
        try
        {
            session.SourceMission = selected.SourceMission;
            var result = await RebuildSourceWorldAsync(session, token);
            if (generation != ViewModel.WorkspaceGeneration || shownDocument != result || session.Owner != result)
                throw new StudioCommandException("context_changed", "Source mission selection was superseded.");
            while (playback is { Restore: true } && animationMode is { } target && target.Document == result
                && target.Owner == playback.Target.Owner && target.Root == playback.Target.Root)
            {
                long intent = playback.Version;
                try
                {
                    await WorldAnimationTransportCoreAsync("seek", playback.Time, token);
                    if (playback.Version == intent && playback.Playing && animationMode == target)
                        await WorldAnimationTransportCoreAsync("play", 0, token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested && playback.Version != intent) { continue; }
                if (playback.Version == intent) break;
            }
            if (result.Document is SourceSceneSnapshot snapshot && snapshot.SourceMission == selected.SourceMission)
            {
                string path = SourceProject.Resolve(session.Root, session.ScriptPath);
                var memory = ViewModel.Settings.SourceMissions;
                if (memory.Count >= 128 && !memory.ContainsKey(path)) memory.Remove(memory.Keys.First());
                memory[path] = selected.SourceMission;
                ViewModel.Settings.Save();
            }
            return result;
        }
        catch
        {
            // Publication may have succeeded before presentation was superseded; the accepted snapshot owns the choice.
            session.SourceMission = (session.Owner?.Document as SourceSceneSnapshot)?.SourceMission ?? previous;
            throw;
        }
        finally { if (sourceMissionPlayback == playback) sourceMissionPlayback = null; SetSourceRebuilding(session, false); }
    }
}
