using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using NAudio.Wave;
using NAudio.CoreAudioApi;

internal static class AnimationCheck
{
    public static int Run(string root)
    {
        root = Path.GetFullPath(root); int exit = 0;
        string output = Path.GetFullPath(Path.Combine("artifacts", "animation-preview",Path.GetFileName(root).Replace("zbd_",""))); Directory.CreateDirectory(output);
        string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"RecoilZbdStudio","settings.json");
        byte[]? originalSettings = File.Exists(settings) ? File.ReadAllBytes(settings) : null;
        var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown, ProcessCommandLine = false }; app.InitializeComponent();
        app.Startup += async (_, _) =>
        {
            var window = (MainWindow)app.MainWindow; window.Width = 1700; window.Height = 1000;
            ((ColumnDefinition)window.FindName("PropertiesColumn")).Width = new(320);
            ((TabControl)window.FindName("InspectorTabs")).Visibility = Visibility.Visible;
            try
            {
                await window.ViewModel.OpenRootAsync(root);
                string path = Path.Combine(root,"m1","anim.zbd"); byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(path));
                var document = await window.ViewModel.OpenFileAsync(path) ?? throw new InvalidDataException("Animation did not open.");
                List<object> report = [];
                foreach (string name in new[] { "fire_bft", "lock", "pu000", "bftmbrst.flt", "chutes", "redsprks.flt", "vtol_destruction1" })
                {
                    var item = document.Assets.FirstOrDefault(a => a.Name == name); if (item == null) continue;
                    document.SelectedAsset = item;
                    if (name == "pu000")
                    {
                        var assets = (DataGrid)window.FindName("AssetGrid"); assets.Focus();
                        if (!Space(window, assets)) throw new InvalidDataException("Space in Assets was not accepted while loading.");
                    }
                    var editor = await WaitEditor(window, item.Index);
                    if (name == "pu000")
                    {
                        if (!editor.IsPlaying) throw new InvalidDataException("Queued Space did not play the selected animation.");
                        editor.Pause(); await CheckTransport(window, editor, output);
                    }
                    Console.WriteLine($"{name}: {editor.Duration?.Kind}, {editor.Duration?.Frames} frames, {editor.Duration?.Seconds:0.###} seconds");
                    if (name == "vtol_destruction1")
                    {
                        await CheckSoundOutput(editor);
                        var context = await document.GetAnimationContextAsync(window.ViewModel.Resolver!, CancellationToken.None);
                        await CheckLoopOutput(context, item.Index, editor.CurrentFrame!);
                    }
                    if (name == "fire_bft")
                    {
                        var context = await document.GetAnimationContextAsync(window.ViewModel.Resolver!, CancellationToken.None);
                        var cycles = editor.CurrentFrame!.Nodes.SelectMany(p => context.Scene.Models[p.Model].Polygons).Select(p => p.MaterialIndex).Distinct().Where(context.MaterialCycles.ContainsKey).Select(m => context.MaterialCycles[m]).ToArray();
                        if (!cycles.Any(c => c.Textures.Count == 12 && c.Speed == 10 && c.Loop)) throw new InvalidDataException("Fire's runtime material cycle was not bound.");
                        await editor.SeekAsync(.2); editor.Viewport.FrameAnimation(editor.CurrentFrame!); await Task.Delay(200);
                        var first = Pixels(editor.Viewport.RenderImage(480, 320));
                        await editor.SeekAsync(.5); await Task.Delay(200);
                        var second = Pixels(editor.Viewport.RenderImage(480, 320));
                        await editor.SeekAsync(.2); await Task.Delay(200);
                        var replay = Pixels(editor.Viewport.RenderImage(480, 320));
                        int changed = first.Zip(second).Count(p => p.First != p.Second);
                        if (changed < 1000 || !first.AsSpan().SequenceEqual(replay)) throw new InvalidDataException($"Fire cycle did not animate/replay deterministically ({changed} changed channels).");
                        Console.WriteLine($"Fire: 12 frames at 10 fps; {changed} rendered channels changed, exact seek replay.");
                    }
                    if (name == "lock")
                    {
                        var lod = (ComboBox)editor.FindName("Lod");
                        await editor.SeekAsync(.2); var before = editor.CurrentFrame!;
                        lod.SelectedIndex = 1; var lower = editor.CurrentFrame!;
                        if (before.Time != lower.Time || before.Nodes.Where(n => n.Visible).SequenceEqual(lower.Nodes.Where(n => n.Visible))) throw new InvalidDataException("Animation LOD picker did not select a different variant at the same time.");
                        lod.SelectedIndex = 0;
                        if (!before.Nodes.SequenceEqual(editor.CurrentFrame!.Nodes)) throw new InvalidDataException("Restoring highest LOD changed animation state.");
                        var level = (CheckBox)editor.FindName("ShowLevel"); level.IsChecked = true; await WaitEditor(window, item.Index);
                        lod.SelectedIndex = 1; await WaitEditor(window, item.Index);
                        if (!lower.Nodes.SequenceEqual(editor.CurrentFrame!.Nodes)) throw new InvalidDataException("Mission context changed the animation's selected LOD pose.");
                        level.IsChecked = false; await WaitEditor(window, item.Index); lod.SelectedIndex = 0;
                        Console.WriteLine("Animation LOD picker switches variants without restarting playback or changing pose state.");
                    }
                    await editor.SeekAsync(.2); await Task.Delay(350); editor.Viewport.FrameAnimation(editor.CurrentFrame!);
                    var earlyFrame = editor.CurrentFrame!;
                    if (editor.Viewport.AnimationMeshCount == 0) throw new InvalidDataException(name + ": no animation meshes");
                    if (editor.CurrentFrame!.Nodes.All(n => !n.Visible || n.Opacity <= 0)) throw new InvalidDataException(name + ": all preview geometry is invisible");
                    Save(editor.Viewport.RenderImage(960,640),Path.Combine(output,name + "_02.png"));
                    await editor.SeekAsync(1.2); await Task.Delay(250);
                    Save(editor.Viewport.RenderImage(960,640),Path.Combine(output,name + "_12.png"));
                    var frame = editor.CurrentFrame!;
                    if (name == "redsprks.flt")
                    {
                        var motionContext = await document.GetAnimationContextAsync(window.ViewModel.Resolver!, CancellationToken.None);
                        var motionEntry = document.AnimationEdits!.Package.Entries[item.Index];
                        var gravityNodes = motionEntry.Sequences.SelectMany(s => s.Events)
                            .Where(e => e.Type == 10 && (e.U32(12) & 1) != 0 && e.F32(24) < 0)
                            .Select(e => motionContext.ResolveNode(motionEntry, e.I32(16))).ToHashSet();
                        var falling = frame.Nodes.Where(n => gravityNodes.Contains(n.SourceNode)).ToArray();
                        if (falling.Length != 4 || falling.Any(n => n.Transform.M42 >= earlyFrame.Nodes.Single(p => p.Id == n.Id).Transform.M42))
                            throw new InvalidDataException("Launched sparks did not turn downward under gravity.");
                        Console.WriteLine("All four red sparks turn downward after their initial launch.");
                    }
                    if (name == "bftmbrst.flt")
                    {
                        var smoke = editor.Viewport.RenderImage(960,640);
                        var rgba = new FormatConvertedBitmap(smoke,PixelFormats.Bgra32,null,0);
                        byte[] pixels = new byte[rgba.PixelWidth * rgba.PixelHeight * 4]; rgba.CopyPixels(pixels,rgba.PixelWidth * 4,0);
                        int visiblePixels = 0;
                        for (int y = 0; y < rgba.PixelHeight * 3 / 4; y++) for (int x = 0; x < rgba.PixelWidth; x++)
                        { int at = (y * rgba.PixelWidth + x) * 4; if (pixels[at] + pixels[at+1] + pixels[at+2] > 210) visiblePixels++; }
                        if (visiblePixels < 100) throw new InvalidDataException("Transparent smoke geometry disappeared from the rendered image.");
                        Console.WriteLine($"Transparent smoke rendered {visiblePixels} visible pixels.");
                    }
                    report.Add(new { name, nodes = frame.Nodes.Count, visible = frame.Nodes.Count(n => n.Visible && n.Opacity > 0), meshes = editor.Viewport.AnimationMeshCount, trace = frame.Trace.Count, frame.Diagnostics, poses = frame.Nodes.Select(n => new { n.SourceNode, n.Visible, n.Opacity, n.Model, position = n.Transform.Translation.ToString(), matrix = n.Transform.ToString() }) });
                    if (name == "pu000")
                    {
                        await editor.SeekAsync(0);
                        var speed = (ComboBox)editor.FindName("Speed"); speed.SelectedIndex = 0;
                        ((Button)editor.FindName("PlayButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        double before = editor.CurrentFrame!.Time; await Task.Delay(600); editor.Pause();
                        if (editor.CurrentFrame!.Time <= before) throw new InvalidDataException("Quarter-speed playback stalled.");
                        var entry = document.AnimationEdits!.Package.Entries[item.Index]; var sequence = entry.Sequences.First(s => s.Events.Count > 0); var ev = sequence.Events[0]; float threshold = ev.Threshold;
                        document.AnimationEdits.Apply(item.Index,"UI integration edit",e => AnimationEditSession.FindSequence(e,sequence.Id).Events[0].Threshold = threshold + .125f);
                        await Task.Delay(250);
                        if (!document.Title.EndsWith(" *",StringComparison.Ordinal)) throw new InvalidDataException("Dirty indicator missing.");
                        editor.Undo(); if (document.AnimationEdits.IsDirty) throw new InvalidDataException("Undo did not restore clean state.");
                        editor.Redo(); if (!document.AnimationEdits.IsDirty) throw new InvalidDataException("Redo did not restore the edit.");
                        string copy = Path.Combine(output,"edited-" + Guid.NewGuid().ToString("N") + ".zbd");
                        await AnimationWriter.SaveAsAsync(document.AnimationEdits.Package,copy,path,root);
                        var reopened = AnimationPackage.Read(File.ReadAllBytes(copy));
                        if (reopened.Entries[item.Index].Sequences[entry.Sequences.IndexOf(sequence)].Events[0].Threshold != threshold + .125f) throw new InvalidDataException("Saved edit did not reopen.");
                        document.AnimationEdits.MarkSaved();
                        await Task.Delay(200); window.UpdateLayout();
                        RenderTargetBitmap shot = new((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32); shot.Render(window); Save(shot,Path.Combine(output,"editor.png"));
                    }
                    if (name == "chutes")
                    {
                        var context = await document.GetAnimationContextAsync(window.ViewModel.Resolver!,CancellationToken.None);
                        await editor.Viewport.ShowAnimationAsync(context,editor.CurrentFrame!,window.ViewModel.Resolver!,true,CancellationToken.None);
                        await Task.Delay(300); Save(editor.Viewport.RenderImage(960,640),Path.Combine(output,"mission-context.png"));
                        var reset = new AnimationPlayer(context,item.Index,resetPhase:true).AdvanceTo(.5,true);
                        if (reset.Trace.Any(t => t.Sequence != document.AnimationEdits!.Package.Entries[item.Index].Primary.Id)) throw new InvalidDataException("Runtime events leaked into reset phase.");
                        if (context.Sounds.Count == 0 || context.Effects.Count == 0) throw new InvalidDataException("No effects/sound templates were resolved.");
                        await using AnimationAudio audio = new() { Muted = false, Volume = 0 };
                        var sample = context.Sounds.Values.First(); var cue = new AnimationSoundCue(777,sample.Name,false,false,0,System.Numerics.Vector3.Zero);
                        var audioContext = context.Snapshot(); var soundEvent = AnimationCatalog.Create(2);
                        soundEvent.SetText(12, sample.Name); soundEvent.SetInt(52, 1);
                        audioContext.Package.Entries[item.Index].Primary.Events.Add(soundEvent);
                        await audio.PrepareAsync(audioContext, item.Index);
                        audio.Update(reset with { Time = .01, Sounds = [cue], ActiveSounds = [] },context,true);
                        if (audio.VoiceCount != 1) throw new InvalidDataException("Animation WASAPI voice did not start.");
                        audio.Stop(); if (audio.VoiceCount != 0) throw new InvalidDataException("Animation voice did not stop.");
                        Console.WriteLine($"Resolved {context.Sounds.Count} sounds ({context.Sounds.Values.Count(s => s.Loop)} looped) and {context.Effects.Count} effects; mission/reset/audio checks passed.");
                    }
                }
                if (report.Count == 0) throw new InvalidDataException("No representative animations found.");
                // A queued key belongs to exactly one selection and must not start its successor.
                document.SelectedAsset = document.Assets.First(a => a.Name == "pu000");
                Space(window, (DataGrid)window.FindName("AssetGrid"));
                Space(window, (DataGrid)window.FindName("AssetGrid"));
                if ((await WaitEditor(window, document.SelectedAsset.Index)).IsPlaying) throw new InvalidDataException("A second Space did not cancel queued playback.");
                document.SelectedAsset = document.Assets.First(a => a.Name == "redsprks.flt");
                Space(window, (DataGrid)window.FindName("AssetGrid"));
                var replacement = document.Assets.First(a => a.Name == "chutes"); document.SelectedAsset = replacement;
                if ((await WaitEditor(window, replacement.Index)).IsPlaying) throw new InvalidDataException("Queued playback leaked to the next selection.");
                if (!sourceHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(path)))) throw new InvalidDataException("Source file changed.");
                var firstEntry = document.AnimationEdits!.Package.Entries.First(e => e.Sequences.Any(s => s.Events.Count > 0));
                document.AnimationEdits.Apply(firstEntry.Index,"Close guard check",e => e.Sequences.First(s => s.Events.Count > 0).Events[0].Threshold += .1f);
                window.ViewModel.ConfirmDiscardAsync = _ => Task.FromResult(false);
                await window.ViewModel.CloseAsync(document); if (!window.ViewModel.Documents.Contains(document)) throw new InvalidDataException("Canceled close lost edits.");
                await window.ViewModel.ReloadAsync(); if (!window.ViewModel.Documents.Contains(document)) throw new InvalidDataException("Canceled reload lost edits.");
                await window.ViewModel.OpenRootAsync(root); if (!window.ViewModel.Documents.Contains(document)) throw new InvalidDataException("Canceled root switch lost edits.");
                window.ViewModel.ConfirmDiscardAsync = _ => Task.FromResult(true); await window.ViewModel.CloseAsync(document);
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                if (((ContentControl)window.FindName("AnimationHost")).Content != null) throw new InvalidDataException("Closing retained the animation editor.");
                File.WriteAllText(Path.Combine(output,"report.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions { WriteIndented = true }));
                Console.WriteLine("Animation UI, quarter-speed playback, seeking, edit/undo/redo, verified Save As and cleanup passed.");
            }
            catch (Exception ex) { Console.Error.WriteLine(ex); exit = 1; }
            finally { foreach (var d in window.ViewModel.Documents) d.AnimationEdits?.MarkSaved(); window.Close(); app.Shutdown(); }
        };
        try { app.Run(); }
        finally { if (originalSettings != null) File.WriteAllBytes(settings,originalSettings); else if (File.Exists(settings)) File.Delete(settings); }
        return exit;
    }
    private static async Task<AnimationEditor> WaitEditor(MainWindow window, int index)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
        while (true)
        {
            timeout.Token.ThrowIfCancellationRequested();
            if (((ContentControl)window.FindName("AnimationHost")).Content is AnimationEditor editor && editor.EntryIndex == index && editor.CurrentFrame != null && ((Border)editor.FindName("LoadingPanel")).Visibility == Visibility.Collapsed) return editor;
            await Task.Delay(100,timeout.Token);
        }
    }
    private static byte[] Pixels(BitmapSource image)
    {
        var bitmap = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        byte[] bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0); return bytes;
    }
    private static void Save(BitmapSource image, string path) { PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); encoder.Save(stream); }
    private static bool Space(MainWindow window, UIElement source)
    {
        var key = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Space) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        source.RaiseEvent(key); return key.Handled;
    }
    private static async Task CheckTransport(MainWindow window, AnimationEditor editor, string output)
    {
        var assets = (DataGrid)window.FindName("AssetGrid");
        var selected = assets.SelectedItem; assets.ScrollIntoView(selected); window.UpdateLayout();
        var row = (DataGridRow)assets.ItemContainerGenerator.ContainerFromItem(selected); row.Focus();
        var focus = Keyboard.FocusedElement;
        if (!Space(window, row) || !editor.IsPlaying || Keyboard.FocusedElement != focus || assets.SelectedItem != selected)
            throw new InvalidDataException("Space did not play from the focused Assets row without changing focus/selection.");
        if (!Space(window, row) || editor.IsPlaying) throw new InvalidDataException("Second Space did not pause.");
        string status = window.ViewModel.Status;
        var escape = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        row.RaiseEvent(escape);
        var cancel = (MenuItem)window.FindName("CancelOperationItem");
        if (escape.Handled || cancel.IsEnabled || window.ViewModel.Status != status) throw new InvalidDataException("Idle cancellation is still exposed or changes the status.");
        cancel.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await editor.SeekAsync(.1);
        if (Math.Abs(editor.CurrentFrame!.Time - .1) > 1e-8) throw new InvalidDataException("Idle cancellation invalidated animation seeking.");
        foreach (string name in new[] { "Seed", "Condition", "Mute", "Speed", "SeekSlider", "PlayButton" })
            if (Space(window, (UIElement)editor.FindName(name)) || editor.IsPlaying) throw new InvalidDataException(name + " lost its native Space behavior.");
        await editor.SeekAsync(0);
        ((Button)editor.FindName("NextFrameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150);
        if (Math.Abs(editor.CurrentFrame!.Time - AnimationPlayer.StepSeconds) > 1e-8) throw new InvalidDataException("Next frame did not step exactly once.");
        ((Button)editor.FindName("PreviousFrameButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150);
        if (editor.CurrentFrame!.Time != 0) throw new InvalidDataException("Previous frame did not return to frame zero.");
        var slider = (Slider)editor.FindName("SeekSlider");
        if (editor.Duration == null || slider.Maximum != editor.Duration.Seconds) throw new InvalidDataException("Automatic range does not match calculated duration.");
        var end = (TextBox)editor.FindName("EndTime"); end.Text = "0.1"; end.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent)); await Task.Delay(150);
        if (Math.Abs(slider.Maximum - .1) > 1e-8) throw new InvalidDataException("Custom range did not apply.");
        ((Button)editor.FindName("AutoRangeButton")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(150);
        if (slider.Maximum != editor.Duration.Seconds) throw new InvalidDataException("Auto did not restore the measured range.");
        await editor.SeekAsync(3600);
        if (Math.Abs(editor.CurrentFrame!.Time - slider.Maximum) > 1e-8) throw new InvalidDataException("Seek exceeded the displayed range.");
        editor.TogglePlayback(); if (!editor.IsPlaying || editor.CurrentFrame!.Time != 0) throw new InvalidDataException("Play at the endpoint did not restart.");
        editor.Pause();
        var transport = (Grid)editor.FindName("TransportRow");
        foreach (bool narrow in new[] { false, true })
        {
            editor.Width = narrow ? 250 : double.NaN; window.UpdateLayout();
            var bounds = new[] { "PlayButton", "StopButton", "PreviousFrameButton", "NextFrameButton", "SeekSlider" }
                .Select(name => (FrameworkElement)editor.FindName(name)).Select(c => new Rect(c.TranslatePoint(new(), transport), c.RenderSize)).ToArray();
            for (int i = 0; i < bounds.Length; i++)
            {
                if (bounds[i].Left < 0 || bounds[i].Right > transport.ActualWidth + .1 || Math.Abs(bounds[i].Top + bounds[i].Height / 2 - 17) > 1)
                    throw new InvalidDataException("Transport is clipped or not on one row: " + bounds[i]);
                if (i > 0 && bounds[i - 1].Right > bounds[i].Left) throw new InvalidDataException("Transport controls overlap.");
            }
            if (slider.ActualWidth < 80) throw new InvalidDataException("Seeker is too small at minimum center width.");
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle); await Task.Delay(250);
            var dpi = VisualTreeHelper.GetDpi(window);
            RenderTargetBitmap shot = new((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
            shot.Render(window); Save(shot, Path.Combine(output, narrow ? "player-narrow.png" : "player-normal.png"));
        }
        editor.Width = double.NaN; window.UpdateLayout();
        // Theme switching is experimental in WPF 10; inspect both supported visual styles.
#pragma warning disable WPF0001
        var theme = Application.Current.ThemeMode;
        Application.Current.ThemeMode = ThemeMode.Light;
        await Task.Delay(400); window.UpdateLayout();
        var lightDpi = VisualTreeHelper.GetDpi(window);
        RenderTargetBitmap light = new((int)Math.Ceiling(window.ActualWidth * lightDpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * lightDpi.DpiScaleY), lightDpi.PixelsPerInchX, lightDpi.PixelsPerInchY, PixelFormats.Pbgra32);
        light.Render(window); Save(light, Path.Combine(output, "player-light.png"));
        Application.Current.ThemeMode = theme;
#pragma warning restore WPF0001
        await Task.Delay(250);
        Console.WriteLine("Space from focused Assets, harmless idle Escape/cancel, input exceptions, frame steps, range clamp, restart and 250px transport layout passed.");
    }
    private static async Task CheckSoundOutput(AnimationEditor editor)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10,0,19041)) throw new PlatformNotSupportedException("Process audio check requires Windows 10 2004 or later.");
        // Capture only this runner's output, never the microphone or another application.
        using var capture = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)Environment.ProcessId,ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000,2)).BuildAsync();
        float peak = 0; long samples = 0;
        capture.DataAvailable += (buffer,flags,_,_) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) == 0)
                for (int at = 0; at + 4 <= buffer.Length; at += 4)
                    peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer.Slice(at,4))));
            Interlocked.Add(ref samples,buffer.Length / 4);
        };
        capture.StartRecording();
        var mute = (CheckBox)editor.FindName("Mute");
        if (mute.IsChecked != false || ((Slider)editor.FindName("Volume")).Value != .5)
            throw new InvalidDataException("Animation audio must default to unmuted at 50%.");
        if (!editor.Audio.IsPrepared || editor.Audio.PreparedSoundCount == 0 || editor.Audio.OutputInitializations != 1)
            throw new InvalidDataException("Sounds and one warm output must be ready before first Play.");
        List<double> updateTimes = [], frameGaps = [];
        double previous = -1;
        void Timing(double clock, double milliseconds)
        {
            updateTimes.Add(milliseconds);
            if (previous >= 0 && clock > previous) frameGaps.Add((clock - previous) * 1000);
            previous = clock;
        }
        editor.AudioUpdated += Timing;
        ((Slider)editor.FindName("Volume")).Value = .15;
        var play = (Button)editor.FindName("PlayButton");
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(450); editor.Pause(); await Task.Delay(150);
        CheckPeak("default playback");
        mute.IsChecked = true;
        await editor.SeekAsync(0);
        Interlocked.Exchange(ref peak, 0);
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        // All four sample events occur by 0.5 s. Unmuting must resume a sound that
        // is still in progress, even though its original dispatch was muted.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (editor.CurrentFrame!.Time < .7) await Task.Delay(30, timeout.Token);
        if (peak > .0001f) throw new InvalidDataException("Muted animation produced sound.");
        mute.IsChecked = false;
        await Task.Delay(450); editor.Pause(); await Task.Delay(150);
        CheckPeak("unmute after sample events");
        await editor.SeekAsync(.2); await Task.Delay(150); Interlocked.Exchange(ref peak, 0);
        await Task.Delay(150);
        if (peak > .0001f) throw new InvalidDataException("Scrubbing played sound while paused.");
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(350); editor.Pause(); await Task.Delay(150);
        CheckPeak("play after seeking");
        Interlocked.Exchange(ref peak, 0);
        play.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(350); editor.Pause(); await Task.Delay(150);
        CheckPeak("pause/resume");
        editor.AudioUpdated -= Timing;
        if (editor.Audio.OutputInitializations != 1) throw new InvalidDataException("Sound playback recreated its audio device.");
        Console.WriteLine($"Audio timing: {editor.Audio.PreparedSoundCount} prepared samples, {editor.Audio.OutputInitializations} output initialization; first update {updateTimes[0]:0.000} ms, max {updateTimes.Max():0.000} ms; frame interval p95 {frameGaps.Order().ElementAt((int)((frameGaps.Count - 1) * .95)):0.00} ms, max {frameGaps.Max():0.00} ms.");
        capture.StopRecording();

        void CheckPeak(string phase)
        {
            Console.WriteLine($"Animation audio {phase}: {samples} samples, peak {peak:0.000000}.");
            if (samples == 0 || peak < .001f) throw new InvalidDataException($"Animation audio {phase} produced no measurable output.");
        }
    }

    private static async Task CheckLoopOutput(AnimationPreviewContext context, int entryIndex, AnimationFrame frame)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) throw new PlatformNotSupportedException("Process audio check requires Windows 10 2004 or later.");
        // Use an actual mission loop resource in a frozen program, without changing the editable document.
        var sound = context.Sounds.Values.Where(s => s.Loop && s.Duration > .01).OrderBy(s => s.Duration).First();
        var snapshot = context.Snapshot(); var ev = AnimationCatalog.Create(2);
        ev.SetText(12, sound.Name); ev.SetInt(52, 1); snapshot.Package.Entries[entryIndex].Primary.Events.Add(ev);
        await using AnimationAudio audio = new() { Volume = .1f };
        await audio.PrepareAsync(snapshot, entryIndex);
        if (!audio.IsPrepared) throw new InvalidDataException("Looped sound output did not prepare.");
        using var capture = await new WasapiRecorderBuilder()
            .WithProcessLoopback((uint)Environment.ProcessId, ProcessLoopbackMode.IncludeTargetProcessTree)
            .WithFormat(WaveFormat.CreateIeeeFloatWaveFormat(48000, 2)).BuildAsync();
        float peak = 0;
        capture.DataAvailable += (buffer, flags, _, _) =>
        {
            if ((flags & AudioClientBufferFlags.Silent) == 0)
                for (int at = 0; at + 4 <= buffer.Length; at += 4) peak = Math.Max(peak, Math.Abs(BitConverter.ToSingle(buffer.Slice(at, 4))));
        };
        capture.StartRecording();
        var cue = new AnimationSoundCue(9901, sound.Name, false, true, 0, System.Numerics.Vector3.Zero);
        audio.Update(frame with { Time = 0, Sounds = [cue], ActiveSounds = [cue] }, snapshot, true);
        await Task.Delay(TimeSpan.FromSeconds(sound.Duration + .15));
        Interlocked.Exchange(ref peak, 0);
        await Task.Delay(TimeSpan.FromSeconds(Math.Max(.2, sound.Duration)));
        if (peak < .001f || audio.OutputInitializations != 1) throw new InvalidDataException("Prepared sound did not continue beyond its loop boundary.");
        audio.Stop(); await Task.Delay(150); Interlocked.Exchange(ref peak, 0); await Task.Delay(150);
        if (peak > .0001f) throw new InvalidDataException("Stopped loop still produces sound.");
        capture.StopRecording();
        Console.WriteLine($"Loop output: {sound.Name}, {sound.Duration:0.000}s; continued beyond WAV end and stopped silently, one output initialization.");
    }
}
