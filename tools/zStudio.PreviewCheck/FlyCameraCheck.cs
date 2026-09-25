using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HelixToolkit.Wpf.SharpDX;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;
using HCamera = HelixToolkit.Wpf.SharpDX.PerspectiveCamera;

internal static class FlyCameraCheck
{
    public static int Run(string rootArg)
    {
        string root = Path.GetFullPath(rootArg), path = Path.Combine(root, "m1", "gamez.zbd");
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(path));
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RecoilZbdStudio", "settings.json");
        byte[]? oldSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        string output = Path.Combine(Path.GetTempPath(), "zstudio-freecam-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); Directory.CreateDirectory(output);
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false };
        app.InitializeComponent();
        int exit = 0;
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow;
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            SceneViewport? scene = null;
            try
            {
                window.Width = 1500; window.Height = 900; window.Activate();
                await window.ViewModel.OpenRootAsync(root);
                var document = await window.ViewModel.OpenFileAsync(path) ?? throw new InvalidDataException("Missing map");
                await Ready();
                scene = (SceneViewport)((ContentControl)window.FindName("SceneHost")).Content;
                var viewport = (Viewport3DX)scene.Content;
                var camera = (HCamera)viewport.Camera!;
                var fly = (ToggleButton)window.FindName("FlyEnabled");
                var toolbar = (ToolBar)window.FindName("SceneToolbar");
                var session = (FlyCameraSession)typeof(MainWindow).GetField("flyCamera", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
                window.Activate(); await Delay(80);
                var pose = scene.CaptureView();
                // Entry must discard queued native forces and preserve the exact position/direction.
                viewport.AddPanForce(12, 8); viewport.AddZoomForce(.5);
                // Isolate inertia from live physical pointer movement during capture.
                scene.SetFly(true);
                await Delay(80);
                // Large scenes can defer their first rendered frame. Wait for camera
                // basis finalization before comparing a complete pose across wheel input.
                while (Math.Abs(Vector3D.DotProduct(camera.LookDirection, camera.UpDirection)) / camera.LookDirection.Length > 1e-8) await Delay(20);
                Require((camera.Position - pose.Position).Length < 1e-6 && (camera.LookDirection - pose.LookDirection).Length < 1e-6, $"Entry applied queued camera inertia: {pose} -> {scene.CaptureView()}");
                scene.SetFly(false); await Enter();
                Require(Mouse.Captured == scene && scene.IsKeyboardFocusWithin && scene.Cursor == Cursors.None, "Input was not captured/hidden/focused");
                GetClipCursor(out var clipped);
                var screen = scene.PointToScreen(new());
                Require(Math.Abs(clipped.Left - screen.X) <= 1 && Math.Abs(clipped.Top - screen.Y) <= 1, "Cursor confinement does not use the viewport's physical bounds");
                double initialSpeed = scene.FlySpeed;
                var fixedPose = scene.CaptureView();
                scene.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 120) { RoutedEvent = Mouse.PreviewMouseWheelEvent });
                Require(scene.FlySpeed == Math.Min(100000, initialSpeed * 1.25) && scene.CaptureView() == fixedPose, $"Wheel speed/pose mismatch: speed {initialSpeed} -> {scene.FlySpeed}, active {session.IsActive}; {fixedPose} -> {scene.CaptureView()}");
                camera.LookDirection = new(0, 0, -10); camera.UpDirection = new(0, 1, 0);
                await Delay(60);
                var start = camera.Position;
                KeyInput(Key.W, true); await Delay(250); KeyInput(Key.W, false); await Delay(80);
                Require(camera.Position.Z < start.Z && Math.Abs(camera.Position.X - start.X) < .01, $"W did not move forward: {start} -> {camera.Position}, look {camera.LookDirection}, active={session.IsActive}, foreground={window.IsActive}");
                var stopped = scene.CaptureView(); await Delay(180);
                Require(scene.CaptureView() == stopped, "Released W retained movement inertia");
                KeyInput(Key.W, true); KeyInput(Key.S, true); var opposed = scene.CaptureView(); await Delay(120);
                Require(scene.CaptureView() == opposed, "Opposing keys did not cancel"); KeyInput(Key.W, false); KeyInput(Key.S, false);
                start = camera.Position; KeyInput(Key.Space, true); await Delay(150); KeyInput(Key.Space, false);
                Require(camera.Position.Y > start.Y && Math.Abs(camera.Position.Z - start.Z) < .01, "Space did not move vertically");
                start = camera.Position; KeyInput(Key.C, true); await Delay(150); KeyInput(Key.C, false); Require(camera.Position.Y < start.Y, "C did not move down");
                start = camera.Position; KeyInput(Key.D, true); await Delay(150); KeyInput(Key.D, false); Require(camera.Position.X > start.X, "D did not strafe right");
                start = camera.Position; KeyInput(Key.A, true); await Delay(150); KeyInput(Key.A, false); Require(camera.Position.X < start.X, "A did not strafe left");

