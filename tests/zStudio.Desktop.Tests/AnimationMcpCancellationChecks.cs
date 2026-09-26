using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
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
