using System.Text;
using Xunit;

namespace Recoil.Zbd.PrWatch.Tests;

public sealed class ProcessTests
{
    [Fact]
    public async Task StopPersistsLocallyEvenWithoutAnAvailableQueueExecutable()
    {
        using var fixture = new WatcherTests.Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "zStudio.slnx"), "test workspace", TestContext.Current.CancellationToken);
        var state = fixture.NewState(); state.ReleaseAuthorized = true;
        WatchLogic.Observe(state, WatcherTests.Observe(WatcherTests.Comment(1)), WatcherTests.Now);
        fixture.Store.Save(state);
        int exit = await Program.Main(["stop", "--workspace", fixture.Root, "--pr", "14", "--codex", Path.Combine(fixture.Root, "missing.exe")]);
        Assert.Equal(0, exit); var saved = fixture.Store.Load()!;
        Assert.False(saved.Active); Assert.False(saved.CommentsArmed); Assert.False(saved.ApprovalEnabled); Assert.False(saved.ReleaseAuthorized);
        Assert.NotNull(saved.Notices[0].Acknowledged); Assert.StartsWith("uncertain", saved.Notices[0].Cleanup);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StopDisarmsBothChannelsOfThePullRequest(bool fromClaude)
    {
        using var fixture = new WatcherTests.Fixture();
        await File.WriteAllTextAsync(Path.Combine(fixture.Root, "zStudio.slnx"), "test workspace", TestContext.Current.CancellationToken);
        var codex = fixture.NewState(); codex.ReleaseAuthorized = true; fixture.Store.Save(codex);
        var claudeStore = new WatchStore(fixture.Root, 14, "claude"); var claude = fixture.NewState(); claude.ReleaseAuthorized = true;
        WatchLogic.Observe(claude, WatcherTests.Observe(WatcherTests.Comment(1)), WatcherTests.Now); claudeStore.Save(claude);
        string[] args = ["stop", "--workspace", fixture.Root, "--pr", "14", "--codex", Path.Combine(fixture.Root, "missing.exe")];
        Assert.Equal(0, await Program.Main(fromClaude ? [.. args, "--claude"] : args));
        foreach (var saved in new[] { fixture.Store.Load()!, claudeStore.Load()! })
        { Assert.False(saved.Active); Assert.False(saved.CommentsArmed); Assert.False(saved.ApprovalEnabled); Assert.False(saved.ReleaseAuthorized); }
        // The Claude channel's claim is retired without a queue; its absent sibling folder is never created by a stop.
        Assert.Equal("not queued", Assert.Single(claudeStore.Load()!.Notices).Cleanup);
        using var alone = new WatcherTests.Fixture();
        await File.WriteAllTextAsync(Path.Combine(alone.Root, "zStudio.slnx"), "test workspace", TestContext.Current.CancellationToken);
        alone.Store.Save(alone.NewState());
        Assert.Equal(0, await Program.Main(["stop", "--workspace", alone.Root, "--pr", "14", "--codex", Path.Combine(alone.Root, "missing.exe")]));
        Assert.False(Directory.Exists(new WatchStore(alone.Root, 14, "claude").Folder));
    }
    [Fact]
    public async Task OutputIsBoundedBeforeAnEntireStreamIsMaterialized()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(new string('x', 8192)));
        using var reader = new StreamReader(stream);
        await Assert.ThrowsAsync<InvalidDataException>(() => CommandRunner.BoundedReadAsync(reader, 1000, TestContext.Current.CancellationToken));
    }
    [Fact]
    public void DiscoveryRejectsMissingAndShellShimExecutables()
    {
        Assert.Throws<FileNotFoundException>(() => CommandRunner.Executable("codex", Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".exe")));
        Assert.Throws<FileNotFoundException>(() => CommandRunner.Executable("codex", "codex.cmd"));
        Assert.Throws<FileNotFoundException>(() => CommandRunner.Executable("codex", "codex.exe"));
    }
    [Fact]
    public void ProcessArgumentsRemainLiteralWithoutAShell()
    {
        var info = CommandRunner.StartInfo("tool.exe", ["folder with spaces", "$(not-a-command)", "a;b"], Path.GetTempPath());
        Assert.False(info.UseShellExecute); Assert.True(info.CreateNoWindow);
        Assert.Equal(["folder with spaces", "$(not-a-command)", "a;b"], info.ArgumentList);
    }
}
