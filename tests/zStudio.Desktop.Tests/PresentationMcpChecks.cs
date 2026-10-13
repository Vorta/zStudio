using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

// Shares WindowClosingTests' one STA/Application. HWND checks run on the private test desktop;
// none of these checks starts Fly, captures input, or sends physical keyboard events.
internal static class PresentationMcpChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run(Application app)
    {
        PresentationRendererChecks.Run();
        string folder = Directory.CreateTempSubdirectory("zstudio-presentation-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var token = deadline.Token;
        var main = new MainWindow { Left = 40, Top = 40, Width = 1000, Height = 700, ShowInTaskbar = false };
        main.Show();
        try
        {
            Assert.Equal("fullscreen", MainWindow.PresentationShortcut(Key.F11, ModifierKeys.None));
            Assert.Equal("uiHidden", MainWindow.PresentationShortcut(Key.H, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.Null(MainWindow.PresentationShortcut(Key.F11, ModifierKeys.Control));
            Assert.Null(MainWindow.PresentationShortcut(Key.H, ModifierKeys.Control));
            Assert.Null(MainWindow.PresentationShortcut(Key.H, ModifierKeys.Control | ModifierKeys.Shift | ModifierKeys.Alt));
            Assert.Null(MainWindow.PresentationShortcut(Key.Escape, ModifierKeys.None));
            State(await Present(), false, false, false);
            await Assert.ThrowsAsync<StudioCommandException>(() => Present(true, true));
            State(await Present(false, false), false, false, false);

            string worldPath = Path.Combine(folder, "gamez.zbd"), animationPath = Path.Combine(folder, "anim.zbd");
            await File.WriteAllBytesAsync(worldPath, ModelFixture.GameZ(), token);
            await File.WriteAllBytesAsync(Path.Combine(folder, "texture2.zbd"), ModelFixture.Texture(), token);
            var files = VtolDropFixture.Files();
            files.Content[VtolDropFixture.Root] = Encoding.Latin1.GetBytes(VtolDropFixture.Definitions("root"));
            files.Content["data/m1/zrdr/flight.zan"] = Encoding.ASCII.GetBytes(VtolDropFixture.MotionScript.Replace("ground", "root", StringComparison.Ordinal));
            await File.WriteAllBytesAsync(animationPath, AnimationWriter.Write(AnimationCompiler.Compile(files, VtolDropFixture.Root, ["root"], token).Package), token);
            await main.ViewModel.OpenRootAsync(folder, token);
            var world = (await main.ViewModel.OpenFileAsync(worldPath, token))!;
            await Preview();
            // A raw model preview intentionally has no placement identity. Zone painting needs
            // the fixture's actual scene node, just as it does in a Whole world preview.
            world.SelectedAsset = world.Assets.First(a => a.Record.Kind == Recoil.Zbd.Core.AssetKind.Node);
            await Preview();
            var viewport = Assert.IsType<SceneViewport>(((ContentControl)main.FindName("SceneHost")).Content);
            State(await Present(), false, false, true);
            await CameraSettled(viewport);
            PresentationRendererChecks.CheckSurfaceStrokes(viewport);
            var view = viewport.CaptureView();
            string layout = JsonSerializer.Serialize(main.ViewModel.Settings.GetWorkspace());
            var hwnd = new WindowInteropHelper(main).Handle;
            var ordinaryChrome = WindowChrome.GetWindowChrome(main);
            var ordinaryStyle = main.WindowStyle; var ordinaryResize = main.ResizeMode;

            // Both show states restore the native normal rectangle, not a work-area/screen-coordinate mix.
            foreach (var mode in new[] { WindowState.Normal, WindowState.Maximized })
            {
                main.WindowState = mode; await Idle();
                var placement = Placement(hwnd);
                Send(main, Key.F11); await Idle();
                State(await Present(), true, false, true);
                AssertFullscreen();
                Assert.False(((Button)main.FindName("CaptionMaximize")).IsEnabled);
                Assert.Equal("busy", (await Assert.ThrowsAsync<StudioCommandException>(() => Command("window", new() { ["action"] = "maximize" }))).Code);
                await Command("window", new() { ["action"] = "minimize" });
                Assert.True(main.PresentationState.Fullscreen);
                await Command("window", new() { ["action"] = "restore" }); await Idle(); AssertFullscreen();
                Send(main, Key.F11); await Idle();
                Assert.Equal(placement.Normal, Placement(hwnd).Normal);
                Assert.Equal(placement.Show, Placement(hwnd).Show);
                Assert.Equal(mode, main.WindowState);
                Assert.Same(ordinaryChrome, WindowChrome.GetWindowChrome(main));
                Assert.Equal(ordinaryStyle, main.WindowStyle); Assert.Equal(ordinaryResize, main.ResizeMode);
                Assert.Same(viewport, ((ContentControl)main.FindName("SceneHost")).Content);
                Assert.Equal(view, viewport.CaptureView());
            }
            main.WindowState = WindowState.Normal; await Idle();

            // A valid uncommitted field exposes accidental LostKeyboardFocus commits; invalid text
            // separately proves presentation never resolves/discards the draft just to hide chrome.
            var owner = (await main.ViewModel.OpenFileAsync(animationPath, token, activate: false))!;
            main.OpenAnimationProperties(owner, 0, Guid.Empty, Guid.Empty); await Idle();
            var popup = main.OpenPropertiesWindow!; var fields = popup.AnimationFields!;
            var input = Descendants(fields).OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Reset delay (s)");
            long revision = owner.Revision; float original = owner.AnimationEdits!.Package.Entries[0].F32(164);
            layout = JsonSerializer.Serialize(main.ViewModel.Settings.GetWorkspace());
            foreach (string text in new[] { "4.5", "-" })
            {
                popup.Activate(); Keyboard.Focus(input); await Idle();
                Assert.Same(input, Keyboard.FocusedElement); input.Text = text;
                Assert.True(popup.HasPendingDrafts);
                State(await Present(fullscreen: true, uiHidden: true), true, true, true); await Idle();
                Assert.False(popup.IsVisible); Assert.Same(popup, main.OpenPropertiesWindow);
                Assert.Same(fields, popup.AnimationFields); Assert.Equal(text, input.Text);
                Assert.True(popup.HasPendingDrafts); Assert.Equal(revision, owner.Revision);
                Assert.Equal(original, owner.AnimationEdits.Package.Entries[0].F32(164)); Assert.False(owner.IsDirty);
                foreach (string name in new[] { "TitleArea", "PresentationStatusBar", "PreviewHeading", "PreviewFooter", "PreviewToolbarHost", "PreviewOverlayChrome" })
                    Assert.False(((FrameworkElement)main.FindName(name)).IsVisible);
                Assert.True(viewport.IsEnabled); Assert.True(viewport.IsVisible); Assert.True(viewport.PresentationUiHidden);
                State(await Present(fullscreen: false), false, true, true);
                Assert.False(popup.IsVisible);
                State(await Present(uiHidden: false), false, false, true); await Idle();
                Assert.True(popup.IsVisible); Assert.Same(popup, main.OpenPropertiesWindow);
                Assert.Equal(text, input.Text); Assert.True(popup.HasPendingDrafts);
                Assert.Equal(revision, owner.Revision); Assert.False(owner.AnimationEdits.CanUndo);
                Assert.Equal(layout, JsonSerializer.Serialize(main.ViewModel.Settings.GetWorkspace()));
                Assert.Same(viewport, ((ContentControl)main.FindName("SceneHost")).Content); Assert.Equal(view, viewport.CaptureView());
                Send(input, Key.Escape); await Idle(); Assert.False(popup.HasPendingDrafts);
            }
            // The owned popup's separate keyboard route forwards F11 without closing or replacing it.
            Send(input, Key.F11); await Idle(); Assert.True(main.PresentationState.Fullscreen);
            Send(input, Key.F11); await Idle(); Assert.False(main.PresentationState.Fullscreen);
            var hide = (MenuItem)main.FindName("HideUiMenu"); hide.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            await Idle(); Assert.True(main.PresentationState.UiHidden);
            await Command("properties_close", new()); Assert.Null(main.OpenPropertiesWindow);
            await Command("properties_open", new() { ["document"] = owner.SessionId.ToString(), ["kind"] = "Animation", ["index"] = 0 });
            var hiddenPopup = main.OpenPropertiesWindow!; Assert.False(hiddenPopup.IsVisible);
            State(await Present(uiHidden: false), false, false, true); await Idle();
            Assert.Same(hiddenPopup, main.OpenPropertiesWindow); Assert.True(hiddenPopup.IsVisible);
            await Present(uiHidden: true); await Command("properties_close", new());
            await Present(uiHidden: false); Assert.Null(main.OpenPropertiesWindow);

            // Use the real compiled-animation viewer, then keep its player and runtime state intact.
            main.ViewModel.SelectedDocument = owner; await Preview();
            var editor = Assert.IsType<AnimationEditor>(typeof(MainWindow).GetField("animation", Fields)!.GetValue(main));
            await editor.TransportAsync("seek", 1.25); editor.Pause();
            await CameraSettled(editor.Viewport);
            var player = typeof(AnimationEditor).GetField("player", Fields)!.GetValue(editor);
            var animationViewport = editor.Viewport; var animationView = animationViewport.CaptureView();
            double time = editor.CurrentFrame!.Time;
            var options = JsonSerializer.Serialize(editor.Options);
            await Command("workspace_view", new() { ["changes"] = new JsonObject { ["inspector"] = true, ["inspectorTab"] = 1 } }); await Idle();
            var seed = (TextBox)editor.FindName("Seed"); seed.BringIntoView(); Keyboard.Focus(seed); await Idle();
            Assert.Same(seed, Keyboard.FocusedElement); string originalSeed = seed.Text; seed.Text = "2";
            string previewDraft = editor.DraftToken;
            State(await Present(uiHidden: true), false, true, true); await Idle();
            Assert.True(editor.PresentationUiHidden);
            Assert.False(((FrameworkElement)editor.FindName("ViewOptionsRow")).IsVisible);
            Assert.False(((FrameworkElement)editor.FindName("PlayerTransport")).IsVisible);
            Assert.NotEqual(Visibility.Collapsed, ((FrameworkElement)editor.FindName("ScreenOverlay")).Visibility);
            Assert.NotEqual(Visibility.Collapsed, ((FrameworkElement)editor.FindName("WaveOverlay")).Visibility);
            await Present(uiHidden: false); await Idle();
            Assert.Same(editor, typeof(MainWindow).GetField("animation", Fields)!.GetValue(main));
            Assert.Same(player, typeof(AnimationEditor).GetField("player", Fields)!.GetValue(editor));
            Assert.Same(animationViewport, editor.Viewport); Assert.Equal(time, editor.CurrentFrame.Time);
            Assert.Equal(animationView, editor.Viewport.CaptureView()); Assert.Equal(options, JsonSerializer.Serialize(editor.Options));
            Assert.Equal("2", seed.Text); Assert.Equal(previewDraft, editor.DraftToken);
            Assert.Equal(1, JsonSerializer.SerializeToNode(editor.PreviewState())!["seed"]!.GetValue<int>());
            seed.Text = originalSeed;
            await editor.TransportAsync("play"); Assert.True(editor.IsPlaying);
            await Present(uiHidden: true); await Present(uiHidden: false);
            Assert.True(editor.IsPlaying); Assert.Same(player, typeof(AnimationEditor).GetField("player", Fields)!.GetValue(editor));
            editor.Pause();

            // Presentation suppresses layout-induced boundaries only; a real field-to-field
            // focus change must still accept valid Seed input and reset preview playback.
            Keyboard.Focus(seed); await Idle(); Assert.Same(seed, Keyboard.FocusedElement); seed.Text = "3";
            var endTime = (TextBox)editor.FindName("EndTime"); Keyboard.Focus(endTime); await Idle();
            Assert.Same(endTime, Keyboard.FocusedElement); await editor.AwaitOptionWorkAsync();
            Assert.Equal(3, JsonSerializer.SerializeToNode(editor.PreviewState())!["seed"]!.GetValue<int>());
            Assert.Equal(0, editor.CurrentFrame!.Time); Assert.Equal(revision, owner.Revision); Assert.False(owner.IsDirty);

            // Observe the actual host attachment, before ShowAsync and the final shell refresh.
            // Newly published 3D content must already inherit the retained hidden override.
            await Present(uiHidden: true);
            main.ViewModel.SelectedDocument = world; await Preview();
            var animationHost = (ContentControl)main.FindName("AnimationHost");
            var content = DependencyPropertyDescriptor.FromProperty(ContentControl.ContentProperty, typeof(ContentControl));
            bool attached = false, hiddenAtAttachment = false;
            EventHandler observe = (_, _) => { if (animationHost.Content is AnimationEditor added) { attached = true; hiddenAtAttachment = added.PresentationUiHidden && added.Viewport.PresentationUiHidden && ((FrameworkElement)added.FindName("ViewOptionsRow")).Visibility == Visibility.Collapsed && ((FrameworkElement)added.FindName("PlayerTransport")).Visibility == Visibility.Collapsed; } };
            content.AddValueChanged(animationHost, observe);
            try { main.ViewModel.SelectedDocument = owner; await Preview(); }
            finally { content.RemoveValueChanged(animationHost, observe); }
            Assert.True(attached, "A new animation editor must attach to the host.");
            Assert.True(hiddenAtAttachment, "The new editor, chrome and renderer must inherit Hide UI before host attachment.");
            State(await Present(), false, true, true);

            // Non-3D navigation must leave an exit route, while fullscreen remains independent.
            await Present(fullscreen: true, uiHidden: true);
            main.ViewModel.SelectedDocument = null; await Preview(); await Idle();
            State(await Present(), true, false, false);
            await Assert.ThrowsAsync<StudioCommandException>(() => Present(uiHidden: true));
            State(await Present(fullscreen: false), false, false, false);
            main.ViewModel.SelectedDocument = world; await Preview(); await Idle();
            main.Width += 37; main.Height += 19; await Idle();
            double savedWidth = main.ActualWidth, savedHeight = main.ActualHeight;
            Assert.NotEqual(main.ViewModel.Settings.Width, savedWidth);
            main.WindowState = WindowState.Maximized; await Idle();
            await Present(fullscreen: true, uiHidden: true);
            main.Close(); await Idle(); Assert.False(main.IsLoaded);
            // ActualSize and native RestoreBounds can differ by a final floating-point bit at
            // fractional DPI. Native rectangle restoration above remains an exact pixel check.
            var saved = StudioSettings.Load(); Assert.Equal(savedWidth, saved.Width, 8); Assert.Equal(savedHeight, saved.Height, 8);
            var reopened = new MainWindow { ShowInTaskbar = false };
            try { reopened.Show(); Assert.False(reopened.PresentationState.Fullscreen); Assert.False(reopened.PresentationState.UiHidden); }
            finally { reopened.Close(); }

            async Task<JsonNode> Command(string name, JsonObject args) => (await main.Commands.ExecuteAsync("zstudio_" + name, args, token)).Data;
            Task<JsonNode> Present(bool? fullscreen = null, bool? uiHidden = null)
            { JsonObject args = new(); if (fullscreen.HasValue) args["fullscreen"] = fullscreen.Value; if (uiHidden.HasValue) args["uiHidden"] = uiHidden.Value; return Command("presentation", args); }
            async Task Preview() => await ((Task)typeof(MainWindow).GetField("previewWork", Fields)!.GetValue(main)!).WaitAsync(token);
            void AssertFullscreen()
            {
                Assert.Equal(hwnd, new WindowInteropHelper(main).Handle);
                var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                Assert.True(GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor));
                Assert.True(GetWindowRect(hwnd, out var actual)); Assert.Equal(monitor.Monitor, actual);
                Assert.Equal(0, GetWindowLongPtr(hwnd, -20).ToInt64() & 8); // WS_EX_TOPMOST
            }
        }
        finally
        {
            if (main.IsLoaded)
            {
                main.OpenPropertiesWindow?.CloseResolved(); main.SetPresentation(false, false);
                foreach (var doc in main.ViewModel.Documents) doc.AnimationEdits?.MarkSaved();
                main.Close(); await Idle();
            }
            Directory.Delete(folder, true);
        }
        Assert.DoesNotContain(app.Windows.Cast<Window>(), w => w.Title is "Resolve property input" or "Unsaved changes");
    }

    internal static async Task CheckMotion(MainWindow main, MotionEditor editor)
    {
        await editor.PresentationWork; await CameraSettled(editor.Viewport);
        var viewport = editor.Viewport; var pose = viewport.CaptureView(); var member = editor.AssemblyMember; int lod = editor.Lod;
        var sampler = typeof(MotionEditor).GetField("sampler", Fields)!.GetValue(editor);
        var state = JsonSerializer.SerializeToNode(editor.State)!;
        await main.Commands.ExecuteAsync("zstudio_presentation", new() { ["uiHidden"] = true }); await Idle();
        Assert.True(editor.PresentationUiHidden); Assert.True(viewport.PresentationUiHidden);
        Assert.False(((FrameworkElement)typeof(MotionEditor).GetField("presentationToolbar", Fields)!.GetValue(editor)!).IsVisible);
        Assert.False(((FrameworkElement)typeof(MotionEditor).GetField("presentationTransport", Fields)!.GetValue(editor)!).IsVisible);
        await main.Commands.ExecuteAsync("zstudio_presentation", new() { ["uiHidden"] = false }); await Idle();
        Assert.Same(viewport, editor.Viewport); Assert.Same(sampler, typeof(MotionEditor).GetField("sampler", Fields)!.GetValue(editor));
        Assert.Equal(pose, viewport.CaptureView()); Assert.Equal(member, editor.AssemblyMember); Assert.Equal(lod, editor.Lod);
        Assert.Equal(state["seconds"]!.GetValue<double>(), JsonSerializer.SerializeToNode(editor.State)!["seconds"]!.GetValue<double>());
        editor.Play(); Assert.True(editor.IsPlaying);
        await main.Commands.ExecuteAsync("zstudio_presentation", new() { ["uiHidden"] = true });
        await main.Commands.ExecuteAsync("zstudio_presentation", new() { ["uiHidden"] = false });
        Assert.True(editor.IsPlaying); Assert.Same(sampler, typeof(MotionEditor).GetField("sampler", Fields)!.GetValue(editor)); editor.Pause();
        await editor.PresentationWork;
        await main.Commands.ExecuteAsync("zstudio_presentation", new() { ["uiHidden"] = true });
        // Hold final pose publication: SceneChanged's later shell refresh must not be what
        // first hides a newly attached viewport's guides.
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(MotionEditor).GetField("sampling", Fields)!.SetValue(editor, release.Task);
        editor.Play();
        var selecting = editor.SelectAssemblyAsync(member!.Value);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (ReferenceEquals(viewport, editor.Viewport)) await Task.Delay(10, deadline.Token);
            Assert.False(selecting.IsCompleted);
            Assert.True(editor.PresentationUiHidden); Assert.True(editor.Viewport.PresentationUiHidden);
            Assert.Same(editor, typeof(MainWindow).GetField("motion", Fields)!.GetValue(main));
            Assert.False(((FrameworkElement)typeof(MotionEditor).GetField("presentationToolbar", Fields)!.GetValue(editor)!).IsVisible);
        }
        finally { release.TrySetResult(); }
        await selecting;
        Assert.True(editor.IsPlaying); Assert.Equal(member, editor.AssemblyMember);
        await main.Commands.ExecuteAsync("zstudio_presentation", new() { ["uiHidden"] = false }); editor.Pause();
    }

    private static void State(JsonNode state, bool fullscreen, bool hidden, bool available)
    { Assert.Equal(fullscreen, state["fullscreen"]!.GetValue<bool>()); Assert.Equal(hidden, state["uiHidden"]!.GetValue<bool>()); Assert.Equal(available, state["available"]!.GetValue<bool>()); }
    private static async Task Idle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(20); }
    private static async Task CameraSettled(SceneViewport viewport)
    {
        // FrameAll initially stores +Y; the ordinary render loop orthogonalizes it in
        // PrepareCameraFrame before RenderContext captures the pose. Await that real frame,
        // with no presentation transition, forced render, resize or direct preparation call.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!viewport.IsOrbitPickingReady || (bool)typeof(SceneViewport).GetField("cameraPoseDirty", Fields)!.GetValue(viewport)! ||
            (TimeSpan)typeof(SceneViewport).GetField("previousFrameTime", Fields)!.GetValue(viewport)! == TimeSpan.Zero)
            await Task.Delay(10, deadline.Token);
        var settled = viewport.CaptureView(); await Idle(); Assert.Equal(settled, viewport.CaptureView());
    }
    private static void Send(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child; }
    private static NativePlacement Placement(nint handle)
    { Assert.Equal(44, Marshal.SizeOf<NativePlacement>()); var result = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() }; Assert.True(GetWindowPlacement(handle, ref result)); return result; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePlacement { public int Length; public uint Flags, Show; public NativePoint Min, Max; public NativeRect Normal; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint handle, ref NativePlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] private static extern bool GetMonitorInfo(nint handle, ref MonitorInfo monitor);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
}
