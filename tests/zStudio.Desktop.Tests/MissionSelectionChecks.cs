using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Rendering;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class MissionSelectionChecks
{
    internal static async Task Run()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token = timeout.Token;
        using var fixture = new Mw3MissionFixture("actor_01");
        string second = Path.Combine(fixture.Folder, "readerm2.zbd"); File.WriteAllBytes(second, fixture.ReaderBytes);
        var asset = fixture.World.Add(AssetKind.World, 0, "Whole world", 0, 0);
        using var document = new DocumentModel(fixture.World);
        var mission = await MissionSceneLoader.LoadAsync(fixture.World, fixture.Resolver, token: token);
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Resolver))!.SetValue(main.ViewModel, fixture.Resolver);
        main.ViewModel.Documents.Add(document);
        var viewport = new SceneViewport();
        await viewport.ShowAsync(fixture.World, asset, fixture.Resolver, null, 0, token, mission: mission);
        Set("shownDocument", document); Set("shownAsset", asset); Set("scene", viewport);
        var host = (ContentControl)main.FindName("SceneHost"); host.Content = viewport; host.Visibility = Visibility.Visible;
        Set("publishedStaticOptions", typeof(MainWindow).GetMethod("ReadStaticSceneOptions", flags)!.Invoke(main, null));
        TaskCompletionSource enteredFirst = new(TaskCreationOptions.RunContinuationsAsynchronously), enteredSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously), releaseSecond = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var load = main.LoadAssetPropertiesAsync; int requests = 0;
        main.LoadAssetPropertiesAsync = async (doc, item, ct) =>
        {
            int request = ++requests;
            if (request == 1) { enteredFirst.TrySetResult(); await releaseFirst.Task.WaitAsync(token); }
            else if (request == 2) { enteredSecond.TrySetResult(); await releaseSecond.Task.WaitAsync(token); }
            return await load(doc, item, ct);
        };
        Task? first = null, newest = null;
        try
        {
            first = Select(); await enteredFirst.Task.WaitAsync(token);
            newest = Select();
            // Selecting an already-requested but not-yet-presented mission must
            // still await publication; the older request cannot claim success.
            Assert.False(newest.IsCompleted, "A pending mission was reported as already selected.");
            await enteredSecond.Task.WaitAsync(token);
            releaseFirst.TrySetResult();
            var error = await Assert.ThrowsAsync<StudioCommandException>(async () => await first);
            Assert.Equal("context_changed", error.Code);
            releaseSecond.TrySetResult(); await newest.WaitAsync(token);
            var current = (SceneViewport)typeof(MainWindow).GetField("scene", flags)!.GetValue(main)!;
            Assert.Equal(second, current.Mission!.Layout.MissionArchive);
            Assert.Equal(second, fixture.Resolver.SelectedMission(fixture.World.Path));
            Assert.Equal(second, main.ViewModel.Settings.Mw3Missions![fixture.World.Path]);
            Assert.Equal(fixture.ReaderBytes, File.ReadAllBytes(fixture.ReaderPath));
        }
        finally
        {
            releaseFirst.TrySetResult(); releaseSecond.TrySetResult();
            foreach (var task in new[] { first, newest }) if (task != null) try { await task.WaitAsync(token); } catch { }
            main.LoadAssetPropertiesAsync = load; main.Close();
        }
        Task Select() => (Task)typeof(MainWindow).GetMethod("SelectMissionAsync", flags)!.Invoke(main, [second, true, token])!;
        void Set(string field, object? value) => typeof(MainWindow).GetField(field, flags)!.SetValue(main, value);
    }
}
