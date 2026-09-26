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

    private sealed class SilentOutput : IAnimationAudioOutput
    {
        public event Action<string>? Failed { add { } remove { } }
        public void Start(ISampleProvider source) { }
        public void Dispose() { }
    }
}