                // Exercise native pointer input. Injected moves can use the fallback when
                // Windows does not synthesize WM_INPUT for SendInput.
                Require(window.IsActive, "Native mouse check requires the owned test window in the foreground");
                var lookBefore = camera.LookDirection;
                Input input = new() { Mouse = new() { X = 90, Y = -45, Flags = 1 } };
                Require(SendInput(1, [input], Marshal.SizeOf<Input>()) == 1, "Native mouse injection failed");
                await Delay(150);
                Require(camera.LookDirection.X > lookBefore.X && camera.LookDirection.Y > lookBefore.Y, $"Native mouse-look did not turn right/up: {lookBefore} -> {camera.LookDirection}; active {session.IsActive}");
                var native = (FlyMouseCapture)typeof(FlyCameraSession).GetField("mouse", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
                var rawExpected = FlyCameraMotion.Look(camera.LookDirection, 30, -10);
                FlyMouseCapture.GetCursorPos(out var rawPointer);
                FlyMouseCapture.SetCursorPos(rawPointer.X + 15, rawPointer.Y + 5);
                native.QueueFallbackLook(); native.ProcessMouse(0, 30, -10); await Delay(80);
                Require((camera.LookDirection - rawExpected).Length < 1e-8, "Relative packet and fallback applied mouse-look twice");
                lookBefore = camera.LookDirection;
                Require(SendInput(1, [input], Marshal.SizeOf<Input>()) == 1, "Mixed pointer injection failed");
                await Delay(150);
                Require(camera.LookDirection.X > lookBefore.X && camera.LookDirection.Y > lookBefore.Y, "Fallback stopped after receiving a relative packet");
                var selected = scene.SelectedPickupRoot;
                scene.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
                Require(!scene.IsPickupDragging && scene.SelectedPickupRoot == selected && !document.IsDirty, "Freecam click edited or selected a pickup");
                scene.RestoreView(pose); await Delay(150);
                Save(scene.RenderImage(1100, 650), "world-freecam.png");
                var dpi = VisualTreeHelper.GetDpi(window);
                var workspace = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                workspace.Render(window); Save(workspace, "world-freecam-window.png");
                using var operation = new CancellationTokenSource();
                var operationField = typeof(MainWindow).GetField("operation", BindingFlags.Instance | BindingFlags.NonPublic)!;
                operationField.SetValue(window, operation);
                Escape(); operationField.SetValue(window, null);
                Require(!operation.IsCancellationRequested, "Freecam Escape also canceled an unrelated operation");
                await Delay(80); Require(!session.IsActive && !scene.IsFlyActive && fly.IsChecked == false && Mouse.Captured != scene && scene.Cursor != Cursors.None, "Escape did not release all input state");
                GetClipCursor(out var released); Require(released.Right - released.Left > clipped.Right - clipped.Left, "Escape retained cursor confinement");
                Require(scene.FlySpeed == Math.Min(100000, initialSpeed * 1.25), "Exit lost selected speed");
                var exitPose = scene.CaptureView(); await Delay(150); Require(scene.CaptureView() == exitPose, "Exit restored old camera inertia");
                Require(viewport.IsPanEnabled && viewport.IsZoomEnabled && viewport.IsRotationEnabled, "Orbit controls were not restored");

                await Enter(); KeyInput(Key.W, true);
                var other = new Window { Owner = window, Width = 200, Height = 100, Content = new TextBox(), Title = "Freecam focus regression" };
                other.Show(); other.Activate(); await Delay(100);
                Require(!session.IsActive && Mouse.Captured != scene && fly.IsChecked == false, "Deactivation retained captured controls");
                other.Close(); window.Activate(); await Delay(80); await Enter();
                stopped = scene.CaptureView(); await Delay(150); Require(scene.CaptureView() == stopped, "Re-entry retained a held movement key");
                Mouse.Capture(null); await Delay(50); Require(!session.IsActive && !scene.IsFlyActive, "Capture loss did not exit");
                await Enter(); ((ContentControl)window.FindName("SceneHost")).IsEnabled = false; await Delay(50);
                Require(!session.IsActive, "Disabled viewer retained capture"); ((ContentControl)window.FindName("SceneHost")).IsEnabled = true;

                ToolBar.SetOverflowMode(fly, OverflowMode.Always); toolbar.IsOverflowOpen = true; await Delay(80);
                Require(ToolBar.GetIsOverflowItem(fly), "Overflow case did not use overflow");
                await Enter(); Require(!toolbar.IsOverflowOpen && session.IsActive, "Overflow activation did not transfer capture"); Escape();
                ToolBar.SetOverflowMode(fly, OverflowMode.AsNeeded);
                await Enter(); window.Width = 1200; await Delay(100); GetClipCursor(out var resized);
                var bottom = scene.PointToScreen(new(scene.ActualWidth, scene.ActualHeight));
                Require(Math.Abs(resized.Right - bottom.X) <= 1, "Resizing left stale cursor bounds"); Escape();

                // Another asset must never inherit a captured session.
                var model = document.Assets.First(a => a.Record.Kind == AssetKind.Model);
                await Enter(); document.SelectedAsset = model; await Ready();
                Require(!session.IsActive && fly.IsChecked == false && Mouse.Captured != scene, "Asset replacement retained freecam");
                await Enter(); KeyInput(Key.W, true); await Delay(100); KeyInput(Key.W, false); Save(scene.RenderImage(900, 600), "model-freecam.png"); Escape();
                await Enter(); window.Close(); Require(!session.IsActive && Mouse.Captured != scene, "Closing retained input");
                Require(sourceHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Source map changed");
                Console.WriteLine("PASS: model/world native capture and injected mouse-look, all movement keys, opposing/released keys, wheel speed-only, no picking/editing, Escape/Alt-window/capture-loss/disabled/replace/close cleanup, overflow transfer, resized physical clipping, re-entry without stuck input and unchanged source. " + output);

                async Task Enter()
                {
                    window.Activate(); fly.IsChecked = true; await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Delay(80);
                    Require(session.IsActive && scene.IsFlyActive, "Fly button failed to enter: " + window.ViewModel.Status);
                }
                void KeyInput(Key key, bool down) => scene.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(scene), Environment.TickCount, key) { RoutedEvent = down ? Keyboard.PreviewKeyDownEvent : Keyboard.PreviewKeyUpEvent });
                void Escape() => KeyInput(System.Windows.Input.Key.Escape, true);
                async Task Ready()
                {
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    var empty = (TextBlock)window.FindName("EmptyPreview");
                    while (empty.Visibility == Visibility.Visible)
                    {
                        if (empty.Text.StartsWith("Preview unavailable")) throw new InvalidDataException(empty.Text);
                        await Delay(50);
                    }
                }
                Task Delay(int ms) => Task.Delay(ms, timeout.Token);
                void Save(BitmapSource bitmap, string name) { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(Path.Combine(output, name)); encoder.Save(stream); }
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { scene?.SetFly(false); window.Close(); app.Shutdown(); }
        };
        try { app.Run(); }
        finally { if (oldSettings != null) File.WriteAllBytes(settings, oldSettings); else File.Delete(settings); }
        return exit;
    }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
    [StructLayout(LayoutKind.Sequential)] private struct RectI { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint Data, Flags, Time; public nint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public MouseInput Mouse; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetClipCursor(out RectI rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] input, int size);
}
