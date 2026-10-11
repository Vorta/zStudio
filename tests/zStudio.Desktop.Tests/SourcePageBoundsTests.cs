using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class SourcePageBoundsTests
{
    [Fact]
    public void TerrainRecipeListingReadsOnlyReturnedRowsAndRetainsCounts()
    {
        string path = new('é', 32000);
        var paths = Enumerable.Range(0, 64).Select(i => path + i).ToArray();
        List<string> found = []; int offset = 0, reads = 0;
        do
        {
            int before = reads;
            var page = MainWindow.TerrainRecipePage(paths, offset, _ => { reads++; return (256, 4096); }, _ => 17, TestContext.Current.CancellationToken).Data;
            var rows = page["items"]!.AsArray();
            Assert.Equal(rows.Count, reads - before);
            Assert.InRange(Encoding.UTF8.GetByteCount(page.ToJsonString()), 1, 1024 * 1024);
            foreach (var row in rows)
            {
                found.Add(row!["path"]!.GetValue<string>());
                Assert.Equal(4096, row["regions"]!.GetValue<int>());
                Assert.Equal(17, row["pieces"]!.GetValue<int>());
            }
            if (page["nextOffset"] == null) break;
            offset = page["nextOffset"]!.GetValue<int>();
        } while (true);
        Assert.Equal(paths, found);
    }
    [Theory]
    [InlineData("")]
    [InlineData("m")]
    public void SourceOutputPagesBoundFullInputPreviews(string query)
    {
        string large = new('é', 32000);
        var plans = Enumerable.Range(1, 200).Select(i => new SourceOutputPlan($"m{i}/gamez.zbd", "world", Enumerable.Repeat(large, 16).ToArray())).ToArray();
        int offset = 0; List<string> found = [];
        do
        {
            var page = MainWindow.SourceOutputPage(plans, new() { ["limit"] = 200, ["offset"] = offset, ["query"] = query }).Data;
            Assert.InRange(Encoding.UTF8.GetByteCount(page.ToJsonString()), 1, 1024 * 1024);
            found.AddRange(page["items"]!.AsArray().Select(i => i!["path"]!.GetValue<string>()));
            if (page["nextOffset"] == null) break;
            offset = page["nextOffset"]!.GetValue<int>();
        } while (true);
        Assert.Equal(plans.Select(p => p.Path), found);
    }

    [Fact]
    public void FullExportReportBoundsEscapedDiagnosticsAndLookupChanges()
    {
        string text = new('é', 32767);
        var outputs = Enumerable.Range(1, 256).Select(i => new SourceExportResult($"m{i}/gamez.zbd", "world", "failed", 0, 0, Enumerable.Repeat(text, 16).ToArray(), text)).ToArray();
        var lookup = new SourceLookup("m999", "animation", text, text, 100, 3, text);
        var report = new SourceExportReport("C:/test", outputs)
        {
            Notes = Enumerable.Repeat(text, 64).ToArray(), Lookups = Enumerable.Repeat(lookup, 256).ToArray(),
            LookupChanges = Enumerable.Repeat(new SourceLookupChange(lookup, lookup), 64).ToArray()
        };
        long before = GC.GetAllocatedBytesForCurrentThread();
        var projection = MainWindow.ExportResult("C:/test", report);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4 * 1024 * 1024);
        var json = System.Text.Json.JsonSerializer.SerializeToNode(projection)!;
        Assert.InRange(Encoding.UTF8.GetByteCount(json.ToJsonString()), 1, 4 * 1024 * 1024 - 4096);
        Assert.Equal(256, json["failed"]!.GetValue<int>());
        Assert.All(json["outputs"]!.AsArray(), o => Assert.True(o!["warningsTruncated"]!.GetValue<bool>()));
    }
    [Fact]
    public void SourceInstructionProjectionBoundsLargeTokensBeforeMakingJson()
    {
        string huge = new('é', 1024 * 1024);
        SourceInstruction instruction = new(huge, 7, "LightSetColor", Enumerable.Repeat(huge, 64).ToArray(), []);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var projection = MainWindow.SourceInstructionProjection(instruction, 3);
        var tokens = MainWindow.SourceInstructionTokens(instruction, 1);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64 * 1024);
        var json = System.Text.Json.JsonSerializer.SerializeToNode(projection)!;
        Assert.True(json["truncated"]!.GetValue<bool>());
        Assert.Equal(3, json["runs"]!.GetValue<int>());
        Assert.InRange(Encoding.UTF8.GetByteCount(json.ToJsonString()), 1, 20000);
        Assert.Equal(16, tokens.Length);
        Assert.All(tokens, t => Assert.InRange(t.Length, 1, 129));
        Assert.Equal(huge, instruction.Tokens[0]);
    }
    [Theory]
    [InlineData("")]
    [InlineData("é")]
    public void LongModelPathsShortenThePageBeforeProjectionAndRemainUsable(string query)
    {
        string path = new('é', 32000);
        var source = Enumerable.Range(0, 200).Select(i => path + i).ToArray();
        int offset = 0, projected = 0; List<string> found = [];
        do
        {
            int before = projected;
            var page = MainWindow.Page(source, new JsonObject { ["offset"] = offset, ["limit"] = 200, ["query"] = query }, p => p,
                p => { projected++; return new { path = p, folder = p }; }, maximumRowBytes: p => 128 + 12L * p.Length).Data;
            var items = page["items"]!.AsArray();
            Assert.Equal(items.Count, projected - before);
            Assert.InRange(Encoding.UTF8.GetByteCount(page.ToJsonString()), 1, 1024 * 1024);
            found.AddRange(items.Select(i => i!["path"]!.GetValue<string>()));
            if (page["nextOffset"] == null) break;
            offset = page["nextOffset"]!.GetValue<int>();
        } while (true);
        Assert.Equal(source, found);
    }

    [Fact]
    public void FullCheckoutPagesBoundNestedEscapedPathsAndRetainExactIdentities()
    {
        string part = new('é', 32000);
        List<(BlenderCheckout Checkout, IReadOnlyList<BlenderExport> Exports)> rows = [];
        for (int i = 0; i < 32; i++)
        {
            string folder = @"C:\" + part;
            var files = Enumerable.Range(0, 64).Select(n => new BlenderCheckoutFile(part + n, part + n, new('a', 64))).ToArray();
            var checkout = new BlenderCheckout("c" + i, folder, part + "/model.gltf", DateTime.UnixEpoch, files);
            var exports = Enumerable.Range(0, 16).Select(n => new BlenderExport("", part + n + ".gltf", DateTime.UnixEpoch, 1)).ToArray();
            rows.Add((checkout, exports));
        }
        int offset = 0; List<string> ids = [];
        do
        {
            var page = MainWindow.CheckoutPage(rows, offset).Data;
            Assert.InRange(Encoding.UTF8.GetByteCount(page.ToJsonString()), 1, 3 * 1024 * 1024);
            foreach (var row in page["checkouts"]!.AsArray())
            {
                ids.Add(row!["id"]!.GetValue<string>());
                Assert.Equal(64, row["fileCount"]!.GetValue<int>());
                Assert.Equal(16, row["exportCount"]!.GetValue<int>());
                Assert.Equal(rows[int.Parse(ids[^1].AsSpan(1))].Checkout.Model, row["model"]!.GetValue<string>());
                Assert.All(row["exports"]!.AsArray(), e => Assert.Contains(e!["path"]!.GetValue<string>(), rows[0].Exports.Select(x => x.Relative)));
            }
            if (page["nextOffset"] == null) break;
            offset = page["nextOffset"]!.GetValue<int>();
        } while (true);
        Assert.Equal(rows.Select(r => r.Checkout.Id), ids);
    }
}
