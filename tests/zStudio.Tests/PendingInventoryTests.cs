using System.Collections;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class PendingInventoryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static long PathUnits(string path) => 256L + 8L * path.Length;

    [Fact]
    public void IndexAdmitsBeforeCopyAndPreservesCapacityExceptionFromSort()
    {
        string[] one = ["data/one.gltf"];
        long exact = 64 + PathUnits(one[0]);
        Assert.Equal(one, new PendingInventory(one, new(exact), Token));
        Assert.Throws<InventoryCapacityException>(() => new PendingInventory(one, new(exact - 1), Token));
        string[] two = ["data/b.gltf", "data/a.gltf"];
        long beforeSort = 128 + two.Sum(PathUnits);
        Assert.Throws<InventoryCapacityException>(() => new PendingInventory(two, new(beforeSort), Token));
        Assert.Equal(["data/a.gltf", "data/b.gltf"], new PendingInventory(two, new(beforeSort + two[0].Length + 1), Token));
    }

    [Fact]
    public void CancellationDuringSortRetainsCancellationIdentity()
    {
        using var canceled = new CancellationTokenSource();
        var source = new CompleteAfterEnumeration(["data/b.gltf", "data/a.gltf"], canceled.Cancel);
        Assert.Throws<OperationCanceledException>(() => new PendingInventory(source, new(), canceled.Token));
    }

    [Fact]
    public void UnrelatedFolderQueriesReuseTheIndexAndKeepAliasesAndFullPaths()
    {
        string root = Directory.CreateTempSubdirectory("zstudio-pending-index-").FullName;
        try
        {
            string prefix = "data/" + new string('x', 100);
            string[] paths = [prefix + "/First.gltf", prefix.ToUpperInvariant() + "/first.GLTF", prefix + "/second.gltf"];
            var allowance = new InventoryBudget(32 * 1024);
            var pending = new PendingInventory(paths, allowance, Token);
            int predicates = 0;
            for (int i = 1; i <= 24; i++)
                Assert.Empty(SourceProject.Files(root, $"data/m{i}/images", _ => { predicates++; return true; }, pending, Token, allowance));
            Assert.Equal(0, predicates);
            Assert.Equal([paths[0], paths[2]], SourceProject.Files(root, prefix, _ => true, pending, Token, allowance));
            Assert.True(pending.ContainsPath(paths[0].ToUpperInvariant(), Token));
            Assert.False(pending.ContainsPath(prefix + "/absent.gltf", Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PlansSharePendingIndexAcrossMissionsButNewCallsCaptureNewKeys()
    {
        string root = Directory.CreateTempSubdirectory("zstudio-pending-plan-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "gamegen"));
            for (int i = 1; i <= 3; i++) Directory.CreateDirectory(Path.Combine(root, "data", $"m{i}"));
            List<string> added = ["data/common/a.gltf", "data/common/b.gltf", "gamegen/m1.gs", "gamegen/m2.gs", "gamegen/m3.gs"];
            var allowance = new InventoryBudget(48 * 1024);
            var first = SourceBuilder.Plan(root, added, null, false, Token, null, inventory: allowance);
            Assert.Equal(3, first.Count(p => p.Family == "world"));
            Assert.All(first.Where(p => p.Family == "world"), p => Assert.Equal(3, p.Inputs.Count));
            added.Add("data/common/c.gltf");
            var next = SourceBuilder.Plan(root, added, automaticPacks: false, token: Token);
            Assert.All(next.Where(p => p.Family == "world"), p => Assert.Equal(4, p.Inputs.Count));
            Assert.All(first.Where(p => p.Family == "world"), p => Assert.Equal(3, p.Inputs.Count));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void SnapshotAdmitsOverlayResolutionAndCapturesPendingKeysOnce()
    {
        string root = Directory.CreateTempSubdirectory("zstudio-pending-snapshot-").FullName;
        try
        {
            const string path = "data/new.gltf";
            Dictionary<string, byte[]> overlay = new() { [path] = [] };
            long exact = 256L + 8L * (root.Length + 1 + path.Length) + 64 + PathUnits(path);
            var snapshot = new SourceBuilder.Snapshot(root, overlay, inventoryLimit: exact, inventoryToken: Token);
            Assert.Equal(exact, snapshot.Inventory.Used); Assert.Equal([path], snapshot.Added);
            Assert.Same(snapshot.Added, snapshot.Added);
            overlay.Add("data/later.gltf", []);
            Assert.Equal([path], snapshot.Added);
            Assert.Throws<InventoryCapacityException>(() => new SourceBuilder.Snapshot(root, new Dictionary<string, byte[]> { [path] = [] }, inventoryLimit: exact - 1, inventoryToken: Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void PreviewRevalidationDoesNotRelabelCapacityAsExternalChange()
    {
        using SourceWorldFixture fixture = new();
        var snapshot = new SourceBuilder.Snapshot(fixture.Project, inventoryLimit: 0, inventoryToken: Token);
        Assert.Throws<InventoryCapacityException>(() => SourceWorlds.CheckPreviewPlanUnchanged(fixture.Project, "m1", snapshot, [], Token));
    }

    [Fact]
    public void ExportRevalidationDoesNotRelabelCapacityAsProjectChange()
    {
        using SourceWorldFixture fixture = new();
        var snapshot = new SourceBuilder.Snapshot(fixture.Project, inventoryLimit: 0, inventoryToken: Token);
        Assert.Throws<InventoryCapacityException>(() => SourceBuilder.CheckPlanUnchanged(fixture.Project,
            BuildProfiles.Modern.Name, BuildProfiles.Modern, null, [], snapshot, Token));
    }

    [Fact]
    public void ExportRevalidationStillAcceptsUnchangedAndRefusesRealInventoryChanges()
    {
        using SourceWorldFixture fixture = new();
        var planned = SourceBuilder.Plan(fixture.Project, profile: BuildProfiles.Modern, token: Token);
        var snapshot = new SourceBuilder.Snapshot(fixture.Project, inventoryToken: Token);
        SourceBuilder.CheckPlanUnchanged(fixture.Project, BuildProfiles.Modern.Name, BuildProfiles.Modern,
            null, planned, snapshot, Token);
        fixture.Write("data/common/zrdr/added.zrd", "( NOTE ( 1 ) )");
        var changed = Assert.Throws<InvalidDataException>(() => SourceBuilder.CheckPlanUnchanged(fixture.Project,
            BuildProfiles.Modern.Name, BuildProfiles.Modern, null, planned, snapshot, Token));
        Assert.Contains("sources changed", changed.Message);
    }

    private sealed class CompleteAfterEnumeration(string[] paths, Action complete) : IReadOnlyCollection<string>
    {
        public int Count => paths.Length;
        public IEnumerator<string> GetEnumerator() { foreach (string path in paths) yield return path; complete(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
