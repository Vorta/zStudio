using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core.Worlds;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Compare worlds through MCP and the window: the merged tree, its filters, selection and refusals.</summary>
internal static class WorldCompareMcpChecks
{
    private sealed class Disk(string root) : IProjectFiles
    {
        public bool Exists(string relative) => File.Exists(Path.Combine(root, relative));
        public byte[] Read(string relative, CancellationToken token) => Read(relative, token, ProjectReadLimits.Document);
        public byte[] Read(string relative, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); return Recoil.Zbd.Core.Sources.SourceRead.All(Path.Combine(root, relative), limits, token); }
    }

    internal static async Task Run()
    {
        using var fixture = new SourceWorldFixture();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90)); var token = deadline.Token;
        // The retail world: m1 as the fixture builds it. The rebuilt one: m1 with a database that adds two copies of a part
        // (a crate with a lid, and a post), and the ground in another zone.
        string retail = fixture.Path("retail.zbd"), rebuilt = fixture.Path("rebuilt.zbd");
        await File.WriteAllBytesAsync(retail, GameZWriter.Write(new WorldAssembler(new Disk(fixture.Project), token).Assemble("m1.gs"), token), token);
        fixture.WritePartDatabase();
        var changed = new WorldAssembler(new Disk(fixture.Project), token).Assemble("m1.gs");
        changed.Nodes.Single(n => n.Name == "ground").Zone = 7;
        await File.WriteAllBytesAsync(rebuilt, GameZWriter.Write(changed, token), token);

        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);

            // Nothing is compared yet; refusals name the problem. Paths that cannot name a world leave no window behind; a
            // file that is not a world is read in the window, which shows why.
            Assert.Contains("not_ready", (await Call("world_compare_tree", new(), error: true)).GetValue<string>());
            Assert.Equal("invalid_argument", (await Job("world_compare", new() { ["retail"] = "retail.zbd", ["rebuilt"] = rebuilt }, "failed"))["code"]!.GetValue<string>());
            Assert.Equal("not_found", (await Job("world_compare", new() { ["retail"] = fixture.Path("missing.zbd"), ["rebuilt"] = rebuilt }, "failed"))["code"]!.GetValue<string>());
            Assert.Null(typeof(MainWindow).GetField("compareWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main));
            Assert.Equal("invalid_data", (await Job("world_compare", new() { ["retail"] = fixture.Path("gamegen/m1.gs"), ["rebuilt"] = rebuilt }, "failed"))["code"]!.GetValue<string>());

            // The comparison opens the visible window and counts the merged tree.
            var summary = await Job("world_compare", new() { ["retail"] = retail, ["rebuilt"] = rebuilt });
            var window = (WorldCompareWindow)typeof(MainWindow).GetField("compareWindow", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
            Assert.True(window.IsVisible); Assert.Same(main, window.Owner);
            Assert.Equal(window.View!.Context, summary["context"]!.GetValue<string>());
            // The ground, and the world, whose grid cells list the added objects.
            Assert.Equal(2, summary["changed"]!.GetValue<int>());
            Assert.Equal(6, summary["onlyRebuilt"]!.GetValue<int>());
            Assert.Equal(0, summary["onlyRetail"]!.GetValue<int>());
            string context = summary["context"]!.GetValue<string>();

            // Roots: the world, whose cells and rows below differ; only differing rows when asked.
            var roots = await Call("world_compare_tree", new() { ["context"] = context });
            var world = roots["children"]!["items"]!.AsArray().Single(r => r!["retailClass"]!.GetValue<string>() == "World")!;
            Assert.Equal("changed", world["status"]!.GetValue<string>());
            Assert.All(world["differences"]!.AsArray(), d => Assert.StartsWith("world.area", d!["field"]!.GetValue<string>()));
            Assert.Equal(7, world["differingBelow"]!.GetValue<int>());
            var below = await Call("world_compare_tree", new() { ["context"] = context, ["row"] = world["row"]!.GetValue<string>(), ["differencesOnly"] = true });
            var rows = below["children"]!["items"]!.AsArray().Select(r => r!).ToList();
            var ground = rows.Single(r => r["name"]!.GetValue<string>() == "ground");
            Assert.Equal("changed", ground["status"]!.GetValue<string>());
            Assert.Equal("zone", ground["differences"]![0]!["field"]!.GetValue<string>());
            Assert.Equal(["crate", "crate", "post", "post"], rows.Where(r => r["status"]!.GetValue<string>() == "only rebuilt").Select(r => r["name"]!.GetValue<string>()).Order());
            // A query keeps rows whose names, or names below them, match.
            var lids = await Call("world_compare_tree", new() { ["context"] = context, ["row"] = world["row"]!.GetValue<string>(), ["query"] = "lid" });
            Assert.Equal(["crate", "crate"], lids["children"]!["items"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()));

            // Selecting a row shows its properties in the window, retail and rebuilt side by side.
            var selected = await Call("world_compare_tree", new() { ["context"] = context, ["action"] = "select", ["row"] = ground["row"]!.GetValue<string>() });
            Assert.Equal(ground["row"]!.GetValue<string>(), selected["selected"]!["row"]!.GetValue<string>());
            Assert.True(selected["selected"]!["selected"]!.GetValue<bool>());
            Assert.Equal("ground", window.View.Selected!.Name);
            var zone = window.View.Details(window.View.Selected).Single(d => d.Field == "Zone");
            Assert.True(zone.Differs); Assert.Equal("7", zone.Rebuilt);
            // Slots are shown, never differences: node order does not change how the world behaves.
            var slot = window.View.Details(window.View.Selected).Single(d => d.Field == "Slot");
            Assert.NotEqual(slot.Retail, slot.Rebuilt); Assert.False(slot.Differs);
            // MCP reads the same property lines as the window shows for the selected row.
            var properties = selected["selected"]!["properties"]!.AsArray();
            Assert.Equal(window.View.Details(window.View.Selected).Select(d => d.Field).Take(32), properties.Select(p => p!["field"]!.GetValue<string>()));
            var zoneLine = properties.Single(p => p!["field"]!.GetValue<string>() == "Zone")!;
            Assert.True(zoneLine["differs"]!.GetValue<bool>()); Assert.Equal("7", zoneLine["rebuilt"]!.GetValue<string>());
            Assert.True(window.View.Selected.Parent!.IsExpanded);

            // The window's filter is its own: MCP reads report it without changing it.
            ((CheckBox)Find(window, c => c is CheckBox { Content: "Differences only" })).IsChecked = true;
            var filtered = await Call("world_compare_tree", new() { ["context"] = context });
            Assert.True(filtered["windowFilter"]!["differencesOnly"]!.GetValue<bool>());
            Assert.All(window.View.VisibleRoots, r => Assert.True(WorldCompareView.Notable(r.Source) || window.View.NotableBelow(r.Source) > 0));
            // Filtering keeps the selected row and its properties.
            Assert.Equal("ground", window.View.Selected?.Name);
            // A name filter in the window opens the rows that hold its matches.
            var nameFilter = (TextBox)Find(window, c => c is TextBox box && System.Windows.Automation.AutomationProperties.GetName(box) == "Filter nodes by name");
            nameFilter.Text = "lid";
            var crates = window.View.VisibleRoots.Single().Visible;
            Assert.Equal(["crate", "crate"], crates.Select(r => r.Name));
            Assert.All(crates, r => Assert.True(r.IsExpanded && r.Visible.Single().Name == "lid"));
            nameFilter.Text = "";
            Capture(window, Path.Combine(Path.GetTempPath(), "zstudio-compare-worlds.png"));

            // A new comparison replaces the rows: the old context is stale.
            await Job("world_compare", new() { ["retail"] = retail, ["rebuilt"] = retail });
            Assert.Contains("stale_context", (await Call("world_compare_tree", new() { ["context"] = context, ["row"] = world["row"]!.GetValue<string>() }, error: true)).GetValue<string>());
            var same = (await Call("world_compare_tree", new()))["summary"]!;
            Assert.Equal((0, 0, 0), (same["changed"]!.GetValue<int>(), same["onlyRetail"]!.GetValue<int>(), same["onlyRebuilt"]!.GetValue<int>()));
            Assert.Equal(retail, main.ViewModel.Settings.CompareRebuilt);

            // A 1998 demo world (version 13) compares with a release world (version 15): the same world in the demos' layout
            // is the same tree, and both versions are reported.
            string demoFolder = Path.Combine(Path.GetTempPath(), "zstudio-demo-" + Guid.NewGuid().ToString("N")), demo = Path.Combine(demoFolder, "m1", "gamez.zbd");
            Directory.CreateDirectory(Path.GetDirectoryName(demo)!);
            try
            {
                await File.WriteAllBytesAsync(demo, DemoWorldFixture.FromVersion15(await File.ReadAllBytesAsync(retail, token)), token);
                var versions = await Job("world_compare", new() { ["retail"] = retail, ["rebuilt"] = demo });
                Assert.Equal((15u, 13u), (versions["retailVersion"]!.GetValue<uint>(), versions["rebuiltVersion"]!.GetValue<uint>()));
                Assert.Equal((0, 0, 0, 0), (versions["changed"]!.GetValue<int>(), versions["onlyRetail"]!.GetValue<int>(), versions["onlyRebuilt"]!.GetValue<int>(), versions["differences"]!.GetValue<int>()));
                Assert.Contains("version 13, a 1998 demo world", ((TextBlock)Find(window, c => c is TextBlock t && t.Text.StartsWith("Retail ", StringComparison.Ordinal) && t.Text.Contains("Merged tree", StringComparison.Ordinal))).Text);

                // The explorer opens it read-only like any world: its probe says so, and Whole world previews it.
                var opened = await Job("open_document", new() { ["path"] = demo });
                Assert.Equal(13, opened["format"]!["Version"]!.GetValue<int>());
                Assert.Contains("1998 demo, read-only", opened["format"]!["Description"]!.GetValue<string>());
                string document = opened["id"]!.GetValue<string>();
                var assets = (await Call("assets", new() { ["document"] = document, ["query"] = "Whole world" }))["items"]!.AsArray();
                var whole = assets.Single(x => x!["Kind"]!.GetValue<string>() == "World")!;
                await Job("select_asset", new() { ["document"] = document, ["kind"] = "World", ["index"] = whole["Index"]!.GetValue<int>() });
                Assert.Same(main.ViewModel.SelectedDocument, main.ViewModel.Documents.Single(d => d.Path == demo));
                Assert.Equal(Visibility.Collapsed, ((FrameworkElement)main.FindName("EmptyPreview")).Visibility);
                Assert.Null(main.ViewModel.SelectedDocument!.ModelEdits);
                await Call("close_document", new() { ["document"] = document, ["revision"] = opened["Revision"]!.GetValue<long>() });
            }
            finally { try { Directory.Delete(demoFolder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

            // A second, parentless World and its descendant are visible to both the comparison window and MCP.
            WorldNode detached = new("detached", WorldNodeClass.World), detachedChild = new("detached-child", WorldNodeClass.Object3D);
            detached.Children.Add(detachedChild); detachedChild.Parents.Add(detached); changed.Nodes.AddRange([detached, detachedChild]);
            string extraWorld = fixture.Path("extra-world.zbd");
            await File.WriteAllBytesAsync(extraWorld, GameZWriter.Write(changed, token), token);
            var extraSummary = await Job("world_compare", new() { ["retail"] = rebuilt, ["rebuilt"] = extraWorld });
            Assert.Equal(2, extraSummary["onlyRebuilt"]!.GetValue<int>());
            var extraRoots = await Call("world_compare_tree", new() { ["context"] = extraSummary["context"]!.GetValue<string>() });
            var extra = Assert.Single(extraRoots["children"]!["items"]!.AsArray(), r => r!["name"]!.GetValue<string>() == "detached")!;
            Assert.Equal("only rebuilt", extra["status"]!.GetValue<string>());
            Assert.Contains(window.View!.Roots, r => r.Name == "detached");

            // Directory-only link changes mark the world row and are inspectable in both the window and MCP.
            WorldTexture texture = new("variant"), next = new("variant-low"); texture.NextVariant = next;
            changed.Textures.AddRange([texture, next]);
            string linked = fixture.Path("linked.zbd"), unlinked = fixture.Path("unlinked.zbd");
            await File.WriteAllBytesAsync(linked, GameZWriter.Write(changed, token), token);
            texture.NextVariant = null;
            await File.WriteAllBytesAsync(unlinked, GameZWriter.Write(changed, token), token);
            var links = await Job("world_compare", new() { ["retail"] = linked, ["rebuilt"] = unlinked });
            Assert.Equal(1, links["differences"]!.GetValue<int>()); Assert.Equal(1, links["changed"]!.GetValue<int>());
            var linkedRoots = await Call("world_compare_tree", new() { ["context"] = links["context"]!.GetValue<string>() });
            var linkedWorld = Assert.Single(linkedRoots["children"]!["items"]!.AsArray(), r => r!["status"]!.GetValue<string>() == "changed")!;
            Assert.Contains(linkedWorld["differences"]!.AsArray(), d => d!["field"]!.GetValue<string>().Contains("nextVariant", StringComparison.Ordinal));
            var row = window.View!.Roots.Single(r => r.Source.Status == WorldComparisonStatus.Changed);
            Assert.Contains(window.View.Details(row), d => d.Differs && d.Field.Contains("nextVariant", StringComparison.Ordinal));

            // The primary root is paired even when renamed; both consumers must report its changed identity.
            changed.Nodes.First(n => n.Class == WorldNodeClass.World).Name = "renamed-world";
            string renamed = fixture.Path("renamed.zbd");
            await File.WriteAllBytesAsync(renamed, GameZWriter.Write(changed, token), token);
            var names = await Job("world_compare", new() { ["retail"] = unlinked, ["rebuilt"] = renamed });
            var nameRoots = await Call("world_compare_tree", new() { ["context"] = names["context"]!.GetValue<string>() });
            Assert.Contains(nameRoots["children"]!["items"]!.AsArray(), r => r!["differences"]!.AsArray().Any(d => d!["field"]!.GetValue<string>() == "name"));
            Assert.Contains(window.View!.Roots, r => window.View.Details(r).Any(d => d.Differs && d.Field == "name"));

            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments, bool error = false)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                string text = string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
                Assert.True((result.IsError == true) == error, text);
                return error ? JsonValue.Create(text)! : JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
            }
        }
        finally { main.Close(); }
    }

    private static DependencyObject Find(DependencyObject root, Func<DependencyObject, bool> match)
    {
        if (match(root)) return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            if (Find(child, match) is { } found && match(found)) return found;
        return null!;
    }

    private static void Capture(Window window, string path)
    {
        window.UpdateLayout();
        if (window.ActualWidth < 1) return;
        // The window itself, so the theme's background is drawn under its content.
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        PngBitmapEncoder encoder = new(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(path); encoder.Save(file);
    }
}
