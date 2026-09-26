using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NAudio.Wave;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Desktop.Audio;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class DraftResolutionMcpChecks
{
    internal static async Task Run()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            foreach (string scenario in new[] { "owner", "document", "cancellation" })
            {
                var package = new AnimationPackage { Prefix = new byte[72], Tail = [] };
                package.Entries.Add(new(new byte[308], 0, 0));
                using var document = new DocumentModel(new ZbdDocument("draft-fixture", new(0, DateTime.MinValue),
                    new(FormatFamily.Animation, 28, Recognition.Supported, "draft fixture"), ReadOnlyMemory<byte>.Empty) { Animations = package });
                main.ViewModel.Documents.Add(document);
                var world = new ZbdDocument("world", new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
                var context = new AnimationPreviewContext { Package = document.AnimationEdits!.Package, World = world };
                using var audio = new AnimationAudio(() => new SilentOutput());
                using var editor = new AnimationEditor(document, 0, new AssetResolver(Path.GetTempPath()), CancellationToken.None, null, audio);
                var player = new AnimationPlayer(context, 0);
                typeof(AnimationEditor).GetField("context", flags)!.SetValue(editor, context);
                typeof(AnimationEditor).GetField("player", flags)!.SetValue(editor, player);
                typeof(AnimationEditor).GetField("frame", flags)!.SetValue(editor, player.Frame());
                ((FrameworkElement)editor.FindName("LoadingPanel")).Visibility = Visibility.Collapsed;
                ((TextBox)editor.FindName("Seed")).Text = "2";
                ((TextBox)editor.FindName("EndTime")).Text = "1";
                typeof(MainWindow).GetField("animation", flags)!.SetValue(main, editor);
                typeof(MainWindow).GetField("shownDocument", flags)!.SetValue(main, document);
                var gate = (SemaphoreSlim)typeof(AnimationEditor).GetField("simulationGate", flags)!.GetValue(editor)!;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                await gate.WaitAsync(deadline.Token);
                var command = main.Commands.ExecuteAsync("zstudio_resolve_drafts", new()
                {
                    ["document"] = document.SessionId.ToString(), ["target"] = "preview", ["token"] = editor.DraftToken, ["action"] = "apply"
                }, request.Token);
                try
                {
                    await Dispatcher.Yield(DispatcherPriority.Background);
                    Assert.False(command.IsCompleted);
                    Assert.False(((Task)typeof(AnimationEditor).GetField("optionWork", flags)!.GetValue(editor)!).IsCompleted);
                    if (scenario == "owner") typeof(MainWindow).GetField("animation", flags)!.SetValue(main, null);
                    if (scenario == "document") main.ViewModel.Documents.Remove(document);
                    if (scenario == "cancellation") request.Cancel();
                }
                finally { gate.Release(); }
                if (scenario == "cancellation") await Assert.ThrowsAnyAsync<OperationCanceledException>(() => command);
                else Assert.Equal(scenario == "owner" ? "context_changed" : "stale_document",
                    (await Assert.ThrowsAsync<StudioCommandException>(() => command)).Code);
                typeof(MainWindow).GetField("animation", flags)!.SetValue(main, null);
                typeof(MainWindow).GetField("shownDocument", flags)!.SetValue(main, null);
                main.ViewModel.Documents.Remove(document);
                await audio.DisposeAsync();
            }
        }
        finally { main.Close(); }
    }
    private sealed class SilentOutput : IAnimationAudioOutput
    {
        public event Action<string>? Failed { add { } remove { } }
        public void Start(ISampleProvider source) { }
        public void Dispose() { }
    }
}
