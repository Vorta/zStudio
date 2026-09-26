using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class PreviewOperationTests
{
    [Fact]
    public async Task ShutdownReachesPendingWorkButDoesNotOwnLoadedPreviewLifetime()
    {
        using var request = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        using (PreviewOperation.Begin(request.Token))
        {
            await Task.Yield();
            using var pending = PreviewOperation.Link(lifetime.Token);
            request.Cancel();
            Assert.True(pending.IsCancellationRequested);
            Assert.False(lifetime.IsCancellationRequested);
        }
        using var later = PreviewOperation.Link(lifetime.Token);
        Assert.False(later.IsCancellationRequested);
    }

    [Fact]
    public void CompletedRequestCannotCancelRetainedWork()
    {
        using var request = new CancellationTokenSource();
        using var lifetime = new CancellationTokenSource();
        CancellationTokenSource pending;
        using (PreviewOperation.Begin(request.Token)) pending = PreviewOperation.Link(lifetime.Token);
        using (pending)
        {
            request.Cancel();
            Assert.False(pending.IsCancellationRequested);
            lifetime.Cancel();
            Assert.True(pending.IsCancellationRequested);
        }
    }
}
