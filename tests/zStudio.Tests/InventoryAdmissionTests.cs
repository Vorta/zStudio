using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Sources;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class InventoryAdmissionTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    // Independent documented accounting: four UTF-16 representations plus256 path/row overhead,
    // and64 units for each additional reference row retained by an ordering/aggregation step.
    private static long PathUnits(string path) => 256L + 8L * path.Length;

    [Fact]
    public void DiskPathsAreAdmittedBeforeRelativeProjectionAndExactBoundarySucceeds()
    {
        using Project project = new();
        project.Write("data/b.gs", "b"); project.Write("data/a.gs", "a");
        long exact = PathUnits(project.Full("data/a.gs")) + PathUnits(project.Full("data/b.gs")) + 128;
        var budget = new InventoryBudget(exact);
        Assert.Equal(["data/a.gs", "data/b.gs"], SourceProject.Files(project.Root, "data", _ => true, token: Token, inventory: budget));
        Assert.Equal(exact, budget.Used);
        Assert.Throws<InventoryCapacityException>(() => SourceProject.Files(project.Root, "data", _ => true, token: Token, inventory: new(exact - 1)));
        int retained = 0;
        long first = Math.Min(PathUnits(project.Full("data/a.gs")), PathUnits(project.Full("data/b.gs")));
        Assert.Throws<InventoryCapacityException>(() => SourceProject.Files(project.Root, "data", _ => true, token: Token,
            inventory: new(first - 1), retainPath: _ => retained++));
        Assert.Equal(0, retained);
    }

    [Fact]
    public void IgnoredEntriesAndRepeatedListingsSpendTheSharedAllowance()
    {
        using Project project = new();
        project.Write("data/ignored.bin", "");
        long path = PathUnits(project.Full("data/ignored.bin"));
        var shared = new InventoryBudget(2 * path - 1);
        Assert.Empty(SourceProject.Files(project.Root, "data", _ => false, token: Token, inventory: shared));
        Assert.Equal(path, shared.Used);
        Assert.Throws<InventoryCapacityException>(() => SourceProject.Files(project.Root, "data", _ => false, token: Token, inventory: shared));
        Assert.Throws<InventoryCapacityException>(() => shared.Rows(0, Token));
        Assert.Empty(SourceProject.Files(project.Root, "data", _ => false, token: Token, inventory: new(path)));
    }

    [Fact]
    public void LongerPrefixesCostMoreWithoutChangingFileCountOrContent()
    {
        using Project project = new();
        project.Write("data/a/x.gs", "same"); project.Write("data/longer-prefix/x.gs", "same");
        long small = PathUnits(project.Full("data/a/x.gs")) + 64;
        Assert.Equal(["data/a/x.gs"], SourceProject.Files(project.Root, "data/a", _ => true, token: Token, inventory: new(small)));
        Assert.Throws<InventoryCapacityException>(() => SourceProject.Files(project.Root, "data/longer-prefix", _ => true, token: Token, inventory: new(small)));
    }

    [Fact]
    public void PendingOnlyAndCaseAliasPathsKeepCompleteIdentityAndDeduplicate()
    {
        using Project project = new();
        string[] pending = ["data/missing/New.gs", "DATA/MISSING/new.gs", "gamegen/other.gs"];
        int retained = 0;
        Assert.Equal([pending[0]], SourceProject.Files(project.Root, "data/missing", _ => true, pending, Token,
            new(), _ => retained++));
        Assert.Equal(1, retained);
        project.Write("data/existing.gs", "original");
        Assert.Equal(["data/existing.gs"], SourceProject.Files(project.Root, "data", _ => true, ["DATA/EXISTING.GS"], Token));
        Assert.Equal("original", File.ReadAllText(project.Full("data/existing.gs")));
    }

    [Fact]
    public void CancellationAndFailedAllowanceDoNotPoisonANewOperation()
    {
        using Project project = new(); project.Write("data/a.gs", "a");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        var allowance = new InventoryBudget();
        Assert.Throws<OperationCanceledException>(() => SourceProject.Files(project.Root, "data", _ => true, token: canceled.Token, inventory: allowance));
        Assert.Equal(0, allowance.Used);
        Assert.Equal(["data/a.gs"], SourceProject.Files(project.Root, "data", _ => true, token: Token));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InventoryBudget(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new InventoryBudget(InventoryBudget.MaximumUnits + 1));
    }

    [Fact]
    public void EffectInventoryUsesItsNarrowAllowanceBeforeReadingResources()
    {
        using Project project = new();
        project.Write("data/common/zrdr/effects.zrd", "This resource must never be parsed.");
        var snapshot = new SourceBuilder.Snapshot(project.Root, effectLimits: new(RetainedBytes: 150));
        var error = Assert.Throws<InvalidDataException>(() => snapshot.Effects("m1", Token));
        Assert.Contains("retained names", error.Message);
        Assert.Empty(snapshot.Hashes()); Assert.Equal(0, snapshot.EffectInputBytes);
    }

    [Fact]
    public async Task SelectedAnimationStillAdmitsEarlierCommonInventoryAndRetryPublishes()
    {
        using Project project = new();
        project.Write("data/common/zrdr/note.zrd", "( NOTE ( 1 ) )");
        project.Write("data/m1/zrdr/anim.zad", "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( gate ) SEQUENCE_DEFINITION ( NAME ( s ) ) ) ) ) )");
        string destination = project.Root + "-export";
        Directory.CreateDirectory(Path.Combine(destination, "m1"));
        string target = Path.Combine(destination, "m1", "anim.zbd"); byte[] sentinel = [7, 8, 9]; File.WriteAllBytes(target, sentinel);
        try
        {
            await Assert.ThrowsAsync<InventoryCapacityException>(() => SourceBuilder.RunAsync(project.Root, destination,
                ["m1/anim.zbd"], true, null, Token, null, inventoryLimit: 0));
            Assert.Equal(sentinel, File.ReadAllBytes(target));
            var report = await SourceBuilder.RunAsync(project.Root, destination, ["m1/anim.zbd"], true, null, Token, null);
            Assert.Equal(1, report.Built); Assert.Equal(0, report.Failed);
            Assert.NotEqual(sentinel, File.ReadAllBytes(target));
        }
        finally { Directory.Delete(destination, true); }
    }

    [Fact]
    public void BuilderPlanSharesInventoryAcrossFoldersAndRevalidationPasses()
    {
        using Project project = new(); project.Write("data/common/zrdr/note.zrd", "( NOTE ( 1 ) )"); project.Write("gamegen/main.gs", "Echo ok");
        var baseline = new SourceBuilder.Snapshot(project.Root);
        var expected = SourceBuilder.Plan(project.Root, null, null, false, Token, baseline);
        long first = baseline.Inventory.Used;
        var shared = new SourceBuilder.Snapshot(project.Root, inventoryLimit: first);
        Assert.Equal(expected.Select(p => p.Path), SourceBuilder.Plan(project.Root, null, null, false, Token, shared).Select(p => p.Path));
        Assert.Throws<InventoryCapacityException>(() => SourceBuilder.Plan(project.Root, null, null, false, Token, shared));
        var retry = new SourceBuilder.Snapshot(project.Root, inventoryLimit: 2 * first);
        SourceBuilder.Plan(project.Root, null, null, false, Token, retry);
        SourceBuilder.Plan(project.Root, null, null, false, Token, retry);
        Assert.Equal(2 * first, retry.Inventory.Used);
    }

    [Fact]
    public void BlenderOutboxesShareAllowanceAndPreserveOrderedFullAndRelativePaths()
    {
        using Project project = new();
        foreach (string id in new[] { "older", "newer" })
        {
            project.Write($"zstudio/export/{id}/manifest.json", new JsonObject
            {
                ["format"] = "zstudio-blender-checkout", ["version"] = 1, ["id"] = id,
                ["model"] = "data/model.gltf", ["created"] = id == "older" ? "2026-01-01T00:00:00Z" : "2026-02-01T00:00:00Z", ["files"] = new JsonArray()
            }.ToJsonString());
            project.Write($"zstudio/export/{id}/outbox/model.gltf", "{}");
        }
        long exact = 0;
        foreach (string id in new[] { "older", "newer" })
        {
            string folder = project.Full($"zstudio/export/{id}");
            exact += PathUnits(folder) + 256 + 8L * (folder.Length + "data/model.gltf".Length)
                + 256 + 8L * (folder.Length + 7) + PathUnits(Path.Combine(folder, "outbox", "model.gltf")) + 3 * 64;
        }
        var accepted = SourceBlender.CheckoutExports(project.Root, 100, Token, inventoryLimit: exact);
        Assert.Equal(["newer", "older"], accepted.Select(row => row.Checkout.Id));
        foreach (var row in accepted)
        {
            var export = Assert.Single(row.Exports);
            Assert.Equal("model.gltf", export.Relative);
            Assert.Equal(Path.Combine(row.Checkout.Outbox, "model.gltf"), export.Gltf);
            Assert.Single(SourceBlender.Exports(row.Checkout, Token));
        }
        Assert.Throws<InventoryCapacityException>(() => SourceBlender.CheckoutExports(project.Root, 100, Token, inventoryLimit: exact - 1));
    }

    [Fact]
    public void ReconstructionInventoryAlsoAdmitsPathsBeforeInputRows()
    {
        using Project project = new(); project.Write("data/input.zbd", "x");
        Assert.Throws<InventoryCapacityException>(() => SourceExtractor.Corpus(project.Full("data"), Token, inventoryLimit: 0));
        Assert.Equal("input.zbd", Assert.Single(SourceExtractor.Corpus(project.Full("data"), Token)).Relative);
    }

    [Fact]
    public void DestinationPackListingsShareTheirOwnAllowanceAcrossMissions()
    {
        using Project project = new();
        project.Write("m1/rtexture16.zbd", ""); project.Write("m2/rtexture16.zbd", "");
        long first = PathUnits(project.Full("m1/rtexture16.zbd"));
        long second = PathUnits(project.Full("m2/rtexture16.zbd"));
        var shared = new InventoryBudget(first + second - 1);
        Assert.Equal(["m1/rtexture16.zbd"], BuildProfiles.ShadowingPacks(project.Root, "m1", [], 100, Token, shared));
        Assert.Throws<InventoryCapacityException>(() => BuildProfiles.ShadowingPacks(project.Root, "m2", [], 100, Token, shared));
        Assert.Equal(["m2/rtexture16.zbd"], BuildProfiles.ShadowingPacks(project.Root, "m2", [], 100, Token, new(second)));
    }

    private sealed class Project : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-inventory-" + Guid.NewGuid().ToString("N"));
        internal Project() { Directory.CreateDirectory(Full("data")); Directory.CreateDirectory(Full("gamegen")); }
        internal string Full(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        internal void Write(string relative, string text) { string path = Full(relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, text, Encoding.Latin1); }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
