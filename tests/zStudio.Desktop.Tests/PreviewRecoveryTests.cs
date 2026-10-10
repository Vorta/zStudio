using HelixToolkit.SharpDX;
using Recoil.Zbd.Rendering;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class PreviewRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InterfaceRecoveryRefusesNestedOperationsBeforeDisposingAndAllowsLaterRetry(bool dispose)
    {
        // No window, D3DImage or hardware surface: one small software device.
        using var owned = new PreviewEffectsManager(new() { EnableSoftwareRendering = true });
        IEffectsManager manager = owned;
        manager.DisposeAllResources();
        int notifications = 0;
        EventHandler<EventArgs> callback = (_, _) =>
        {
            notifications++;
            var device = owned.Device;
            Assert.NotNull(device);
            try
            {
                if (dispose) manager.DisposeAllResources(); else manager.Reinitialize();
            }
            finally
            {
                Assert.Same(device, owned.Device);
                Assert.False(device.IsDisposed); // Nested disposal must refuse before touching it.
            }
        };
        owned.Reinitialized += callback;
        try
        {
            var error = Assert.Throws<InvalidOperationException>(manager.Reinitialize);
            Assert.Contains("retry the preview", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, notifications);
        }
        finally { owned.Reinitialized -= callback; }

        // An unrelated callback exception must also release the recovery guard.
        var unrelated = new InvalidOperationException("controlled device observer failure");
        EventHandler<EventArgs> failing = (_, _) => throw unrelated;
        manager.DisposeAllResources();
        owned.Reinitialized += failing;
        try { Assert.Same(unrelated, Assert.Throws<InvalidOperationException>(manager.Reinitialize)); }
        finally { owned.Reinitialized -= failing; }
        manager.DisposeAllResources();
        manager.Reinitialize();
        Assert.NotNull(owned.Device);
        Assert.False(owned.Device.IsDisposed);
    }

    [Fact]
    public void FailedPassInstallationDisposesTheUnpublishedResource()
    {
        var resource = new Resource();
        var original = new InvalidOperationException("controlled shader installation failure");
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() =>
            PreviewResourceLifetime.Create(() => resource, _ => throw original)));
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public void FailedPassInstallationRetainsBothOriginalAndDisposalErrors()
    {
        var disposal = new InvalidOperationException("controlled disposal failure");
        var resource = new Resource { DisposalFailure = disposal };
        var original = new InvalidOperationException(new string('x', 100_000));
        var result = Assert.Throws<InvalidOperationException>(() =>
            PreviewResourceLifetime.Create(() => resource, _ => throw original));
        var evidence = Assert.IsType<AggregateException>(result.InnerException);
        Assert.Equal(new Exception[] { original, disposal }, evidence.InnerExceptions);
        Assert.True(result.Message.Length < 600);
        Assert.Equal(1, resource.Disposals);
    }

    [Fact]
    public void FailedAttachmentDetachesDisposesAndRetriesWithANewIdentity()
    {
        Resource? retained = null, attached = null;
        List<Resource> created = [];
        List<string> order = [];
        var original = new InvalidOperationException("controlled interop failure");
        bool refuse = true;
        Resource Create() { var item = new Resource { Disposing = () => order.Add("dispose") }; created.Add(item); return item; }
        void Attach(Resource? item)
        {
            attached = item;
            order.Add(item == null ? "detach" : "attach");
            if (item != null && refuse) throw original;
        }
        Assert.Same(original, Assert.Throws<InvalidOperationException>(() => PreviewResourceLifetime.Attach(ref retained, Create, Attach)));
        Assert.Null(retained); Assert.Null(attached);
        Assert.Equal(new[] { "attach", "detach", "dispose" }, order);
        Assert.Single(created); Assert.Equal(1, created[0].Disposals);
        refuse = false;
        PreviewResourceLifetime.Attach(ref retained, Create, Attach);
        Assert.Equal(2, created.Count); Assert.Same(created[1], retained); Assert.Same(retained, attached);
        PreviewResourceLifetime.Attach(ref retained, Create, Attach);
        Assert.Equal(2, created.Count); Assert.Equal(0, created[1].Disposals);
        retained!.Dispose();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void AttachmentCleanupAttemptsBothStepsAndRetainsFailures(bool failDetach, bool failDispose)
    {
        var original = new InvalidOperationException("controlled attach failure");
        var detach = new InvalidOperationException("controlled detach failure");
        var disposal = new InvalidOperationException("controlled disposal failure");
        var resource = new Resource { DisposalFailure = failDispose ? disposal : null };
        Resource? retained = resource;
        int detachCalls = 0;
        var result = Assert.Throws<InvalidOperationException>(() => PreviewResourceLifetime.Attach(ref retained,
            () => throw new InvalidOperationException("Retained resource must be reused"), item =>
            {
                if (item != null) throw original;
                detachCalls++;
                if (failDetach) throw detach;
            }));
        Assert.Null(retained); Assert.Equal(1, detachCalls); Assert.Equal(1, resource.Disposals);
        var evidence = Assert.IsType<AggregateException>(result.InnerException);
        Assert.Same(original, evidence.InnerExceptions[0]);
        Assert.Equal(1 + (failDetach ? 1 : 0) + (failDispose ? 1 : 0), evidence.InnerExceptions.Count);
        if (failDetach) Assert.Contains(detach, evidence.InnerExceptions);
        if (failDispose) Assert.Contains(disposal, evidence.InnerExceptions);
    }

    private sealed class Resource : IDisposable
    {
        internal int Disposals { get; private set; }
        internal Exception? DisposalFailure { get; init; }
        internal Action? Disposing { get; init; }
        public void Dispose()
        {
            Disposals++; Disposing?.Invoke();
            if (DisposalFailure != null) throw DisposalFailure;
        }
    }
}
