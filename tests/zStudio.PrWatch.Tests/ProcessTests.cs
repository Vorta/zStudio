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
        WatchLogic.Observe(state, WatcherTests.Observe(WatcherTests.Comment(1)), WatcherTests.Now, fixture.Store.StatePath);
        fixture.Store.Save(state);
        int exit = await Program.Main(["stop", "--workspace", fixture.Root, "--pr", "14", "--codex", Path.Combine(fixture.Root, "missing.exe")]);
        Assert.Equal(0, exit); var saved = fixture.Store.Load()!;
        Assert.False(saved.Active); Assert.False(saved.CommentsArmed); Assert.False(saved.ApprovalEnabled); Assert.False(saved.ReleaseAuthorized);
        Assert.NotNull(saved.Notices[0].Acknowledged); Assert.StartsWith("uncertain", saved.Notices[0].Cleanup);
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
