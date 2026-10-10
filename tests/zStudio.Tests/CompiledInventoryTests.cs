using System.Buffers.Binary;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class CompiledInventoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void ResourceDiscoveryPreservesDirectoryOrderDeduplicatesAndRechecksDisk()
    {
        using Fixture f = new();
        string mission = Directory.CreateDirectory(Path.Combine(f.Root, "m1")).FullName;
        string z = f.Archive("m1/z.zbd"), a = f.Archive("m1/A.zbd"), shared = f.Archive("shared.zbd");
        using AssetResolver resolver = new(f.Root);
        string world = Path.Combine(mission, "gamez.zbd");
        Assert.Equal([a, z, shared], MissionSceneLoader.ResourceFiles(world, resolver, Token));
        f.Archive("m1/b.zbd");
        Assert.Equal([a, Path.Combine(mission, "b.zbd"), z, shared], MissionSceneLoader.ResourceFiles(world, resolver, Token));
        using AssetResolver same = new(mission);
        Assert.Equal(3, MissionSceneLoader.ResourceFiles(world, same, Token).Length);
        Assert.Empty(FormatRegistry.Default.OpenBytes(a, File.ReadAllBytes(a), token: Token).Assets);
    }
    [Fact]
    public void IgnoredEntriesAndQueuedDirectoriesSpendTheSameVisitAllowance()
    {
        using Fixture f = new();
        File.WriteAllText(Path.Combine(f.Root, "ignored.txt"), "ignored");
        Directory.CreateDirectory(Path.Combine(f.Root, "nested")); f.Archive("nested/one.zbd");
        var budget = new CompiledInventory(1024 * 1024, 2, Token);
        Assert.Throws<CompiledInventoryCapacityException>(() => budget.Entries(f.Root, recurse: true).ToArray());
        Assert.Equal(3, new CompiledInventory(Token).Entries(f.Root, recurse: true).Count());
    }
    [Fact]
    public void PathAdmissionPrecedesRetentionAndAnExhaustedOwnerCannotRestart()
    {
        using Fixture f = new(); f.Archive("one.zbd");
        // Exactly the root reservation: the first entry cannot be retained.
        var budget = new CompiledInventory(256L + 8L * f.Root.Length, 10, Token);
        Assert.Throws<CompiledInventoryCapacityException>(() => budget.Files(f.Root));
        Assert.Throws<CompiledInventoryCapacityException>(() => budget.Rows(0));
        Assert.Single(new CompiledInventory(Token).Files(f.Root));
    }
    [Fact]
    public void StableSortingChargesComparisonsAndPreservesCancellationType()
    {
        List<string> equal = ["a", "A", "a"];
        new CompiledInventory(Token).Sort(equal, StringComparer.OrdinalIgnoreCase.Compare, p => p);
        Assert.Equal(["a", "A", "a"], equal);
        List<string> values = ["z", "a"];
        var tooSmall = new CompiledInventory(4 * 64, 10, Token);
        Assert.Throws<CompiledInventoryCapacityException>(() => tooSmall.Sort(values, StringComparer.Ordinal.Compare, p => p));
        Assert.Equal(["z", "a"], values); // Failed sorting does not mutate the caller's list.
        using CancellationTokenSource cancel = new();
        Assert.Throws<OperationCanceledException>(() => new CompiledInventory(cancel.Token).Sort(values, (a, b) =>
        { cancel.Cancel(); return StringComparer.Ordinal.Compare(a, b); }, p => p));
        Assert.Equal(["z", "a"], values);
    }
    [Fact]
    public void FingerprintIsCompactCompleteAndRefreshesStamps()
    {
        using Fixture f = new(); string a = f.Archive("a.zbd"), b = f.Archive("b.zbd");
        string first = new CompiledInventory(Token).Fingerprint([a, b]);
        Assert.Equal(64, first.Length);
        Assert.Equal(first, new CompiledInventory(Token).Fingerprint([a, b]));
        Assert.NotEqual(first, new CompiledInventory(Token).Fingerprint([b, a]));
        File.SetLastWriteTimeUtc(a, File.GetLastWriteTimeUtc(a).AddSeconds(2));
        Assert.NotEqual(first, new CompiledInventory(Token).Fingerprint([a, b]));
        var budget = new CompiledInventory(0, 10, Token);
        Assert.Throws<CompiledInventoryCapacityException>(() => budget.Fingerprint([a]));
        File.Delete(a);
        Assert.Throws<FileNotFoundException>(() => new CompiledInventory(Token).Fingerprint([a]));
    }
    [Fact]
    public void TextureDiscoveryPreservesPriorityAndFullIdentitiesWithFreshRetry()
    {
        using Fixture f = new();
        string low = f.Texture("rtexture1.zbd"), high = f.Texture("texture8.zbd"), image = f.Texture("image.zbd");
        f.Archive("resource.zbd");
        using AssetResolver resolver = new(f.Root);
        string world = Path.Combine(f.Root, "gamez.zbd");
        Assert.Throws<CompiledInventoryCapacityException>(() => resolver.TexturePacks(world, new CompiledInventory(0, 10, Token)));
        Assert.Equal([high, low, image], resolver.TexturePacks(world, Token));
        File.Delete(high); Assert.Equal([low, image], resolver.TexturePacks(world, Token));
        Assert.Throws<OperationCanceledException>(() => resolver.TexturePacks(world, new CancellationToken(true)));
    }
    [Fact]
    public async Task PickupAndMw3DiscoveryPropagateCancellationBeforePartialLoading()
    {
        using Fixture f = new(); f.Archive("one.zbd");
        using AssetResolver resolver = new(f.Root); string world = Path.Combine(f.Root, "gamez.zbd");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PickupPlacementEditSession.LoadAsync(world, resolver, new CancellationToken(true)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => MissionSceneLoader.Mw3MissionCatalogAsync(world, resolver, new CancellationToken(true)));
        Assert.Empty((await MissionSceneLoader.Mw3MissionCatalogAsync(world, resolver, Token)).Missions);
    }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "zstudio-compiled-inventory-" + Guid.NewGuid().ToString("N"))).FullName;
        internal string Archive(string relative)
        { byte[] bytes = new byte[8]; BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1); return Write(relative, bytes); }
        internal string Texture(string relative)
        { byte[] bytes = new byte[24]; BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), 1); return Write(relative, bytes); }
        private string Write(string relative, byte[] bytes)
        { string path = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes); return path; }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
