using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using NAudio.Wave;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Desktop.Audio;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class AnimationMcpCancellationChecks
{
    internal static async Task Run()
    {
        await CheckReplayRefusal();
        await CheckRenderRefusal();
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
        var entry = new AnimationEntry(new byte[308], 0, 0); package.Entries.Add(entry);
        var sound = AnimationCatalog.Create(2); sound.SetText(12, "retry"); sound.SetInt(52, 1); entry.Primary.Events.Add(sound);
        var source = new ZbdDocument("fixture", new(0, DateTime.MinValue), new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package };
        using var doc = new DocumentModel(source);
        var world = new ZbdDocument("world", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
        var context = new AnimationPreviewContext { Package = doc.AnimationEdits!.Package, World = world };
        using var wav = new MemoryStream();
        using (var writer = new BinaryWriter(wav, System.Text.Encoding.ASCII, leaveOpen: true))
        {
            writer.Write("RIFF"u8); writer.Write(38); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(48000); writer.Write(96000);
            writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(2); writer.Write((short)0);
        }
        context.Sounds["retry"] = new("retry", "fixture.wav", false, wav.ToArray());
        using var release = new ManualResetEventSlim();
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var audio = new AnimationAudio(() => new SilentOutput(), (_, token) =>
        {
            started.TrySetResult(); release.Wait(token); return new PreparedSound(new float[20]);
        });
        using var editorLifetime = new CancellationTokenSource();
        using var editor = new AnimationEditor(doc, 0, new AssetResolver(Path.GetTempPath()), editorLifetime.Token, null, audio);
        // Install a frozen synthetic context without a GPU scene or physical audio device.
        typeof(AnimationEditor).GetField("context", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(editor, context);
        try
        {
            await editor.SetPreviewOptionAsync("mute", JsonValue.Create(true)!);
            using var request = new CancellationTokenSource();
            using (PreviewOperation.Begin(request.Token))
            {
                var retry = editor.SetPreviewOptionAsync("mute", JsonValue.Create(false)!);
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                request.Cancel();
                await retry.WaitAsync(TimeSpan.FromSeconds(5));
            }
            Assert.False(audio.IsPrepared);
            Assert.False(editorLifetime.IsCancellationRequested);
            Assert.Equal(0, audio.OutputInitializations);
            release.Set();
            await editor.SetPreviewOptionAsync("mute", JsonValue.Create(true)!);
            await editor.SetPreviewOptionAsync("mute", JsonValue.Create(false)!);
            Assert.True(audio.IsPrepared);
            Assert.Equal(1, audio.OutputInitializations);

            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            var initialPlayer = new AnimationPlayer(context, 0);
            typeof(AnimationEditor).GetField("player", flags)!.SetValue(editor, initialPlayer);
            typeof(AnimationEditor).GetField("frame", flags)!.SetValue(editor, initialPlayer.Frame());
            ((FrameworkElement)editor.FindName("LoadingPanel")).Visibility = Visibility.Collapsed;
            var seedInput = (TextBox)editor.FindName("Seed");
            var rangeInput = (TextBox)editor.FindName("EndTime");
            var range = (Slider)editor.FindName("SeekSlider");
            range.Maximum = 1;
            seedInput.Text = "2"; rangeInput.Text = "10";
            editor.ResolveAutomationDrafts(editor.DraftToken, apply: true);
            await editor.AwaitOptionWorkAsync();
            Assert.Equal(10, range.Maximum);
            Assert.Equal(2, ((AnimationPlayer)typeof(AnimationEditor).GetField("player", flags)!.GetValue(editor)!).Seed);

            var gate = (SemaphoreSlim)typeof(AnimationEditor).GetField("simulationGate", flags)!.GetValue(editor)!;
            await gate.WaitAsync();
            Task applying;
            try
            {
                seedInput.Text = "3"; rangeInput.Text = "12";
                editor.ResolveAutomationDrafts(editor.DraftToken, apply: true);
                applying = editor.AwaitOptionWorkAsync();
                Assert.False(applying.IsCompleted);
                seedInput.Text = "99"; rangeInput.Text = "20";
            }
            finally { gate.Release(); }
            Assert.Equal("draft_conflict", (await Assert.ThrowsAsync<StudioCommandException>(() => applying)).Code);
            Assert.Equal(12, range.Maximum); Assert.Equal("20", rangeInput.Text); Assert.Equal("99", seedInput.Text);
            Assert.Equal(3, ((AnimationPlayer)typeof(AnimationEditor).GetField("player", flags)!.GetValue(editor)!).Seed);
            editor.ResolveAutomationDrafts(editor.DraftToken, apply: false);
            await editor.SeekAsync(.5);
            Assert.Equal(.5, ((AnimationFrame)typeof(AnimationEditor).GetField("frame", flags)!.GetValue(editor)!).Time, 6);

            ((FrameworkElement)editor.FindName("LoadingPanel")).Visibility = Visibility.Visible;
            seedInput.Text = "4";
            editor.ResolveAutomationDrafts(editor.DraftToken, apply: true);
            Assert.Equal("not_ready", (await Assert.ThrowsAsync<StudioCommandException>(() => editor.AwaitOptionWorkAsync())).Code);
            Assert.Equal("4", seedInput.Text);
            Assert.Equal(3, ((AnimationPlayer)typeof(AnimationEditor).GetField("player", flags)!.GetValue(editor)!).Seed);
            ((FrameworkElement)editor.FindName("LoadingPanel")).Visibility = Visibility.Collapsed;
            // Force an actual rebuild error. Seek reports failures through the
            // editor; draft resolution must not turn that into MCP success.
            var retainedEntry = context.Package.Entries[0];
            await gate.WaitAsync();
            try
            {
                editor.ResolveAutomationDrafts(editor.DraftToken, apply: true);
                // Field refresh sees the valid document. Only the deferred
                // simulation snapshot fails after it acquires its gate.
                context.Package.Entries.Clear();
            }
            finally { gate.Release(); }
            try
            {
                Assert.Equal("preview_unavailable", (await Assert.ThrowsAsync<StudioCommandException>(() => editor.AwaitOptionWorkAsync())).Code);
            }
            finally { context.Package.Entries.Add(retainedEntry); }
            Assert.Equal(3, ((AnimationPlayer)typeof(AnimationEditor).GetField("player", flags)!.GetValue(editor)!).Seed);
            // The accepted seed text already matches appliedSeed after failure.
            // Retrying it must still finish the pending reconstruction.
            editor.ResolveAutomationDrafts(editor.DraftToken, apply: true);
            await editor.AwaitOptionWorkAsync();
            Assert.Equal(4, ((AnimationPlayer)typeof(AnimationEditor).GetField("player", flags)!.GetValue(editor)!).Seed);
            Assert.Equal(0, ((AnimationFrame)typeof(AnimationEditor).GetField("frame", flags)!.GetValue(editor)!).Time);
            Assert.False((bool)typeof(AnimationEditor).GetField("resetSimulation", flags)!.GetValue(editor)!);
            // A range-only failure can occur after simulation flags were reset.
            // Its retained failure must also require successful reconstruction.
            rangeInput.Text = "8";
            await gate.WaitAsync();
            try
            {
                editor.ResolveAutomationDrafts(editor.DraftToken, apply: true);
                context.Package.Entries.Clear();
            }
            finally { gate.Release(); }
            try { Assert.Equal("preview_unavailable", (await Assert.ThrowsAsync<StudioCommandException>(() => editor.AwaitOptionWorkAsync())).Code); }
            finally { context.Package.Entries.Add(retainedEntry); }
            Assert.False((bool)typeof(AnimationEditor).GetField("resetSimulation", flags)!.GetValue(editor)!);
            long lastSeek = (long)typeof(AnimationEditor).GetField("seekGeneration", flags)!.GetValue(editor)!;
            editor.ResolveAutomationDrafts(editor.DraftToken, apply: true);
            await editor.AwaitOptionWorkAsync();
            Assert.Null(typeof(AnimationEditor).GetField("previewOperationFailure", flags)!.GetValue(editor));
            Assert.Equal(lastSeek + 1, (long)typeof(AnimationEditor).GetField("publishedSeekGeneration", flags)!.GetValue(editor)!);
            Assert.Equal(8, range.Maximum);

            // Explicit scene rebinding must report a failed preview, rather than
            // returning a successful MCP result for the retained loading overlay.
            string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "gamez.zbd");
            var failure = await Assert.ThrowsAsync<StudioCommandException>(() => editor.SetPreviewOptionAsync("worldPath", JsonValue.Create(missing)!));
            Assert.Equal("preview_unavailable", failure.Code);
            using var canceledBind = new CancellationTokenSource();
            canceledBind.Cancel();
            using (PreviewOperation.Begin(canceledBind.Token))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => editor.SetPreviewOptionAsync("worldPath", JsonValue.Create(missing)!));
            // Recovery reached a terminal actionable error with a fresh token;
            // it did not leave an indefinitely spinning canceled loading request.
            Assert.DoesNotContain("Loading animation", ((TextBlock)editor.FindName("LoadingText")).Text);
            Assert.False(editorLifetime.IsCancellationRequested);
        }
        finally { release.Set(); await audio.DisposeAsync(); }
    }

    private static async Task CheckReplayRefusal()
    {
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
        var entry = new AnimationEntry(new byte[308], 0, 0);
        entry.SetText(32, "root"); entry.SetText(68, "root"); entry.SetFloat(164, -1); package.Entries.Add(entry);
        var source = new ZbdDocument("replay", new(0, DateTime.MinValue), new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package };
        using var document = new DocumentModel(source);
        var safe = Context(1, 0);
        var retainedPlayer = new AnimationPlayer(safe, 0);
        var retainedFrame = retainedPlayer.AdvanceTo(1);
        var audio = new AnimationAudio(() => new SilentOutput());
        using var editor = new AnimationEditor(document, 0, new AssetResolver(Path.GetTempPath()), CancellationToken.None, null, audio);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(AnimationEditor).GetField(name, flags)!.SetValue(editor, value);
        object? Get(string name) => typeof(AnimationEditor).GetField(name, flags)!.GetValue(editor);
        Set("context", Context(900, 1300)); Set("player", retainedPlayer); Set("frame", retainedFrame);
        ((FrameworkElement)editor.FindName("LoadingPanel")).Visibility = Visibility.Collapsed;
        ((Slider)editor.FindName("SeekSlider")).Maximum = 1;
        ((TextBox)editor.FindName("EndTime")).Text = "1";
        Assert.True(((Button)editor.FindName("PlayButton")).IsEnabled);
        Assert.False(editor.HasAutomationDrafts);
        Assert.True(retainedPlayer.Time >= ((Slider)editor.FindName("SeekSlider")).Maximum);
        try
        {
            // Only 2,201 synthetic nodes. The real aggregate guard stops the shared-chain
            // frame near one million ancestry visits; no GPU scene/audio output is opened.
            var refusal = await Assert.ThrowsAsync<StudioCommandException>(() => editor.TransportAsync("play"));
            Assert.Equal("preview_unavailable", refusal.Code);
            Assert.Contains("Animation binding", refusal.Message);
            Assert.Same(retainedPlayer, Get("player")); Assert.Same(retainedFrame, Get("frame"));
            Assert.False((bool)Get("playing")!);
            // A later valid replay must clear the old failure and publish the new reset.
            Set("context", safe);
            await editor.TransportAsync("play");
            Assert.True((bool)Get("playing")!);
            Assert.NotSame(retainedPlayer, Get("player"));
            Assert.Equal(0, ((AnimationFrame)Get("frame")!).Time);
            Assert.Null(Get("previewOperationFailure"));
            await editor.TransportAsync("pause");
            Assert.False((bool)Get("playing")!);
            Assert.Equal(0, audio.OutputInitializations);
        }
        finally { editor.Pause(); await audio.DisposeAsync(); }

        AnimationPreviewContext Context(int depth, int leaves)
        {
            GameScene scene = new();
            scene.Nodes.Add(new(0, "world", "world", null, [], [1], new(), new()));
            for (int i = 1; i <= depth + leaves; i++)
            {
                int[] children = i < depth ? [i + 1] : i == depth ? Enumerable.Range(depth + 1, leaves).ToArray() : [];
                scene.Nodes.Add(new(i, i == 1 ? "root" : "node" + i, "object3d", i > depth ? 0 : null,
                    [i <= depth ? i - 1 : depth], children, new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
            }
            var world = new ZbdDocument("world", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
            return new() { Package = document.AnimationEdits!.Package, World = world };
        }
    }

    private static async Task CheckRenderRefusal()
    {
        var token = TestContext.Current.CancellationToken;
        var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
        var entry = new AnimationEntry(new byte[308], 0, 0);
        entry.SetText(32, "root"); entry.SetText(68, "root"); entry.SetFloat(164, -1); package.Entries.Add(entry);
        var source = new ZbdDocument("render-refusal", new(0, DateTime.MinValue), new(FormatFamily.Animation, 28, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package };
        using var document = new DocumentModel(source);
        GameScene scene = new();
        scene.Materials.Add(new() { ["alpha"] = 255, ["texture_index"] = -1 });
        scene.Models.Add(new(0, [System.Numerics.Vector3.Zero, System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY], [], [], [new(0, 3, [0, 1, 2], [], [], [])], []));
        scene.Nodes.Add(new(0, "root", "object3d", 0, [], [], new() { ["flags"] = 4 }, new() { ["flags"] = 8 }));
        var world = new ZbdDocument("world", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 27, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
        var context = new AnimationPreviewContext { Package = document.AnimationEdits!.Package, World = world };
        var audio = new AnimationAudio(() => new SilentOutput());
        using var resolver = new AssetResolver(Path.GetTempPath());
        using var editor = new AnimationEditor(document, 0, resolver, token, null, audio);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        void Set(string name, object value) => typeof(AnimationEditor).GetField(name, flags)!.SetValue(editor, value);
        object? Get(string name) => typeof(AnimationEditor).GetField(name, flags)!.GetValue(editor);
        try
        {
            ((System.Windows.Controls.Primitives.ToggleButton)editor.FindName("ShowLevel")).IsChecked = false;
            var initial = new AnimationPlayer(context, 0); var initialFrame = initial.Frame(token);
            await editor.Viewport.ShowAnimationAsync(context, initialFrame, resolver, false, token);
            Set("context", context); Set("player", initial); Set("frame", initialFrame); Set("audioDirty", false);
            Set("playbackTarget", .5); Set("pendingPlay", true);
            ((FrameworkElement)editor.FindName("LoadingPanel")).Visibility = Visibility.Collapsed;
            editor.Viewport.MaximumRenderUnits = 2;
            // The actual timer callback advances simulation before the renderer refuses.
            // It must not leave that rejected state available to later WPF callbacks.
            typeof(AnimationEditor).GetMethod("Tick", flags)!.Invoke(editor, [null, EventArgs.Empty]);
            Assert.True(initial.Time >= .5);
            Assert.Null(Get("player")); Assert.Null(editor.CurrentFrame);
            Assert.False(editor.IsPlaying); Assert.False((bool)Get("pendingPlay")!);
            Assert.Null(editor.Viewport.PreviewScene); Assert.Equal(0, editor.Viewport.AnimationMeshCount);
            Assert.Equal(0, ((Slider)editor.FindName("SeekSlider")).Value);
            Assert.Null(((AnimationTimeline)editor.FindName("Timeline")).Frame);
            Assert.Equal("Preview unavailable", ((TextBlock)editor.FindName("TimeLabel")).Text);
            var lighting = (System.Windows.Controls.Primitives.ToggleButton)editor.FindName("Lighting"); lighting.IsChecked = lighting.IsChecked != true;
            var follow = (System.Windows.Controls.Primitives.ToggleButton)editor.FindName("FollowCamera");
            var error = await Assert.ThrowsAsync<StudioCommandException>(() => editor.SetPreviewOptionAsync("followCamera", JsonValue.Create(follow.IsChecked != true)!));
            Assert.Equal("preview_unavailable", error.Code);
            Assert.Null(editor.CurrentFrame); Assert.Equal(0, audio.OutputInitializations);
            editor.Viewport.MaximumRenderUnits = 8;
            await editor.TransportAsync("seek", 0);
            var rebuilt = Assert.IsType<AnimationPlayer>(Get("player"));
            Assert.NotSame(initial, rebuilt); var accepted = Assert.IsType<AnimationFrame>(editor.CurrentFrame);
            Assert.Equal(rebuilt.Time, accepted.Time);
            Assert.Equal(1, editor.Viewport.AnimationMeshCount);
            Assert.Null(Get("previewOperationFailure")); Assert.False(editor.IsPlaying);
        }
        finally { editor.Pause(); await audio.DisposeAsync(); }
    }

    private sealed class SilentOutput : IAnimationAudioOutput
    {
        public event Action<string>? Failed { add { } remove { } }
        public void Start(ISampleProvider source) { }
        public void Dispose() { }
    }
}
