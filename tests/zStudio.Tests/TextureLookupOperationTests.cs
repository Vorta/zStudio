using System.Buffers.Binary;
using System.Text;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class TextureLookupOperationTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MissingNamesIndexEachPackOnceBeyondTheFourDocumentCache()
    {
        using Fixture f = new();
        for (int i = 0; i < 6; i++) f.Empty("texture" + i);
        using AssetResolver resolver = new(f.Root);
        // Exactly six headers: another cold read cannot be hidden by a generous limit.
        var lookup = new TextureLookupOperation(resolver, f.Context, null, maximumColdReadBytes: 6 * 24);
        for (int i = 0; i < 20; i++) Assert.Null(await lookup.ResolveAsync("missing" + i, Token));
    }

    [Fact]
    public async Task ManyMissingNamesShareWorkAndFailureAllowsAFreshValidOperation()
    {
        using Fixture f = new();
        for (int i = 0; i < 6; i++) f.Empty("texture" + i);
        using AssetResolver resolver = new(f.Root);
        long oneScan = 8 + 6 + Enumerable.Range(0, 6).Sum(i => (long)Path.Combine(f.Root, "texture" + i + ".zbd").Length + 8);
        var lookup = new TextureLookupOperation(resolver, f.Context, null, maximumWork: oneScan + 10);
        Assert.Null(await lookup.ResolveAsync("missing", Token));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        { for (int i = 0; i < 20; i++) await lookup.ResolveAsync("missing" + i, Token); });
        await Assert.ThrowsAsync<InvalidDataException>(() => lookup.ResolveAsync("", Token));
        f.Pack("texture99", "present", "other");
        Assert.Equal("present", (await resolver.BeginTextureLookup(f.Context).ResolveAsync("present.png", Token))!.Asset.Name);
    }

    [Fact]
    public async Task PreferredPackAndStemAliasesKeepFirstRecordAndDuplicateAmbiguity()
    {
        using Fixture f = new();
        string first = f.Pack("texture2", "Foo.png", "FOO.tga");
        string preferred = f.Pack("rtexture9", "foo", "other");
        using AssetResolver resolver = new(f.Root);
        var normal = await resolver.BeginTextureLookup(f.Context).ResolveAsync("FOO.png", Token);
        Assert.NotNull(normal); Assert.Equal(first, normal.Document.Path);
        Assert.Equal(0, normal.Asset.Index); Assert.True(normal.Ambiguous);
        var selected = await resolver.BeginTextureLookup(f.Context, preferred).ResolveAsync("folder/foo.dds", Token);
        Assert.NotNull(selected); Assert.Equal(preferred, selected.Document.Path); Assert.False(selected.Ambiguous);
        Assert.Equal(0, selected.Asset.Index);
    }

    [Fact]
    public async Task MalformedDirectoryRowsDoNotConfuseListOrdinalsWithSerializedIndices()
    {
        using Fixture f = new(); string path = f.Pack("texture1", "foo", "foo");
        byte[] bytes = File.ReadAllBytes(path); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(56), 1); File.WriteAllBytes(path, bytes);
        using AssetResolver resolver = new(f.Root);
        var found = await resolver.BeginTextureLookup(f.Context).ResolveAsync("foo.png", Token);
        Assert.NotNull(found); Assert.Equal(1, found.Asset.Index); Assert.False(found.Ambiguous);
        Assert.Equal(1, Assert.Single(found.Document.Assets).Index);
    }

    [Fact]
    public async Task ColdBytesAreAdmittedBeforeReadAndCachedPacksNeedNoNewReadAllowance()
    {
        using Fixture f = new(); string path = f.Pack("texture1", "foo", "bar");
        using AssetResolver resolver = new(f.Root);
        var denied = new TextureLookupOperation(resolver, f.Context, null, maximumColdReadBytes: new FileInfo(path).Length - 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => denied.ResolveAsync("foo", Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => denied.ResolveAsync("foo", Token));
        Assert.NotNull(await resolver.BeginTextureLookup(f.Context).ResolveAsync("foo", Token));
        Assert.NotNull(await new TextureLookupOperation(resolver, f.Context, null, maximumColdReadBytes: 0).ResolveAsync("bar", Token));
    }

    [Fact]
    public async Task ChangedPacksAndWorkspaceSnapshotsRefuseStaleIndexes()
    {
        using Fixture f = new(); string path = f.Pack("texture1", "foo", "bar");
        using AssetResolver resolver = new(f.Root);
        var old = resolver.BeginTextureLookup(f.Context); Assert.Null(await old.ResolveAsync("missing", Token));
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddSeconds(2));
        await Assert.ThrowsAsync<InvalidDataException>(() => old.ResolveAsync("foo", Token));
        var current = resolver.BeginTextureLookup(f.Context); Assert.NotNull(await current.ResolveAsync("foo", Token));
        var snapshot = FormatRegistry.Default.OpenBytes(path, File.ReadAllBytes(path), token: Token);
        snapshot.Assets[0].Name = "draft"; resolver.SetWorkspaceSnapshots(Guid.NewGuid(), [snapshot]);
        await Assert.ThrowsAsync<InvalidDataException>(() => current.ResolveAsync("foo", Token));
        Assert.Equal("draft", (await resolver.BeginTextureLookup(f.Context).ResolveAsync("draft", Token))!.Asset.Name);
    }

    [Fact]
    public async Task DiscoveryAdmitsBorrowedAndWarmBytesAndCacheEvictsByBytes()
    {
        // Must-have: target discovery precedes replacement in GUI/MCP, so it must not open
        // unbounded sibling buffers even when the selected pack and snapshots are externally owned.
        using Fixture f = new();
        string first = f.Pack("texture1", "foo", "bar"), second = f.Pack("texture2", "foo", "bar");
        byte[] bytes = File.ReadAllBytes(first); long size = bytes.Length;
        using AssetResolver resolver = new(f.Root, maximumCachedBytes: size);
        var original = await resolver.OpenCachedAsync(first, Token);
        await resolver.OpenCachedAsync(second, Token);
        Assert.NotSame(original, await resolver.OpenCachedAsync(first, Token));
        var warm = await resolver.OpenCachedAsync(second, Token);
        TextureEditSession edits = new(original);
        await Assert.ThrowsAsync<InvalidDataException>(() => edits.DiscoverTargetsAsync(0, resolver, 2 * size - 1, TextureLookupOperation.MaximumWork, Token));
        Assert.Same(warm, await resolver.OpenCachedAsync(second, Token));
        Assert.Equal(2, (await edits.DiscoverTargetsAsync(0, resolver, 2 * size, TextureLookupOperation.MaximumWork, Token)).Count);
        await Assert.ThrowsAsync<InvalidDataException>(() => edits.DiscoverTargetsAsync(0, resolver, 2 * size, 1, Token));

        // The disk header is tiny; admission must use the authoritative workspace snapshot's bytes.
        string third = f.Empty("texture3");
        var snapshot = FormatRegistry.Default.OpenBytes(third, bytes, token: Token);
        resolver.SetWorkspaceSnapshots(Guid.NewGuid(), [snapshot]);
        await Assert.ThrowsAsync<InvalidDataException>(() => edits.DiscoverTargetsAsync(0, resolver, 3 * size - 1, TextureLookupOperation.MaximumWork, Token));
        Assert.Equal(3, (await edits.DiscoverTargetsAsync(0, resolver, 3 * size, TextureLookupOperation.MaximumWork, Token)).Count);
        var denied = new TextureLookupOperation(resolver, f.Context, null, maximumColdReadBytes: 0, maximumDiscoveryBytes: size - 1);
        await Assert.ThrowsAsync<InvalidDataException>(() => denied.ResolveAsync("foo", Token));
        await Assert.ThrowsAsync<InvalidDataException>(() => denied.ResolveAsync("foo", Token));
        Assert.Same(snapshot, (await resolver.BeginTextureLookup(f.Context).ResolveAsync("foo", Token))!.Document);
    }

    [Fact]
    public async Task LongOperandsAreRejectedBeforeProjectionAndCancellationHasFreshRetry()
    {
        using Fixture f = new(); f.Pack("texture1", "foo", "bar");
        using AssetResolver resolver = new(f.Root);
        await Assert.ThrowsAsync<InvalidDataException>(() => new TextureLookupOperation(resolver, f.Context, null, maximumWork: 10).ResolveAsync(new string('x', 1000), Token));
        using CancellationTokenSource cancel = new(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resolver.BeginTextureLookup(f.Context).ResolveAsync("foo", cancel.Token));
        Assert.NotNull(await resolver.BeginTextureLookup(f.Context).ResolveAsync("foo", Token));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "zstudio-texture-lookup-" + Guid.NewGuid().ToString("N"))).FullName;
        internal string Context => Path.Combine(Root, "gamez.zbd");
        internal string Empty(string name)
        {
            byte[] bytes = new byte[24]; BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), 1);
            string path = Path.Combine(Root, name + ".zbd"); File.WriteAllBytes(path, bytes); return path;
        }
        internal string Pack(string name, string first, string second)
        {
            byte[] bytes = ContentFixture.Texture(2, 2, false);
            bytes.AsSpan(24, 32).Clear(); Encoding.ASCII.GetBytes(first).CopyTo(bytes, 24);
            bytes.AsSpan(64, 32).Clear(); Encoding.ASCII.GetBytes(second).CopyTo(bytes, 64);
            string path = Path.Combine(Root, name + ".zbd"); File.WriteAllBytes(path, bytes); return path;
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
