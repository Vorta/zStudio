using System.IO;
using System.Text.Json.Nodes;
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
        try { return GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes(file.Name, Recoil.Zbd.Core.Sources.SourceRead.All(path, MaximumCompareFileBytes, token), token: token), token); }
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
        RegisterJob(r, "world_compare", "Compare two RECOIL GameZ worlds (gamez.zbd) of version 15, or 13 for the 1998 demos, such as a retail world and one rebuilt from a source project or a demo world, in the visible Compare worlds window (Tools → Compare worlds…). Their node trees are merged by parent-child structure: children match by name, then repeated names by their whole contents, then structure, their children's names and nearest position, independent of serialized node slots. After pairing, child, world-cell and overflow lists are compared in encounter order, including repeated memberships, because they can change zone selection. Each pair is the same or changed (class, flags, zone, grid cell, transform, class data, model, number of children or traversal order); other nodes are only in one world. The result gives both versions, counts them and their differences, says whether the tree is truncated (it holds at most 500,000 rows, a node under several parents appearing under each, and 256 levels), and the names several nodes share whose whole-world lookup (the engine finds a name's highest slot first) finds another node in the rebuilt world; animations bind their roots and fall back to such lookups, but search their own subtrees first, so not every one changes behaviour. Indistinguishable copies (same parents, same contents all the way down) count as the same node. The world row also reports texture-directory entry counts, load states and next-variant targets; duplicate texture names pair by directory occurrence, while distinct names may reorder. Read rows with zstudio_world_compare_tree.",
            [P("retail", "string", "Full path of the retail (expected) world file.", true), P("rebuilt", "string", "Full path of the rebuilt (actual) world file.", true)], true, async (a, token) =>
        {
            // Arguments that cannot name a world leave no window behind.
            string retail = Text(a, "retail"), rebuilt = Text(a, "rebuilt");
            await Task.Run(() => { RequireCompareFile(retail, "retail"); RequireCompareFile(rebuilt, "rebuilt"); }, token);
            var view = await ShowCompareWindow().CompareAsync(Text(a, "retail"), Text(a, "rebuilt"), token);
            return Result(DescribeComparison(view));
        });
        Register(r, "world_compare_tree", "Read the merged node tree of the Compare worlds window: its roots (the world node with its members, then the nodes no parent holds) or a row's children, optionally filtered before paging. A row is a pair of nodes or a node only one world has, with both slots, its status, how many rows below differ, whether a whole-world lookup of its name finds another node in the rebuilt world, whether the tree leaves out some of its children (truncated), and its differences (at most 16, values shortened to 256 characters and 1,024 in all, of differenceCount). selected and row also give the window's property lines for both worlds, 32 at a time of propertyCount: propertyOffset pages those of row, or of the selected row when no row is given, up to nextPropertyOffset. It only reads the comparison, so it stays available while operations run: select shows a row's properties in the window, expand and collapse change only the window's tree, and these actions return busy while a dialog is open.", false,
            [P("context", "string", "Comparison context from zstudio_world_compare or a read; required for actions, row queries and property pages."),
                P("action", "string", "Tree action; default read. expand, collapse and select change only the Compare worlds window's presentation.", false, "read", "expand", "collapse", "select"),
                P("row", "string", "Opaque row ID: whose children to read, or the row to act on."),
                new("propertyOffset", "integer", "Zero-based offset into the property lines of row, or of the selected row when no row is given (also the selected row's when it is row); a read returns at most 32 and nextPropertyOffset gives the next. Default 0.", Minimum: 0, Maximum: int.MaxValue),
                P("differencesOnly", "boolean", "List only rows that differ, or hold differences below them. Independent of the window's checkbox; default false."),
                .. PageParameters.Select(p => p.Name == "query" ? P("query", "string", "Case-insensitive text: rows whose node name, or a name below them, contains it. Independent of the window's filter.") : p)], a =>
        {
            var view = compareWindow?.View ?? throw new StudioCommandException("not_ready", "No comparison is shown. Run zstudio_world_compare.");
            string action = Text(a, "action", "read");
            if ((action != "read" || a["row"] != null || a["propertyOffset"] != null || a["context"] != null) && Text(a, "context") != view.Context)
                throw new StudioCommandException("stale_context", "The comparison changed. Read its current roots.");
            var row = a["row"] == null ? null : view.Find(Text(a, "row")) ?? throw new StudioCommandException("stale_record", "Tree row unavailable.");
            if (action != "read")
            {
                if (row == null) throw new StudioCommandException("invalid_argument", "Supply the row to act on.");
                // Not while a dialog waits for the user (a modal window, or a message box or picker whatever window owns it).
                if (shutdownToken.IsCancellationRequested || GuiDialogOpen()) throw new StudioCommandException("busy", "A dialog is open. Retry after it closes.");
                if (action == "select") { for (var parent = row.Parent; parent != null; parent = parent.Parent) parent.IsExpanded = true; view.Select(row); }
                else row.IsExpanded = action == "expand";
            }
            return CompareTreePage(view, row, a, compareWindow!.Filter);
        });
    }

    /// <summary>
    /// A world_compare_tree read: the summary, the selected row and <paramref name="row"/> with a page of their property lines
    /// (from propertyOffset for <paramref name="row"/>, or for the selected row when no row is given), and a page of the row's
    /// children (or the roots).
    /// </summary>
    internal static StudioResult CompareTreePage(WorldCompareView view, WorldCompareRow? row, JsonObject a, (bool DifferencesOnly, string Query) filter)
    {
        bool differences = Flag(a, "differencesOnly");
        int propertyOffset = Int(a, "propertyOffset");
        var source = (row == null ? view.Roots : row.Children).Where(c => view.Passes(c, differences, ""));
        var page = Page(source, a, matches: (c, query) => view.Passes(c, false, query.Trim()), project: c => DescribeCompareRow(view, c));
        var selected = view.Selected;
        return Result(new { DescribeComparison(view).context, summary = DescribeComparison(view),
            selected = selected == null ? null : DescribeCompareRow(view, selected, properties: true, row == null || row == selected ? propertyOffset : 0),
            windowFilter = new { differencesOnly = filter.DifferencesOnly, query = Bounded(filter.Query, 256) }, row = row == null ? null : DescribeCompareRow(view, row, properties: true, propertyOffset), children = page.Data });
    }

    private static Summary DescribeComparison(WorldCompareView view)
    {
        var c = view.Comparison;
        return new(view.Context, view.RetailPath, view.RebuiltPath, view.RetailVersion, view.RebuiltVersion, view.RetailNodes, view.RebuiltNodes, c.Counts[WorldComparisonStatus.Same], c.Counts[WorldComparisonStatus.Changed],
            c.Counts[WorldComparisonStatus.OnlyExpected], c.Counts[WorldComparisonStatus.OnlyActual], c.DifferenceCount, c.Bindings.Count, c.Bindings.Count(b => !b.Same), c.Truncated, c.PairingTruncated,
            c.ApproximatePairing, c.UncheckedBindings);
    }
    private sealed record Summary(string context, string retail, string rebuilt, uint retailVersion, uint rebuiltVersion, int retailNodes, int rebuiltNodes, int same, int changed, int onlyRetail, int onlyRebuilt,
        long differences, int sharedNames, int wholeWorldLookupsDiffering, bool truncated, bool pairingTruncated, bool approximatePairing, int wholeWorldLookupsUnchecked);

    /// <summary>
    /// The most characters of difference fields and values a tree row returns: with its name and path, a full page of rows
    /// whose every character is escaped stays well within the response limit (about 2.5 of its 4 MiB with the two rows'
    /// pages of <see cref="MaximumPropertyLines"/> property lines, under 2.9 with file paths of the longest Windows allows).
    /// </summary>
    private const int MaximumRowDifferenceText = 1024;
    /// <summary>The most property lines a read returns for a row; propertyOffset reads the rest, so every line the window shows can be read.</summary>
    private const int MaximumPropertyLines = 32;

    /// <param name="properties">Whether to add the window's property lines (both worlds side by side), as for the selected row.</param>
    /// <param name="propertyOffset">The first of those lines to return; at most <see cref="MaximumPropertyLines"/> follow.</param>
    private static object DescribeCompareRow(WorldCompareView view, WorldCompareRow row, bool properties = false, int propertyOffset = 0)
    {
        var node = row.Source;
        var details = properties ? view.Details(row) : null;
        var lines = details?.Skip(propertyOffset).Take(MaximumPropertyLines)
            .Select(d => new { field = WorldCompareView.Short(d.Field, 64), retail = WorldCompareView.Short(d.Retail, 256), rebuilt = WorldCompareView.Short(d.Rebuilt, 256), differs = d.Differs }).ToArray();
        List<object> differences = []; int text = 0;
        foreach (var d in node.Differences.Take(16))
        {
            string field = WorldCompareView.Short(d.Field, 64), retail = WorldCompareView.Short(d.Expected, 256), rebuilt = WorldCompareView.Short(d.Actual, 256);
            if ((text += field.Length + retail.Length + rebuilt.Length) > MaximumRowDifferenceText && differences.Count > 0) break;
            differences.Add(new { field, retail, rebuilt });
        }
        return new
        {
            row = row.Id, name = WorldCompareView.Short(node.Name, 256), path = WorldCompareView.Short(node.Path, 512), status = WorldCompareView.Status(node),
            retailSlot = view.RetailSlot(node), rebuiltSlot = view.RebuiltSlot(node), retailClass = node.Expected?.Class.ToString(), rebuiltClass = node.Actual?.Class.ToString(),
            childCount = node.Children.Count, truncated = node.Truncated, differingBelow = view.NotableBelow(node), wholeWorldLookupDiffers = node.BindsElsewhere,
            differenceCount = node.DifferenceCount, differences,
            expanded = row.IsExpanded, selected = row.IsSelected,
            properties = lines, propertyCount = details?.Count, propertyOffset = details == null ? null : (int?)propertyOffset,
            nextPropertyOffset = details != null && (long)propertyOffset + lines!.Length < details.Count ? propertyOffset + lines.Length : (int?)null,
        };
    }
}
