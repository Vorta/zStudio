using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using zStudio.Automation;
using zStudio.Rendering;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

// Shares WindowClosingTests' one STA/Application. HWND checks run on the private test desktop;
// none of these checks starts Fly, captures input, or sends physical keyboard events.
internal static class PresentationMcpChecks
{
    private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run(Application app)
    {
        CheckMaximizedClientGeometry();
        PresentationRendererChecks.Run();
        string folder = Directory.CreateTempSubdirectory("zstudio-presentation-").FullName;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var token = deadline.Token;
        var main = new MainWindow { Left = 40, Top = 40, Width = 1000, Height = 700, ShowInTaskbar = false };
        main.Show();
        try
        {
            Assert.Equal("fullscreen", MainWindow.PresentationShortcut(Key.F11, ModifierKeys.None));
            Assert.Null(MainWindow.PresentationShortcut(Key.F11, ModifierKeys.Control));
            Assert.Equal("uiHidden", MainWindow.PresentationShortcut(Key.F10, ModifierKeys.None));
            Assert.Null(MainWindow.PresentationShortcut(Key.H, ModifierKeys.Control | ModifierKeys.Shift));
            Assert.Null(MainWindow.PresentationShortcut(Key.Escape, ModifierKeys.None));
            State(await Present(), false, false);
            await Assert.ThrowsAsync<StudioCommandException>(() => Present(true));
            State(await Present(false), false, false);

            string worldPath = Path.Combine(folder, "gamez.zbd");
            await File.WriteAllBytesAsync(worldPath, ModelFixture.GameZ(), token);
            await File.WriteAllBytesAsync(Path.Combine(folder, "texture2.zbd"), ModelFixture.Texture(), token);
            await main.ViewModel.OpenRootAsync(folder, token);
            var world = (await main.ViewModel.OpenFileAsync(worldPath, token))!;
            await Preview();
            // A raw model preview intentionally has no placement identity. Zone painting needs
            // the fixture's actual scene node, just as it does in a Whole world preview.
            world.SelectedAsset = world.Assets.First(a => a.Record.Kind == zStudio.Core.AssetKind.Node);
            await Preview();
            var viewport = Assert.IsType<SceneViewport>(((ContentControl)main.FindName("SceneHost")).Content);
            State(await Present(), false, true);
            await CameraSettled(viewport);
            PresentationRendererChecks.CheckSurfaceStrokes(viewport);
            var view = viewport.CaptureView();
            string layout = JsonSerializer.Serialize(main.ViewModel.Settings.GetWorkspace());
            var hwnd = new WindowInteropHelper(main).Handle;
            var ordinaryChrome = WindowChrome.GetWindowChrome(main);
            var ordinaryStyle = main.WindowStyle; var ordinaryResize = main.ResizeMode;

            // A custom maximized client must leave the auto-hide reveal edge outside its HWND client area.
            // A private desktop has no taskbar, so simulate only the shell's query result, never change OS settings.
            uint taskbarEdges = 8;
            main.AutoHideTaskbarEdgesForTest = () => taskbarEdges;
            main.WindowState = WindowState.Maximized; await Idle(); AssertMaximizedClient();
            taskbarEdges = 0;
            SendMessage(hwnd, 0x001A, 0, 0); await Idle(); AssertMaximizedClient();
            taskbarEdges = 8;
            SendMessage(hwnd, 0x001A, 0, 0); await Idle(); AssertMaximizedClient();
            var maximizedPlacement = Placement(hwnd);
            await Command("window", new() { ["action"] = "minimize" }); await Idle();
            SendMessage(hwnd, 0x0112, 0xF120, 0); await Idle(); // native SC_RESTORE retains the maximized placement
            AssertMaximizedClient(); Assert.Equal(maximizedPlacement.Normal, Placement(hwnd).Normal);

            // Both show states restore the native normal rectangle, not a work-area/screen-coordinate mix.
            foreach (var mode in new[] { WindowState.Normal, WindowState.Maximized })
            {
                main.WindowState = mode; await Idle();
                if (mode == WindowState.Maximized) AssertMaximizedClient();
                var placement = Placement(hwnd);
                Send(main, Key.F11); await Idle();
                var shown = await Present();
                // Names why F11 did not take effect when it fails (a refusal lands in the status line).
                Assert.True(shown["fullscreen"]!.GetValue<bool>(), $"F11 from {mode}: status '{main.ViewModel.Status}', modifiers {Keyboard.Modifiers}, available {shown["available"]}");
                State(shown, true, true);
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
                if (mode == WindowState.Maximized) AssertMaximizedClient();
                Assert.Same(ordinaryChrome, WindowChrome.GetWindowChrome(main));
                Assert.Equal(ordinaryStyle, main.WindowStyle); Assert.Equal(ordinaryResize, main.ResizeMode);
                Assert.Same(viewport, ((ContentControl)main.FindName("SceneHost")).Content);
                Assert.Equal(view, viewport.CaptureView());
            }
            main.WindowState = WindowState.Normal; await Idle();

            // Hide UI (F10) leaves the view alone: chrome, guides and the title strip's window dragging go, and come back as they were.
            var chromeElements = new[] { "TitleArea", "PresentationStatusBar", "PreviewOverlayChrome" }.Select(name => (UIElement)main.FindName(name)).ToArray();
            Send(main, Key.F10); await Idle();
            State(await Present(), false, true, hidden: true);
            Assert.All(chromeElements, element => Assert.Equal(Visibility.Collapsed, element.Visibility));
            Assert.True(viewport.PresentationUiHidden); Assert.False(viewport.PresentationGuidesVisible);
            Assert.Equal(0, WindowChrome.GetWindowChrome(main).CaptionHeight);
            // Fullscreen keeps the hidden UI's caption until it shows again.
            await Present(fullscreen: true); await Present(fullscreen: false); await Idle();
            Assert.Equal(0, WindowChrome.GetWindowChrome(main).CaptionHeight);
            Send(main, Key.F10); await Idle();
            State(await Present(), false, true);
            Assert.All(chromeElements, element => Assert.Equal(Visibility.Visible, element.Visibility));
            Assert.False(viewport.PresentationUiHidden); Assert.True(viewport.PresentationGuidesVisible);
            Assert.Equal(((FrameworkElement)main.FindName("TitleArea")).Height, WindowChrome.GetWindowChrome(main).CaptionHeight);
            Assert.Equal(layout, JsonSerializer.Serialize(main.ViewModel.Settings.GetWorkspace()));
            Assert.Equal(view, viewport.CaptureView());

            // Leaving 3D keeps fullscreen, which can then be turned off; it ends Hide UI.
            await Present(uiHidden: true);
            await Present(fullscreen: true);
            main.ViewModel.SelectedDocument = null; await Preview(); await Idle();
            State(await Present(), true, false);
            Assert.Equal(Visibility.Visible, chromeElements[0].Visibility);
            State(await Present(fullscreen: false), false, false);
            main.ViewModel.SelectedDocument = world; await Preview(); await Idle();
            main.Width += 37; main.Height += 19; await Idle();
            double savedWidth = main.ActualWidth, savedHeight = main.ActualHeight;
            Assert.NotEqual(main.ViewModel.Settings.Width, savedWidth);
            main.WindowState = WindowState.Maximized; await Idle();
            await Present(fullscreen: true);
            main.Close(); await Idle(); Assert.False(main.IsLoaded);
            // ActualSize and native RestoreBounds can differ by a final floating-point bit at
            // fractional DPI. Native rectangle restoration above remains an exact pixel check.
            var saved = StudioSettings.Load(); Assert.Equal(savedWidth, saved.Width, 8); Assert.Equal(savedHeight, saved.Height, 8);
            var reopened = new MainWindow { ShowInTaskbar = false };
            try { reopened.Show(); Assert.False(reopened.PresentationState.Fullscreen); }
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
            void AssertMaximizedClient()
            {
                Assert.Equal(WindowState.Maximized, main.WindowState);
                var monitor = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
                Assert.True(GetMonitorInfo(MonitorFromWindow(hwnd, 2), ref monitor));
                var expected = MainWindow.MaximizedClientArea(ToBounds(monitor.Monitor), ToBounds(monitor.Work), taskbarEdges);
                Assert.True(GetClientRect(hwnd, out var actual));
                var origin = new NativePoint(); Assert.True(ClientToScreen(hwnd, ref origin));
                Assert.Equal(expected.Left, origin.X); Assert.Equal(expected.Top, origin.Y);
                Assert.Equal(expected.Right, origin.X + actual.Right); Assert.Equal(expected.Bottom, origin.Y + actual.Bottom);
                Assert.Equal(0, GetWindowLongPtr(hwnd, -20).ToInt64() & 8);
            }
        }
        finally
        {
            if (main.IsLoaded)
            {
                main.SetPresentation(false, false);
                main.Close(); await Idle();
            }
            Directory.Delete(folder, true);
        }
        Assert.DoesNotContain(app.Windows.Cast<Window>(), w => w.Title is "Resolve property input" or "Unsaved changes");
    }

