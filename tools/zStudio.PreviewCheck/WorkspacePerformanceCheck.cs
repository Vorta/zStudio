using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Recoil.Zbd.Desktop;

internal static class WorkspacePerformanceCheck
{
    // Uses the pre-redesign public surface so this exact runner can also load the
    // preserved portable baseline assemblies for a comparable local measurement.
    public static int Run(string root)
    {
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RecoilZbdStudio","settings.json");
        byte[]? original = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        App app = new() { ProcessCommandLine = false, ShutdownMode = ShutdownMode.OnExplicitShutdown }; app.InitializeComponent(); int exit = 0;
        app.Startup += async (_,_) =>
        {
            var window = (MainWindow)app.MainWindow; window.Width = 1600; window.Height = 900; window.Left = -12000;
            try
            {
                await window.ViewModel.OpenRootAsync(Path.GetFullPath(root));
                var doc = await window.ViewModel.OpenFileAsync(Path.Combine(Path.GetFullPath(root),"m1","anim.zbd")) ?? throw new InvalidDataException("Animation missing");
                var asset = doc.Assets.First(a => a.Name == "vtol_destruction1"); doc.SelectedAsset = asset;
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                AnimationEditor editor;
                while (true)
                {
                    if (((ContentControl)window.FindName("AnimationHost")).Content is AnimationEditor current && current.EntryIndex == asset.Index && current.CurrentFrame != null && ((Border)current.FindName("LoadingPanel")).Visibility == Visibility.Collapsed) { editor = current; break; }
                    await Task.Delay(50,timeout.Token);
                }
                ((Slider)editor.FindName("Volume")).Value = 0;
                await editor.SeekAsync(0); await Task.Delay(600,timeout.Token);
                var process = Process.GetCurrentProcess(); TimeSpan cpu = process.TotalProcessorTime; long allocated = GC.GetTotalAllocatedBytes();
                await Task.Delay(2000,timeout.Token); process.Refresh();
                var idle = new { CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated };
                List<double> intervals = []; Stopwatch watch = Stopwatch.StartNew(); double previous = 0,lastFrame = -1;
                DispatcherTimer sample = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(8) };
                sample.Tick += (_,_) => { if (editor.CurrentFrame!.Time == lastFrame) return; double now = watch.Elapsed.TotalMilliseconds; if (lastFrame >= 0) intervals.Add(now - previous); previous = now; lastFrame = editor.CurrentFrame.Time; };
                cpu = process.TotalProcessorTime; allocated = GC.GetTotalAllocatedBytes(); sample.Start(); editor.TogglePlayback();
                await Task.Delay(4000,timeout.Token); editor.Pause(); sample.Stop(); process.Refresh(); intervals.Sort();
                var playback = new { CpuMilliseconds = (process.TotalProcessorTime - cpu).TotalMilliseconds, AllocatedBytes = GC.GetTotalAllocatedBytes() - allocated, Samples = intervals.Count, MedianFrameIntervalMs = intervals.Count == 0 ? 0 : intervals[intervals.Count / 2], P95FrameIntervalMs = intervals.Count == 0 ? 0 : intervals[(int)(intervals.Count * .95)], MaxFrameIntervalMs = intervals.LastOrDefault(), AudioOutputInitializations = editor.Audio.OutputInitializations };
                Console.WriteLine(JsonSerializer.Serialize(new { StudioAssembly = typeof(AnimationEditor).Assembly.Location, Window = new { window.ActualWidth,window.ActualHeight }, Viewport = new { editor.Viewport.ActualWidth,editor.Viewport.ActualHeight }, Idle = idle, Playback = playback },new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { window.Close(); app.Shutdown(exit); }
        };
        try { app.Run(); } finally { if (original != null) File.WriteAllBytes(settings,original); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
}
