using System.Buffers.Binary;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class ResourceEditingMcpChecks
{
    internal static async Task Run()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "zstudio-resource-protocol-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(rootPath);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            string source = Path.Combine(rootPath, "resources.zbd"), copy = Path.Combine(rootPath, "copy.zbd");
            var authored = ZrdNode.Create(ZrdKind.Array) with { Children = [ZrdNode.Create(ZrdKind.Int, "42"), ZrdNode.Create(ZrdKind.Float, "0x7FA12345"), ZrdNode.Create(ZrdKind.Array)] };
            byte[] data = ZrdWriter.Write(authored, token), bytes = new byte[data.Length + 148 + 8]; data.CopyTo(bytes, 0);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(data.Length + 4), data.Length); Encoding.Latin1.GetBytes("data.zrd").CopyTo(bytes, data.Length + 8);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), 1); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), 1);
            await File.WriteAllBytesAsync(source, bytes, token);
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await Job("open_document", new() { ["path"] = source }); var doc = main.ViewModel.Documents.Single(); var edits = doc.ResourceEdits!;
            var members = await Call("archive_members", Args()); Guid member = Guid.Parse(members["members"]!["items"]![0]!["member"]!.GetValue<string>());
            var listing = await Job("zrd_nodes", Args(("member", member))); Guid root = Guid.Parse(listing["nodes"]!["items"]![0]!["node"]!.GetValue<string>());
            var children = await Job("zrd_nodes", Args(("member", member), ("node", root)));
            Guid value = Guid.Parse(children["nodes"]!["items"]![0]!["node"]!.GetValue<string>()), array = Guid.Parse(children["nodes"]!["items"]![2]!["node"]!.GetValue<string>());
            await Job("resource_select", Args(("member", member), ("node", value)));
            var tree = (TreeView)main.FindName("CentralTree"); Assert.IsType<ResourceTreeItem>(tree.Items[0]);
            await Job("resource_properties", Args(("action", "open"), ("member", member), ("node", value)));
            var pinned = main.OpenPropertiesWindow!.ResourceFields!; Assert.Equal(value, pinned.NodeId);
            var box = Descendants(pinned).OfType<TextBox>().Single(b => !b.IsReadOnly); box.Text = "invalid integer";
            var bad = await Job("zrd_edit", Args(("action", "delete"), ("member", member), ("node", value)), "failed"); Assert.Equal("pending_drafts", bad["code"]!.GetValue<string>());
            var draft = await Call("drafts", new() { ["target"] = "properties" });
            var invalidArgs = Args(("target", "properties"), ("token", draft["drafts"]!["token"]!.GetValue<string>()), ("action", "apply")); invalidArgs.Remove("revision");
            var invalid = await client.CallToolAsync("zstudio_resolve_drafts", invalidArgs, cancellationToken: token);
            Assert.Contains("invalid_draft", invalid.Content.OfType<TextContentBlock>().Single().Text);
            Assert.True(invalid.IsError); Assert.Equal("invalid integer", box.Text); Assert.False(doc.IsDirty);
            box.Text = "84";
            draft = await Call("drafts", new() { ["target"] = "properties" }); await Call("resolve_drafts", Args(("target", "properties"), ("token", draft["drafts"]!["token"]!.GetValue<string>()), ("action", "apply")));
            Assert.True(doc.IsDirty); Assert.Equal(84u, edits.Tree(edits.Member(member), token).Find(value)!.Bits); Assert.False(pinned.HasPendingDrafts);
            Assert.True(((Button)main.FindName("DocumentUndo")).IsEnabled);
            await Job("zrd_edit", Args(("action", "move"), ("member", member), ("node", value), ("parent", array), ("position", 0)));
            Assert.Equal(value, main.OpenPropertiesWindow.ResourceFields!.NodeId); Assert.Equal(84u, edits.Tree(edits.Member(member), token).Find(array)!.Children.Single().Bits);
            await Job("zrd_edit", Args(("action", "delete"), ("member", member), ("node", value))); Assert.Contains("deleted", pinned.TargetLabel);
            await Call("undo_redo", Args(("action", "undo"))); Assert.Equal("84", pinned.Json["value"]!.GetValue<string>());
            await Job("zrd_edit", Args(("action", "add"), ("member", member), ("node", array), ("kind", "String"), ("value", "\"new\\u0000value\"")));
            var fieldList = await Job("resource_properties", Args(("action", "fields"), ("member", member), ("node", value)));
            string field = fieldList["fields"]!["fields"]!.AsArray().Single(f => f!["Label"]!.GetValue<string>() == "Value")!["Id"]!.GetValue<string>();
            await Job("resource_properties", Args(("action", "edit"), ("member", member), ("node", value), ("field", field), ("value", "126")));
            await Job("archive_edit", Args(("action", "duplicate"), ("member", member), ("name", "data.zrd"))); Assert.Equal(2, edits.Current.Members.Count);
            Guid duplicate = edits.Current.Members[1].Id;
            var contextRow = doc.Assets.Single(a => a.ResourceId == member);
            await Job("archive_edit", Args(("action", "move"), ("member", member), ("position", 1))); Assert.Equal(member, doc.SelectedAsset!.ResourceId);
            const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(MainWindow).GetField("contextAsset", flags)!.SetValue(main, contextRow); typeof(MainWindow).GetField("assetContextDocument", flags)!.SetValue(main, doc);
            ((MenuItem)main.FindName("AssetPropertiesMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            while (main.OpenPropertiesWindow?.ResourceFields?.NodeId != null) await Task.Delay(10, token);
            Assert.Equal(member, main.OpenPropertiesWindow!.ResourceFields!.MemberId);
            await Job("resource_properties", Args(("action", "open"), ("member", member), ("node", value))); pinned = main.OpenPropertiesWindow.ResourceFields!;
            await Job("archive_edit", Args(("action", "rename"), ("member", duplicate), ("name", "copy.zrd")));
            await Job("archive_edit", Args(("action", "delete"), ("member", duplicate)));
            await Job("archive_edit", Args(("action", "add_zrd"), ("name", "fresh.zrd")));
            var staleArgs = Args(("action", "rename"), ("member", member), ("name", "wrong")); staleArgs["revision"] = 0;
            Assert.Equal("revision_conflict", (await Job("archive_edit", staleArgs, "failed"))["code"]!.GetValue<string>());
            var resolver = main.ViewModel.Resolver!; Assert.Same(edits.Current.Document, await resolver.OpenCachedAsync(source, token));
            Guid other = Guid.NewGuid(); Assert.Throws<InvalidOperationException>(() => resolver.EditOwnership.Acquire(other, "pickup editor", [source]));
            await Job("save_document", Args(("destination", copy))); Assert.False(doc.IsDirty); Assert.Equal(copy, edits.TargetPath); Assert.Equal(bytes, await File.ReadAllBytesAsync(source, token));
            Assert.Contains(Descendants(pinned).OfType<TextBox>(), b => b.IsReadOnly && b.Text == copy);
            await Job("archive_edit", Args(("action", "rename"), ("member", member), ("name", "renamed.zrd")));
            await Job("archive_edit", Args(("action", "rename"), ("member", member), ("name", "renamed.bin")));
            await Job("resource_select", Args(("member", member), ("node", value)));
            Assert.Equal(AssetKind.Zrd, doc.SelectedAsset!.Record.Kind); Assert.IsType<ResourceTreeItem>(tree.Items[0]);
            Assert.Equal(value, main.OpenPropertiesWindow!.ResourceFields!.NodeId);
            await Job("save_document", Args()); Assert.False(doc.IsDirty);
            var saved = await FormatRegistry.Default.OpenAsync(copy, token); Assert.Equal("renamed.bin", saved.Assets[0].Name); Assert.Equal(AssetKind.Zrd, saved.Assets[0].Kind);
            Assert.Equal(126u, ZrdDecoder.Read(saved.Slice(saved.Assets[0].Offset, saved.Assets[0].Length), token).FindByPath(1, 0).Bits);
            var editMenu = (MenuItem)main.FindName("EditMenu"); editMenu.RaiseEvent(new RoutedEventArgs(MenuItem.SubmenuOpenedEvent, editMenu));
            Assert.Contains(editMenu.Items.OfType<MenuItem>(), m => Equals(m.Header, "Archive members") && m.IsEnabled && m.Items.Count == 8);
            await Job("open_document", new() { ["path"] = copy });
            var openCopy = main.ViewModel.Documents.Single(d => d.Path == copy);
            Assert.Equal("destination_open", (await Job("reload_document", Args(), "failed"))["code"]!.GetValue<string>());
            Assert.False(doc.IsDisposed); Assert.Equal(2, main.ViewModel.Documents.Count); Assert.Same(edits, doc.ResourceEdits);
            await Call("close_document", new() { ["document"] = openCopy.SessionId.ToString(), ["revision"] = openCopy.Revision });
            await Job("reload_document", Args()); doc = main.ViewModel.Documents.Single(); edits = doc.ResourceEdits!;
            Assert.Equal(copy, doc.Path); Assert.Equal("renamed.bin", doc.Assets[0].Name); Assert.Equal(AssetKind.Zrd, doc.Assets[0].Record.Kind);
            await Call("close_document", Args()); resolver.EditOwnership.Acquire(other, "pickup editor", [source]); resolver.EditOwnership.Release(other);
            // Standalone root type replacement must remain ZRD even when an int payload resembles a ZAR footer.
            string standalone = Path.Combine(rootPath, "single.zrd"); await File.WriteAllBytesAsync(standalone, data, token);
            await Job("open_document", new() { ["path"] = standalone }); doc = main.ViewModel.Documents.Single(); edits = doc.ResourceEdits!; member = edits.Current.Members.Single().Id; root = edits.Tree(edits.Member(member), token).Id;
            await Job("zrd_edit", Args(("action", "type"), ("member", member), ("node", root), ("kind", "Int"), ("value", "0")));
            await Job("save_document", Args()); Assert.Equal(8, new FileInfo(standalone).Length);
            await Call("close_document", Args());
            // Bound formatting work as well as the response: escaping NUL expands each byte sixfold.
            string largePath = Path.Combine(rootPath, "large.zrd");
            var large = ZrdNode.Create(ZrdKind.String) with { Text = new string('\0', 2 * 1024 * 1024) };
            await File.WriteAllBytesAsync(largePath, ZrdWriter.Write(large, token), token);
            await Job("open_document", new() { ["path"] = largePath }); doc = main.ViewModel.Documents.Single(); edits = doc.ResourceEdits!; member = edits.Current.Members.Single().Id;
            await Job("zrd_nodes", Args(("member", member))); // Warm protocol/serializer caches before measuring.
            long allocated = GC.GetTotalAllocatedBytes(true);
            var largeListing = await Job("zrd_nodes", Args(("member", member)));
            allocated = GC.GetTotalAllocatedBytes(true) - allocated;
            Assert.True(allocated < 8 * 1024 * 1024, $"Bounded ZRD listing allocated {allocated:N0} bytes.");
            var largeRow = Assert.Single(largeListing["nodes"]!["items"]!.AsArray())!;
            Assert.True(largeRow["valueTruncated"]!.GetValue<bool>());
            string expectedPrefix = "\"" + string.Concat(Enumerable.Repeat("\\u0000", 683));
            Assert.Equal(expectedPrefix[..4096], largeRow["value"]!.GetValue<string>());
            var largeTree = new ResourceTreeItem(large, 0, null, new Dictionary<Guid, bool>());
            long labelAllocated = GC.GetAllocatedBytesForCurrentThread();
            string label = largeTree.Label;
            labelAllocated = GC.GetAllocatedBytesForCurrentThread() - labelAllocated;
            Assert.Equal("Root · String · " + expectedPrefix[..200] + "…", label);
            Assert.True(labelAllocated < 128 * 1024, $"Data tree label allocated {labelAllocated:N0} bytes.");
            Assert.False(doc.IsDirty);
            var largeNodeId = edits.Tree(edits.Member(member), token).Id;
            var largeFields = await Job("resource_properties", Args(("action", "fields"), ("member", member), ("node", largeNodeId)));
            var valueField = largeFields["fields"]!["fields"]!.AsArray().Single(f => f!["Label"]!.GetValue<string>() == "Value")!;
            Assert.True(valueField["readOnly"]!.GetValue<bool>()); Assert.Equal(16384, valueField["value"]!.GetValue<string>().Length);
            await Job("resource_properties", Args(("action", "open"), ("member", member), ("node", largeNodeId)));
            Assert.DoesNotContain(Descendants(main.OpenPropertiesWindow!.ResourceFields!).OfType<TextBox>(), b => !b.IsReadOnly);
            Assert.False(main.OpenPropertiesWindow.ResourceFields!.HasPendingDrafts);
            var inspected = await Call("inspect_asset", Args(("kind", "Zrd"), ("index", 0)));
            Assert.True(inspected["source"]!["tree"]!["value_truncated"]!.GetValue<bool>());
            Assert.Equal(4096, inspected["edited"]!["tree"]!["value"]!.GetValue<string>().Length);
            Assert.Equal(large.Text, edits.Tree(edits.Member(member), token).Text); Assert.False(doc.IsDirty);
            await Job("zrd_edit", Args(("action", "type"), ("member", member), ("node", largeNodeId), ("kind", "String"), ("value", "\"short\"")));
            Assert.Equal("\"short\"", Descendants(main.OpenPropertiesWindow!.ResourceFields!).OfType<TextBox>().Single(b => !b.IsReadOnly).Text);
            await Call("undo_redo", Args(("action", "undo")));
            Assert.DoesNotContain(Descendants(main.OpenPropertiesWindow.ResourceFields!).OfType<TextBox>(), b => !b.IsReadOnly);
            Assert.Equal(large.Text, edits.Tree(edits.Member(member), token).Text); Assert.False(doc.IsDirty);
            int projections = 0;
            var paged = MainWindow.Page(Enumerable.Range(0, 10000), new() { ["offset"] = 10, ["limit"] = 2, ["query"] = "9" }, i => i.ToString(System.Globalization.CultureInfo.InvariantCulture), i => { projections++; return new { index = i }; }).Data;
            var matching = Enumerable.Range(0, 10000).Where(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture).Contains('9')).ToArray();
            Assert.Equal(2, projections); Assert.Equal(matching.Length, paged["total"]!.GetValue<int>());
            Assert.Equal(matching[10], paged["items"]![0]!["index"]!.GetValue<int>()); Assert.Equal(12, paged["nextOffset"]!.GetValue<int>());
            await CheckSharedPickupOwnerAsync(rootPath, token);

            Dictionary<string, object?> Args(params (string Key, object Value)[] values)
            { var result = new Dictionary<string, object?> { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision }; foreach (var (key, v) in values) result[key] = v is Guid guid ? guid.ToString() : v; return result; }
            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments)
            {
                // Read-only schemas deliberately reject accidental revision arguments.
                if (name is "archive_members" or "resolve_drafts" or "inspect_asset") arguments.Remove("revision");
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token); Assert.False(result.IsError == true, string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
                return JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                if (name is "zrd_nodes" or "resource_select") arguments.Remove("revision");
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
            }
        }
        finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); Directory.Delete(rootPath, true); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static ZrdNode FindByPath(this ZrdNode node, params int[] indices) { foreach (int i in indices) node = node.Children[i]; return node; }
    private static async Task CheckSharedPickupOwnerAsync(string parent, CancellationToken token)
    {
        string folder = Path.Combine(parent, "ownership"); Directory.CreateDirectory(folder);
        ZrdNode Array(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
        ZrdNode Vector() => Array(ZrdNode.Create(ZrdKind.Float), ZrdNode.Create(ZrdKind.Float), ZrdNode.Create(ZrdKind.Float));
        var root = Array(Array(Array(ZrdNode.Create(ZrdKind.String, System.Text.Json.JsonSerializer.Serialize(MissionPickupType.Catalog[0].Name)), ZrdNode.Create(ZrdKind.Int, "1"), Vector(), Vector(), ZrdNode.Create(ZrdKind.Float))));
        byte[] data = ZrdWriter.Write(root, token), bytes = new byte[data.Length + 156]; data.CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(data.Length + 4), data.Length); Encoding.Latin1.GetBytes("puppies.zrd").CopyTo(bytes, data.Length + 8);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 8), 1); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(bytes.Length - 4), 1);
        string path = Path.Combine(folder, "zrdr.zbd"); await File.WriteAllBytesAsync(path, bytes, token);
        using AssetResolver resolver = new(folder);
        DocumentModel Map() { var d = new DocumentModel(new ZbdDocument(Path.Combine(folder, "gamez.zbd"), new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty)); d.AttachResolver(resolver); return d; }
        using var archive = new DocumentModel(await FormatRegistry.Default.OpenAsync(path, token)); archive.AttachResolver(resolver);
        using var map = Map(); var pickups = await map.GetPickupEditsAsync(resolver, token); var placement = Assert.Single(pickups.Records);
        pickups.MoveTo(placement.Source, new(1,2,3)); pickups.Undo();
        var edits = archive.ResourceEdits!; Guid member = edits.Current.Members[0].Id;
        var duplicate = await edits.PrepareArchiveAsync("duplicate", member, "copy.zrd", token: token);
        Assert.Throws<InvalidOperationException>(() => edits.Accept(duplicate)); Assert.False(edits.IsDirty);
        map.Dispose(); edits.Accept(duplicate); Assert.True(edits.IsDirty);
        using var nextMap = Map(); var nextPickups = await nextMap.GetPickupEditsAsync(resolver, token); var nextPlacement = Assert.Single(nextPickups.Records);
        Assert.Throws<InvalidOperationException>(() => nextPickups.MoveTo(nextPlacement.Source, new(1,2,3)));
        archive.Dispose();
        Assert.Throws<InvalidOperationException>(() => nextPickups.MoveTo(nextPlacement.Source, new(1,2,3))); // stale unsaved dependency snapshot
        var refreshed = await nextMap.GetPickupEditsAsync(resolver, token); Assert.NotSame(refreshed, nextPickups);
        Assert.True(refreshed.MoveTo(Assert.Single(refreshed.Records).Source, new(1,2,3)));
    }
}