    private static MainWindow.CaptionBounds ToBounds(NativeRect rect) => new() { Left = rect.Left, Top = rect.Top, Right = rect.Right, Bottom = rect.Bottom };
    private static void CheckMaximizedClientGeometry()
    {
        // Screen pixels stay pixels at fractional DPI; negative secondary-monitor coordinates are not work-area offsets.
        foreach (var monitor in new[] { new MainWindow.CaptionBounds { Right = 3840, Bottom = 2160 }, new MainWindow.CaptionBounds { Left = -1920, Top = -200, Bottom = 880 } })
        {
            Assert.Equal(monitor, MainWindow.MaximizedClientArea(monitor, monitor, 0));
            for (int edge = 0; edge < 4; edge++)
            {
                var expected = monitor;
                switch (edge) { case 0: expected.Left += 2; break; case 1: expected.Top += 2; break; case 2: expected.Right -= 2; break; case 3: expected.Bottom -= 2; break; }
                Assert.Equal(expected, MainWindow.MaximizedClientArea(monitor, monitor, 1u << edge));
            }
            var occupied = monitor; occupied.Bottom -= 60;
            Assert.Equal(occupied, MainWindow.MaximizedClientArea(monitor, occupied, 8));
        }
    }

    private static void State(JsonNode state, bool fullscreen, bool available, bool hidden = false)
    { Assert.Equal(fullscreen, state["fullscreen"]!.GetValue<bool>()); Assert.Equal(hidden, state["uiHidden"]!.GetValue<bool>()); Assert.Equal(available, state["available"]!.GetValue<bool>()); }
    private static async Task Idle() { await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(20); }
    private static async Task CameraSettled(SceneViewport viewport)
    {
        // FrameAll initially stores +Y; the ordinary render loop orthogonalizes it in
        // PrepareCameraFrame before RenderContext captures the pose. Await that real frame,
        // with no presentation transition, forced render, resize or direct preparation call.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            while (!viewport.IsOrbitPickingReady || (bool)typeof(SceneViewport).GetField("cameraPoseDirty", Fields)!.GetValue(viewport)! ||
                (TimeSpan)typeof(SceneViewport).GetField("previousFrameTime", Fields)!.GetValue(viewport)! == TimeSpan.Zero)
                await Task.Delay(10, deadline.Token);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            var window = Window.GetWindow(viewport);
            List<string> ancestors = [];
            for (DependencyObject? item = viewport; item != null; item = System.Windows.Media.VisualTreeHelper.GetParent(item))
                if (item is FrameworkElement element) ancestors.Add($"{element.GetType().Name}#{element.Name}: {element.Visibility}/{element.IsVisible} {element.ActualWidth}x{element.ActualHeight}");
            throw new TimeoutException($"Camera did not settle: ready={viewport.IsOrbitPickingReady}, dirty={typeof(SceneViewport).GetField("cameraPoseDirty", Fields)!.GetValue(viewport)}, previousFrame={typeof(SceneViewport).GetField("previousFrameTime", Fields)!.GetValue(viewport)}, render={viewport.RenderStatus}, loaded={viewport.IsLoaded}, frontBuffer={viewport.FrontBufferAvailable}, pixels={viewport.PresentedPixelSize}, window={window?.WindowState}/{window?.IsActive}/{window?.IsVisible}, source={PresentationSource.FromVisual(viewport) != null}, error={viewport.RenderException}. Ancestors: {string.Join("; ", ancestors)}");
        }
        var settled = viewport.CaptureView(); await Idle(); Assert.Equal(settled, viewport.CaptureView());
    }
    private static void Send(UIElement target, Key key) => target.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(target), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static NativePlacement Placement(nint handle)
    { Assert.Equal(44, Marshal.SizeOf<NativePlacement>()); var result = new NativePlacement { Length = Marshal.SizeOf<NativePlacement>() }; Assert.True(GetWindowPlacement(handle, ref result)); return result; }
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePlacement { public int Length; public uint Flags, Show; public NativePoint Min, Max; public NativeRect Normal; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowPlacement(nint handle, ref NativePlacement placement);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint handle, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint handle, ref NativePoint point);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern nint SendMessage(nint handle, uint message, nint wparam, nint lparam);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint handle, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)] private static extern bool GetMonitorInfo(nint handle, ref MonitorInfo monitor);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint handle, int index);
}
