using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Worlds;
using Recoil.Zbd.Desktop;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>
/// A world_compare_tree page at the limits of everything a row may hold, in characters JSON escapes, stays well within the
/// 4 MiB response limit: about 2.85 million characters with file paths of the longest Windows allows, against 4.14 million
/// (past the limit) when a row's difference fields were not counted with its values.
/// </summary>
public sealed class CompareTreePageBoundsTests
{
    [Fact]
    public void AFullPageOfTheLongestRowsKeepsAMarginBelowTheResponseLimit()
    {
        const int MaximumPage = 3 * 1024 * 1024;
        static string Text(int length) => new('<', length);
        Dictionary<WorldNode, int> slots = new(ReferenceEqualityComparer.Instance);
        WorldNode Node(string name) { WorldNode node = new(name, WorldNodeClass.Object3D); slots[node] = slots.Count; return node; }
        WorldComparisonNode Pair(WorldComparisonNode? parent, string name, int differences, int value)
        {
            WorldComparisonNode node = new(parent, name, name, Node(name), Node(name))
            {
                // Sixteen and more differences whose fields and values all escape.
                Described = [.. Enumerable.Range(0, differences).Select(_ => (Text(64), Text(value), Text(value)))],
                DifferenceCount = differences,
            };
            parent?.Children.Add(node);
            return node;
        }
        // Rows below two levels of the longest names (their paths run past 512 characters), each with 16 differences of short
        // values and the longest fields; the parent row and the selected row show 32 property lines of the longest values.
        var world = Pair(null, "world", 0, 0);
        var page = Pair(Pair(world, Text(300), 64, 600), Text(300), 64, 600);
        for (int i = 0; i < 200; i++) Pair(page, Text(300), 16, 32);
        var comparison = new WorldComparison
        {
            Roots = [world], ExpectedSlots = slots, ActualSlots = slots, Differences = [], Bindings = [], Counterparts = new Dictionary<WorldNode, WorldNode>(),
            Counts = Enum.GetValues<WorldComparisonStatus>().ToDictionary(s => s, _ => 0),
        };
        // Both worlds named by paths of the longest Windows allows.
        string path = @"C:\" + new string('é', 32700) + @"\gamez.zbd";
        var view = new WorldCompareView(path, path, comparison, slots.Count, slots.Count);
        var level = view.Roots[0].Children[0]; var row = level.Children[0];
        view.Select(level);
        string json = MainWindow.CompareTreePage(view, row, new JsonObject { ["context"] = view.Context, ["row"] = row.Id, ["limit"] = 200 }, (false, Text(300))).Data.ToJsonString();
        var items = JsonNode.Parse(json)!["children"]!["items"]!.AsArray();
        Assert.Equal(200, items.Count);
        Assert.Equal(32, JsonNode.Parse(json)!["selected"]!["properties"]!.AsArray().Count);
        Assert.True(json.Length <= MaximumPage, $"{json.Length:N0} characters");
        // Each row still lists the differences that fit: their fields and values together are at most 1,024 characters.
        Assert.All(items, item => Assert.InRange(item!["differences"]!.AsArray().Sum(d => d!["field"]!.GetValue<string>().Length + d["retail"]!.GetValue<string>().Length + d["rebuilt"]!.GetValue<string>().Length), 1, 1024));
    }
}
