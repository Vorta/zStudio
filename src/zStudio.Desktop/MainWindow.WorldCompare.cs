using System.IO;
using System.Windows;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    /// <summary>The largest world file a comparison reads.</summary>
    private const long MaximumCompareFileBytes = 256L * 1024 * 1024;
    private WorldCompareWindow? compareWindow;

    private void CompareWorldsClick(object sender, RoutedEventArgs e) => ShowCompareWindow();

    /// <summary>Shows the Compare worlds window (one, owned by this window), with the files last compared.</summary>
    internal WorldCompareWindow ShowCompareWindow()
    {
        if (compareWindow == null)
        {
            var settings = ViewModel.Settings;
            compareWindow = new(this, settings.CompareRetail, settings.CompareRebuilt, CompareWorldFilesAsync);
            compareWindow.Compared += (retail, rebuilt) => { settings.CompareRetail = retail; settings.CompareRebuilt = rebuilt; try { settings.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } };
            compareWindow.Closed += (_, _) => compareWindow = null;
        }
        compareWindow.Show(); compareWindow.Activate();
        return compareWindow;
    }

    /// <summary>Reads both worlds and compares them off the UI thread.</summary>
    private static Task<WorldCompareView> CompareWorldFilesAsync(string retail, string rebuilt, CancellationToken token) => Task.Run(() =>
    {
        var expected = ReadCompareWorld(retail, "retail", token); var actual = ReadCompareWorld(rebuilt, "rebuilt", token);
        var comparison = WorldComparer.CompareTree(expected, actual, token: token);
        return new WorldCompareView(retail, rebuilt, comparison, expected.Nodes.Count, actual.Nodes.Count) { RetailVersion = expected.SourceVersion, RebuiltVersion = actual.SourceVersion };
    }, token);

    private static GameZWorld ReadCompareWorld(string path, string which, CancellationToken token)
    {
        var file = RequireCompareFile(path, which);
        try { return GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes(file.Name, File.ReadAllBytes(path), token: token), token); }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException or UnauthorizedAccessException)
        { throw new StudioCommandException("invalid_data", $"The {which} world {file.Name} cannot be read as a GameZ world: {ex.Message}"); }
    }

    /// <summary>A world file to compare: a full path to an existing file of at most <see cref="MaximumCompareFileBytes"/>.</summary>
    private static FileInfo RequireCompareFile(string path, string which)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new StudioCommandException("invalid_argument", $"Give the {which} world as a full path.");
        if (Directory.Exists(path)) throw new StudioCommandException("invalid_argument", $"The {which} world {path} is a folder; give its gamez.zbd.");
        FileInfo file = new(path);
        if (!file.Exists) throw new StudioCommandException("not_found", $"The {which} world {path} does not exist.");
        if (file.Length > MaximumCompareFileBytes) throw new StudioCommandException("invalid_argument", $"The {which} world is larger than {MaximumCompareFileBytes / (1024 * 1024)} MB.");
        return file;
    }

    private void RegisterWorldCompareCommands(StudioCommands r)
    {
        RegisterJob(r, "world_compare", "Compare two RECOIL GameZ worlds (gamez.zbd) of version 15, or 13 for the 1998 demos, such as a retail world and one rebuilt from a source project or a demo world, in the visible Compare worlds window (Tools → Compare worlds…). Their node trees are merged by parent-child structure: children match by name, then repeated names by structure, their children's names and position, independent of node order and slots. Each pair is the same or changed (class, flags, zone, grid cell, transform, class data, model, number of children); other nodes are only in one world. The result gives both versions, counts them, and the names several nodes share whose whole-world lookup (the engine finds a name's highest slot first) finds another node in the rebuilt world; animations bind their roots and fall back to such lookups, but search their own subtrees first, so not every one changes behaviour. Indistinguishable copies (same parents, same contents all the way down) count as the same node. Read rows with zstudio_world_compare_tree.",
            [P("retail", "string", "Full path of the retail (expected) world file.", true), P("rebuilt", "string", "Full path of the rebuilt (actual) world file.", true)], true, async (a, token) =>
        {
            // Arguments that cannot name a world leave no window behind.
            string retail = Text(a, "retail"), rebuilt = Text(a, "rebuilt");
            await Task.Run(() => { RequireCompareFile(retail, "retail"); RequireCompareFile(rebuilt, "rebuilt"); }, token);
            var view = await ShowCompareWindow().CompareAsync(Text(a, "retail"), Text(a, "rebuilt"), token);
            return Result(DescribeComparison(view));
        });
        Register(r, "world_compare_tree", "Read the merged node tree of the Compare worlds window: its roots (the world node with its members, then the nodes no parent holds) or a row's children, optionally filtered before paging. A row is a pair of nodes or a node only one world has, with both slots, its status, how many rows below differ, whether a whole-world lookup of its name finds another node in the rebuilt world, and its differences (at most 16, values shortened). select shows a row's properties in the window; expand and collapse change only the window's tree.", true,
            [P("context", "string", "Comparison context from zstudio_world_compare or a read; required for actions and row queries."),
                P("action", "string", "Tree action; default read.", false, "read", "expand", "collapse", "select"),
                P("row", "string", "Opaque row ID: whose children to read, or the row to act on."),
                P("differencesOnly", "boolean", "List only rows that differ, or hold differences below them. Independent of the window's checkbox; default false."),
                .. PageParameters.Select(p => p.Name == "query" ? P("query", "string", "Case-insensitive text: rows whose node name, or a name below them, contains it. Independent of the window's filter.") : p)], a =>
        {
            var view = compareWindow?.View ?? throw new StudioCommandException("not_ready", "No comparison is shown. Run zstudio_world_compare.");
            string action = Text(a, "action", "read");
            if ((action != "read" || a["row"] != null || a["context"] != null) && Text(a, "context") != view.Context)
                throw new StudioCommandException("stale_context", "The comparison changed. Read its current roots.");
            var row = a["row"] == null ? null : view.Find(Text(a, "row")) ?? throw new StudioCommandException("stale_record", "Tree row unavailable.");
            if (action != "read")
            {
                if (row == null) throw new StudioCommandException("invalid_argument", "Supply the row to act on.");
                if (action == "select") { for (var parent = row.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true; view.Select(row); }
                else row.IsExpanded = action == "expand";
            }
            bool differences = Flag(a, "differencesOnly");
            var source = (row == null ? view.Roots : row.Children).Where(c => view.Passes(c, differences, ""));
            var page = Page(source, a, matches: (c, query) => view.Passes(c, false, query.Trim()), project: c => DescribeCompareRow(view, c));
            var filter = compareWindow!.Filter;
            return Result(new { DescribeComparison(view).context, summary = DescribeComparison(view), selected = view.Selected == null ? null : DescribeCompareRow(view, view.Selected, properties: true),
                windowFilter = new { differencesOnly = filter.DifferencesOnly, query = Bounded(filter.Query, 256) }, row = row == null ? null : DescribeCompareRow(view, row, properties: true), children = page.Data });
        });
    }

    private static Summary DescribeComparison(WorldCompareView view)
    {
        var c = view.Comparison;
        return new(view.Context, view.RetailPath, view.RebuiltPath, view.RetailVersion, view.RebuiltVersion, view.RetailNodes, view.RebuiltNodes, c.Counts[WorldComparisonStatus.Same], c.Counts[WorldComparisonStatus.Changed],
            c.Counts[WorldComparisonStatus.OnlyExpected], c.Counts[WorldComparisonStatus.OnlyActual], c.Differences.Count, c.Bindings.Count, c.Bindings.Count(b => !b.Same), c.Truncated);
    }
    private sealed record Summary(string context, string retail, string rebuilt, uint retailVersion, uint rebuiltVersion, int retailNodes, int rebuiltNodes, int same, int changed, int onlyRetail, int onlyRebuilt,
        int differences, int sharedNames, int wholeWorldLookupsDiffering, bool truncated);

    /// <param name="properties">Whether to add the window's property lines (both worlds side by side), as for the selected row.</param>
    private static object DescribeCompareRow(WorldCompareView view, WorldCompareRow row, bool properties = false)
    {
        var node = row.Source;
        var details = properties ? view.Details(row) : null;
        return new
        {
            row = row.Id, name = WorldCompareView.Short(node.Name, 256), path = WorldCompareView.Short(node.Path, 512), status = WorldCompareView.Status(node),
            retailSlot = view.RetailSlot(node), rebuiltSlot = view.RebuiltSlot(node), retailClass = node.Expected?.Class.ToString(), rebuiltClass = node.Actual?.Class.ToString(),
            childCount = node.Children.Count, differingBelow = view.NotableBelow(node), wholeWorldLookupDiffers = node.BindsElsewhere,
            differenceCount = node.Differences.Count,
            differences = node.Differences.Take(16).Select(d => new { field = d.Field, retail = WorldCompareView.Short(d.Expected, 256), rebuilt = WorldCompareView.Short(d.Actual, 256) }).ToArray(),
            expanded = row.IsExpanded, selected = row.IsSelected,
            properties = details?.Take(32).Select(d => new { field = WorldCompareView.Short(d.Field, 64), retail = WorldCompareView.Short(d.Retail, 256), rebuilt = WorldCompareView.Short(d.Rebuilt, 256), differs = d.Differs }).ToArray(),
            propertyCount = details?.Count,
        };
    }
}
