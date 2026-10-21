using zStudio.Core;
using Xunit;

namespace zStudio.Tests;

public sealed class ResolverAllowanceTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("cold")]
    [InlineData("cache")]
    public async Task ResolverAppliesRemainingAllowanceToEveryProvider(string provider)
    {
        using Folder folder = new(); string path = folder.PathOf("texture.zbd");
        await File.WriteAllBytesAsync(path, ModelFixture.Texture(), Token);
        using AssetResolver resolver = new(folder.Path);
        if (provider == "cache") await resolver.OpenCachedAsync(path, Token);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => resolver.OpenCachedAsync(path, 23, Token));
        Assert.Contains("exceeds 23 bytes", error.Message);
        var accepted = await resolver.OpenCachedAsync(path, 24, Token); Assert.Equal(24, accepted.Bytes.Length);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.OpenCachedAsync(path, 24, canceled.Token));
    }

    private sealed class Folder : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "zstudio-read-allowance-" + Guid.NewGuid().ToString("N"));
        public Folder() => Directory.CreateDirectory(Path);
        public string PathOf(string name) => System.IO.Path.Combine(Path, name);
        public void Dispose() => Directory.Delete(Path, true);
    }
}
