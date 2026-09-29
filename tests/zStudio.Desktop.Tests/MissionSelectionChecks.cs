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

    /// <summary>A LOD/horizon/resource refresh that supersedes the mission refresh carries the selection.</summary>
    internal static async Task RunSupersedingRefresh()
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
        Task? selection = null, refresh = null;
        try
        {
            selection = (Task)typeof(MainWindow).GetMethod("SelectMissionAsync", flags)!.Invoke(main, [second, true, token])!;
            await enteredFirst.Task.WaitAsync(token);
            // An unrelated option refresh cancels the mission's refresh after it captured readerm2.
            refresh = (Task)typeof(MainWindow).GetMethod("RefreshStaticSceneAsync", flags)!.Invoke(main, [document, asset])!;
            await enteredSecond.Task.WaitAsync(token);
            releaseFirst.TrySetResult();
            await Task.Delay(50, token);
            Assert.False(selection.IsCompleted, "A superseded mission refresh was reported before the newer refresh finished.");
            Assert.Equal(second, fixture.Resolver.SelectedMission(fixture.World.Path), ignoreCase: true);
            releaseSecond.TrySetResult();
            await selection.WaitAsync(token); await refresh.WaitAsync(token);
            var current = (SceneViewport)typeof(MainWindow).GetField("scene", flags)!.GetValue(main)!;
            Assert.Equal(second, current.Mission!.Layout.MissionArchive, ignoreCase: true);
            Assert.Equal(second, fixture.Resolver.SelectedMission(fixture.World.Path), ignoreCase: true);
            Assert.Equal(second, main.ViewModel.Settings.Mw3Missions[fixture.World.Path], ignoreCase: true);
            var published = typeof(MainWindow).GetField("publishedStaticOptions", flags)!.GetValue(main)!;
            Assert.Equal(second, (string?)published.GetType().GetProperty("Mission")!.GetValue(published), ignoreCase: true);
            Assert.Equal(fixture.ReaderBytes, File.ReadAllBytes(fixture.ReaderPath));
        }
        finally
        {
            releaseFirst.TrySetResult(); releaseSecond.TrySetResult();
            foreach (var task in new[] { selection, refresh }) if (task != null) try { await task.WaitAsync(token); } catch { }
            main.LoadAssetPropertiesAsync = load; main.Close();
        }
        void Set(string field, object? value) => typeof(MainWindow).GetField(field, flags)!.SetValue(main, value);
    }

    /// <summary>A remembered reader that no longer qualifies falls back visibly; an explicit request for it is rejected.</summary>
    internal static async Task RunUnavailableSelection()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var token = timeout.Token;
        using var fixture = new Mw3MissionFixture("actor_01");
        string second = Path.Combine(fixture.Folder, "readerm2.zbd"); File.WriteAllBytes(second, fixture.ReaderBytes);
        var asset = fixture.World.Add(AssetKind.World, 0, "Whole world", 0, 0);
        using var document = new DocumentModel(fixture.World);
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(MainViewModel).GetProperty(nameof(MainViewModel.Resolver))!.SetValue(main.ViewModel, fixture.Resolver);
        main.ViewModel.Documents.Add(document);
        fixture.Resolver.SelectMission(fixture.World.Path, second);
        main.ViewModel.Settings.Mw3Missions[fixture.World.Path] = second;
        File.Delete(second); // The remembered reader disappeared between sessions.
        try
        {
            typeof(MainWindow).GetField("shownDocument", flags)!.SetValue(main, document); // ShowAsset previews the shown document.
            await (Task)typeof(MainWindow).GetMethod("ShowAsset", flags)!.Invoke(main, [document, asset])!;
            var current = (SceneViewport?)typeof(MainWindow).GetField("scene", flags)!.GetValue(main);
            Assert.True(current?.Mission != null, main.ViewModel.Status + " | " + ((System.Windows.Controls.TextBlock)main.FindName("EmptyPreview")).Text);
            Assert.Equal(fixture.ReaderPath, current.Mission!.Layout.MissionArchive, ignoreCase: true);
            Assert.Equal(second, current.Mission.Layout.UnavailableMission, ignoreCase: true);
            Assert.Contains(current.Mission.Diagnostics, d => d.Contains("readerm2.zbd", StringComparison.Ordinal));
            Assert.Equal(fixture.ReaderPath, fixture.Resolver.SelectedMission(fixture.World.Path), ignoreCase: true);
            Assert.Equal(fixture.ReaderPath, main.ViewModel.Settings.Mw3Missions[fixture.World.Path], ignoreCase: true);
            var picker = (ComboBox)main.FindName("WorldMission");
            Assert.Equal(Visibility.Visible, picker.Visibility);
            Assert.Equal(fixture.ReaderPath, ((MissionVariant)picker.SelectedItem!).Archive, ignoreCase: true);
            var error = await Assert.ThrowsAsync<StudioCommandException>(async () => await (Task)typeof(MainWindow).GetMethod("SelectMissionAsync", flags)!.Invoke(main, [second, true, token])!);
            Assert.Equal("invalid_argument", error.Code);
            Assert.Equal(fixture.ReaderPath, current.Mission.Layout.MissionArchive, ignoreCase: true);
            Assert.Equal(fixture.ReaderBytes, File.ReadAllBytes(fixture.ReaderPath));
        }
        finally { main.Close(); }
    }
}
