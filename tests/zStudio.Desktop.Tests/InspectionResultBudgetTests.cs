using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class InspectionResultBudgetTests
{
    [Fact]
    public void OrdinaryRowsKeepTheRequestedMaximumAndExactOrder()
    {
        var rows = Enumerable.Range(0, 201).Select(i => new MissionActor(i, i, "actor", "short source")).ToArray();
        var page = MainWindow.Page(rows, new() { ["limit"] = 200 }, maximumRowBytes: InspectionResultBudget.Actor).Data;
        Assert.Equal(200, page["items"]!.AsArray().Count);
        Assert.Equal(200, page["nextOffset"]!.GetValue<int>());
        Assert.Equal(Enumerable.Range(0, 200), page["items"]!.AsArray().Select(r => r!["Root"]!.GetValue<int>()));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(200)]
    public void CompleteNestedActorIdentitiesFitTheChargedPageAndAllRemainDiscoverable(int limit)
    {
        string path = "C:/" + new string('&', 30_000) + "/resources.zbd";
        var rows = Enumerable.Range(0, 12).Select(i => new MissionActor(i, i, "actor", path,
            new(0, "ERFPG_AMMO", 1, 1, Vector3.Zero, Vector3.Zero, 0, new(path, 0, "puppies.zrd", i)),
            new(path, 1, "vehicle.zrd", i))).ToArray();
        List<int> seen = []; int? offset = 0;
        do
        {
            var page = MainWindow.Page(rows, new() { ["offset"] = offset, ["limit"] = limit },
                maximumRowBytes: InspectionResultBudget.Actor).Data;
            Assert.True(page.ToJsonString().Length < InspectionResultBudget.PageBytes);
            foreach (var row in page["items"]!.AsArray())
            {
                Assert.Equal(path, row!["Pickup"]!["Source"]!["ArchivePath"]!.GetValue<string>());
                Assert.Equal(path, row["CoordinateSource"]!["ArchivePath"]!.GetValue<string>());
                seen.Add(row["Root"]!.GetValue<int>());
            }
            offset = page["nextOffset"]?.GetValue<int>();
        } while (offset != null);
        Assert.Equal(Enumerable.Range(0, rows.Length), seen);
        Assert.Empty(MainWindow.Page(rows, new() { ["offset"] = rows.Length }, maximumRowBytes: InspectionResultBudget.Actor).Data["items"]!.AsArray());
    }

    [Fact]
    public void OversizedSingleRowIsRefusedBeforeProjectionAndANewRequestStillWorks()
    {
        int projected = 0;
        Assert.Equal("too_large", Assert.Throws<StudioCommandException>(() => MainWindow.Page(new[] { new string('&', 200_000) }, new(),
            project: p => { projected++; return p; }, maximumRowBytes: InspectionResultBudget.Text)).Code);
        Assert.Equal(0, projected);
        Assert.Single(MainWindow.Page(new[] { "short" }, new(), maximumRowBytes: InspectionResultBudget.Text).Data["items"]!.AsArray());
    }

    [Fact]
    public void PickupCostIncludesBothPathsAndSearchKeepsCrossFieldMatchingWithinItsAllowance()
    {
        string path = new('&', 6000);
        var record = new PickupPlacementRecord(new(path, 0, "puppies.zrd", 1), "ERFPG_AMMO", Vector3.Zero, Vector3.Zero, []);
        string scope = "Applies to: Easy, Medium, Hard";
        var value = new { source = record.Source, record.Type, position = Vector3.Zero, rotationRadians = Vector3.Zero, record.OriginalPosition, scope, target = path };
        var json = JsonSerializer.Serialize(value, new JsonSerializerOptions { IncludeFields = true });
        Assert.True(json.Length < InspectionResultBudget.Pickup(record, path, scope));
        InspectionResultBudget.Search search = new();
        Assert.True(search.Pickup(record, path, "ammo PUPPIES"));
        Assert.False(search.Pickup(record, path, "not present"));
        var excessive = new PickupPlacementRecord(new("short", 0, "puppies.zrd", 0), "ERFPG_AMMO", Vector3.Zero, Vector3.Zero, []);
        Assert.Equal("too_large", Assert.Throws<StudioCommandException>(() => new InspectionResultBudget.Search(64).Pickup(excessive, "short", "a")).Code);
        Assert.True(new InspectionResultBudget.Search().Pickup(excessive, "short", "short"));
    }
}
