using System.IO;
using System.Globalization;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Animation;

internal static class AnimationRefreshRecoveryCheck
{
    internal static async Task Run(AnimationEditor editor)
    {
        await editor.SetPreviewOptionAsync("map", JsonValue.Create(false)!);
        await editor.TransportAsync("seek", .5);
        var view = editor.Viewport.CaptureView();
        var loading = (FrameworkElement)editor.FindName("LoadingPanel");
        double originalHeight = double.Parse(((TextBox)editor.FindName("PreviewHeight")).Text, CultureInfo.InvariantCulture);
        var difficulty = (ComboBox)editor.FindName("Difficulty");
        string originalDifficulty = difficulty.SelectedItem!.ToString()!;
        // Information is emitted only after ShowAsync has replaced the meshes.
        // This exercises late cancellation, not the initial loading overlay.
        await CancelAfterMeshes("map", JsonValue.Create(true)!);
        await CancelAfterMeshes("difficulty", JsonValue.Create(originalDifficulty == "Hard" ? "Easy" : "Hard")!);
        await editor.SetPreviewOptionAsync("difficulty", JsonValue.Create(originalDifficulty)!);
        await editor.TransportAsync("seek", .5);
        if (editor.Viewport.AnimationMeshCount == 0)
            throw new InvalidDataException("Recovered scene cannot render the animation after seeking.");
        await CheckSeekSupersession(false);
        await CheckSeekSupersession(true);
        // Canceling a seek locally is not an MCP revocation and must not cause
        // the canceled seek to restart over subsequent GUI playback intent.
        var playButton = (Button)editor.FindName("PlayButton");
        DependencyPropertyChangedEventHandler cancelLocally = (_, _) =>
        {
            if (playButton.IsEnabled) return;
            ((CancellationTokenSource)typeof(AnimationEditor).GetField("seeking", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!).Cancel();
            editor.TogglePlayback(); // GUI Play queues playback while a seek owns the disabled button.
        };
        playButton.IsEnabledChanged += cancelLocally;
        try { await editor.SeekAsync(.9); }
        finally { playButton.IsEnabledChanged -= cancelLocally; }
        if (Math.Abs(editor.CurrentFrame!.Time - .75) > 1.0 / 60)
            throw new InvalidDataException("A locally canceled seek was restarted as MCP recovery.");
        // Apply the retained Play intent at the next completed GUI seek, then pause.
        await editor.SeekAsync(.5);
        if (!editor.IsPlaying) throw new InvalidDataException("Local seek cancellation lost queued GUI Play intent.");
        editor.Pause();
        await editor.SetPreviewOptionAsync("height", JsonValue.Create(originalHeight)!);
        var lod = (ComboBox)editor.FindName("Lod");
        if (lod.Items.Count > 1)
        {
            int retained = lod.SelectedIndex;
            var gate = (SemaphoreSlim)typeof(AnimationEditor).GetField("simulationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            await gate.WaitAsync();
            try
            {
                using var request = new CancellationTokenSource();
                using var operation = PreviewOperation.Begin(request.Token);
                var change = editor.SetPreviewOptionAsync("lod", JsonValue.Create(retained == 0 ? 1 : 0)!);
                request.Cancel();
                await change.WaitAsync(TimeSpan.FromSeconds(5));
                if (lod.SelectedIndex != retained) throw new InvalidDataException("Canceled queued LOD left an unapplied selection.");
            }
            finally { gate.Release(); }
            await CheckLodSnapshotCancellation();
            await CheckLodSupersession();
        }
        await CheckMapSupersession(false);
        await CheckMapSupersession(true);
        await CheckBindingPresentation();
        editor.Viewport.RestoreView(view);

        async Task CheckBindingPresentation()
        {
            if (lod.Items.Count > 1) await editor.SetPreviewOptionAsync("lod", JsonValue.Create(1)!);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var context = (AnimationPreviewContext)typeof(AnimationEditor).GetField("context", flags)!.GetValue(editor)!;
            bool replaced = false;
            void Replace(string _)
            {
                if (replaced) return;
                replaced = true;
                ((System.Windows.Controls.Primitives.ToggleButton)editor.FindName("ShowLevel")).IsChecked = false;
            }
            editor.Viewport.Information += Replace;
            try { await editor.SetPreviewOptionAsync("worldPath", JsonValue.Create(context.World.Path)!); }
            finally { editor.Viewport.Information -= Replace; }
            var player = (AnimationPlayer)typeof(AnimationEditor).GetField("player", flags)!.GetValue(editor)!;
            int meshes = ((System.Collections.ICollection)editor.Viewport.GetType().GetField("meshes", flags)!.GetValue(editor.Viewport)!).Count;
            if (!replaced || lod.SelectedIndex != 0 || player.LodLevel != 0 || meshes != 0 || loading.Visibility != Visibility.Collapsed)
                throw new InvalidDataException("A world rebind did not reconcile initial LOD and presentation changes made during upload.");
            await editor.SetPreviewOptionAsync("map", JsonValue.Create(true)!);
            await editor.TransportAsync("seek", .5);
        }

        async Task CheckLodSnapshotCancellation()
        {
            await editor.SetPreviewOptionAsync("lod", JsonValue.Create(0)!);
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var document = (DocumentModel)typeof(AnimationEditor).GetField("document", flags)!.GetValue(editor)!;
            var context = (AnimationPreviewContext)typeof(AnimationEditor).GetField("context", flags)!.GetValue(editor)!;
            var contextTask = typeof(DocumentModel).GetField("animationContext", flags)!;
            object? retainedTask = contextTask.GetValue(document);
            TaskCompletionSource<AnimationPreviewContext> contextReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
            contextTask.SetValue(document, contextReady.Task);
            typeof(AnimationEditor).GetField("contextDirty", flags)!.SetValue(editor, true);
            typeof(AnimationEditor).GetField("resetSimulation", flags)!.SetValue(editor, true);
            int staticMeshes = ((System.Collections.ICollection)editor.Viewport.GetType().GetField("meshes", flags)!.GetValue(editor.Viewport)!).Count;
            using var request = new CancellationTokenSource();
            bool canceled = false;
            void Cancel(string _) { if (!canceled) { canceled = true; request.Cancel(); } }
            editor.Viewport.Information += Cancel;
            try
            {
                // Hold a genuine GUI context refresh inside the simulation lock,
                // then queue an MCP LOD request before its new player is created.
                Task seek = editor.SeekAsync(.5);
                var heldGate = (SemaphoreSlim)typeof(AnimationEditor).GetField("simulationGate", flags)!.GetValue(editor)!;
                if (seek.IsCompleted || heldGate.CurrentCount != 0 || !ReferenceEquals(contextTask.GetValue(document), contextReady.Task))
                    throw new InvalidDataException("LOD snapshot regression did not hold the GUI context refresh at its intended boundary.");
                using var operation = PreviewOperation.Begin(request.Token);
                Task change = editor.SetPreviewOptionAsync("lod", JsonValue.Create(lod.Items.Count - 1)!);
                contextReady.SetResult(context);
                await Task.WhenAll(seek, change).WaitAsync(TimeSpan.FromSeconds(30));
                var player = (AnimationPlayer)typeof(AnimationEditor).GetField("player", flags)!.GetValue(editor)!;
                int rendered = ((System.Collections.ICollection)editor.Viewport.GetType().GetField("meshes", flags)!.GetValue(editor.Viewport)!).Count;
                if (!canceled || lod.SelectedIndex != 0 || player.LodLevel != 0 || rendered != staticMeshes)
                    throw new InvalidDataException($"A canceled queued LOD leaked into the GUI seek's published player or map geometry: canceled={canceled}, picker={lod.SelectedIndex}, player={player.LodLevel}, meshes={rendered}/{staticMeshes}.");
            }
            finally { contextTask.SetValue(document, retainedTask); contextReady.TrySetResult(context); editor.Viewport.Information -= Cancel; }
        }

        async Task CheckLodSupersession()
        {
            var gate = (SemaphoreSlim)typeof(AnimationEditor).GetField("simulationGate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
            await gate.WaitAsync();
            Task change;
            using var request = new CancellationTokenSource();
            using (PreviewOperation.Begin(request.Token)) change = editor.SetPreviewOptionAsync("lod", JsonValue.Create(1)!);
            lod.SelectedIndex = 0; // Newer GUI request, outside the MCP scope.
            gate.Release();
            bool rejected = false;
            try { await change; } catch (StudioCommandException ex) when (ex.Code == "context_changed") { rejected = true; }
            if (!rejected || lod.SelectedIndex != 0) throw new InvalidDataException("A newer GUI LOD request did not supersede MCP LOD.");
        }

        async Task CheckMapSupersession(bool pause)
        {
            await editor.SetPreviewOptionAsync("map", JsonValue.Create(false)!);
            await editor.TransportAsync("play");
            bool replaced = false;
            void Replace(string _)
            {
                if (replaced) return;
                replaced = true;
                using var gui = PreviewOperation.Begin(CancellationToken.None);
                ((System.Windows.Controls.Primitives.ToggleButton)editor.FindName("ShowLevel")).IsChecked = false;
                if (pause) editor.Pause();
            }
            editor.Viewport.Information += Replace;
            bool rejected = false;
            using var request = new CancellationTokenSource();
            try
            {
                using var operation = PreviewOperation.Begin(request.Token);
                await editor.SetPreviewOptionAsync("map", JsonValue.Create(true)!);
            }
            catch (StudioCommandException ex) when (ex.Code == "context_changed") { rejected = true; }
            finally { editor.Viewport.Information -= Replace; }
            int meshes = ((System.Collections.ICollection)editor.Viewport.GetType().GetField("meshes", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor.Viewport)!).Count;
            if (!replaced || !rejected || loading.Visibility != Visibility.Collapsed || meshes != 0 || editor.IsPlaying == pause)
                throw new InvalidDataException("A GUI Map toggle during mesh upload did not supersede MCP scene options.");
            editor.Pause();
            await editor.SetPreviewOptionAsync("map", JsonValue.Create(true)!);
        }

        async Task CheckSeekSupersession(bool option)
        {
            var play = (Button)editor.FindName("PlayButton");
            Task? userSeek = null;
            DependencyPropertyChangedEventHandler replace = (_, _) =>
            {
                if (play.IsEnabled || userSeek != null) return;
                // A real GUI action runs outside the MCP execution context.
                using var gui = PreviewOperation.Begin(CancellationToken.None);
                userSeek = editor.SeekAsync(.75);
            };
            play.IsEnabledChanged += replace;
            bool rejected = false;
            using var request = new CancellationTokenSource();
            try
            {
                using var operation = PreviewOperation.Begin(request.Token);
                if (option) await editor.SetPreviewOptionAsync("height", JsonValue.Create(1.0)!);
                else await editor.TransportAsync("seek", .25);
            }
            catch (StudioCommandException ex) when (ex.Code == "context_changed") { rejected = true; }
            finally { play.IsEnabledChanged -= replace; }
            if (userSeek != null) await userSeek;
            if (!rejected || userSeek == null || Math.Abs(editor.CurrentFrame!.Time - .75) > 1.0 / 60)
                throw new InvalidDataException("A GUI seek did not supersede the MCP " + (option ? "option update" : "transport") + " request.");
        }

        async Task CancelAfterMeshes(string option, JsonNode value)
        {
            using var request = new CancellationTokenSource();
            bool canceled = false;
            void Cancel(string _)
            {
                if (canceled) return;
                canceled = true; request.Cancel();
            }
            editor.Viewport.Information += Cancel;
            try
            {
                using (PreviewOperation.Begin(request.Token)) await editor.SetPreviewOptionAsync(option, value);
            }
            finally { editor.Viewport.Information -= Cancel; }
            if (!canceled || loading.Visibility != Visibility.Collapsed || editor.CurrentFrame == null || editor.Viewport.AnimationMeshCount == 0)
                throw new InvalidDataException("Late canceled " + option + " refresh did not reconstruct the retained animation scene.");
            double time = editor.CurrentFrame.Time;
            if (Math.Abs(time - .5) > 1.0 / 60)
                throw new InvalidDataException("Late canceled " + option + " refresh lost its playhead.");
            await editor.TransportAsync("seek", .75);
            await editor.TransportAsync("seek", .5);
        }
    }
}
