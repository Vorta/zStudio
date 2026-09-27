using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class ModelReplacementMcpChecks
{
    internal static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(),"zstudio-model-protocol-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            string source = Path.Combine(root,"gamez.zbd"), texture = Path.Combine(root,"texture2.zbd"), input = Path.Combine(root,"input"); Directory.CreateDirectory(input);
            await File.WriteAllBytesAsync(source,ModelFixture.GameZ(),token); await File.WriteAllBytesAsync(texture,ModelFixture.Texture(),token);
            await File.WriteAllTextAsync(Path.Combine(input,"mesh.obj"),"v -.3 0 0\nv .3 0 0\nv 0 2 .1\nvt 0 0\nvt 1 0\nvt .5 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\n",token);
            await File.WriteAllBytesAsync(Path.Combine(input,"diffuse.png"),PngEncoder.Encode(new(1,1,[128,64,32,255]),token),token);
            string manifest = Path.Combine(input,"replacement.json");
            await File.WriteAllTextAsync(manifest,JsonSerializer.Serialize(new ModelImportManifest(1,Convert.ToHexString(SHA256.HashData(ModelFixture.GameZ())),"new_shell","diffuse.png",[new(1,"mesh.obj")])),token);
            await using var host = new LocalMcpHost(main.Commands,"test");
            await using var pipe = new NamedPipeClientStream(".",host.Instance.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe,pipe),cancellationToken:token);
            var tools = await client.ListToolsAsync(cancellationToken:token); Assert.Contains(tools,t=>t.Name=="zstudio_model_replace");
            await Job("open_document",new() { ["path"] = source });
            var doc = main.ViewModel.Documents.Single();
            await Job("select_asset", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Model", ["index"] = 1 });
            var sourceModelRow = doc.SelectedAsset!;
            string originalBytesText = ((TextBox)main.FindName("RawText")).Text;
            await Call("properties_open", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Model", ["index"] = 1 });
            var resolver = main.ViewModel.Resolver!; Guid otherOwner = Guid.NewGuid();
            resolver.EditOwnership.Acquire(otherOwner, "other resource editor", [texture]);
            long originalRevision = doc.Revision; var originalSnapshot = doc.ModelEdits!.Current;
            var ownershipFailure = await Job("model_replace", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["manifest"] = manifest }, "failed");
            Assert.Contains("owned", ownershipFailure.ToJsonString()); Assert.Same(originalSnapshot, doc.ModelEdits.Current);
            Assert.Equal(originalRevision, doc.Revision); Assert.False(doc.IsDirty); Assert.False(doc.CanUndoScene);
            resolver.EditOwnership.Release(otherOwner);
            var replaced = await Job("model_replace",new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["manifest"] = manifest });
            Assert.True(doc.IsDirty); Assert.True(((MenuItem)main.FindName("UndoMenu")).IsEnabled);
            Assert.Equal(doc.PreviewDocument.Assets.Count, doc.Assets.Count);
            Assert.All(doc.Assets, row => Assert.Same(doc.PreviewDocument.Assets.Single(a => a.Id == row.Record.Id), row.Record));
            Assert.Equal(sourceModelRow.Record.Id, doc.SelectedAsset!.Record.Id);
            Assert.Same(sourceModelRow, doc.SelectedAsset);
            Assert.Equal(0, main.OpenPropertiesWindow!.CurrentJson!["properties"]!.Int("model_type"));
            Assert.Equal(0, JsonNode.Parse(((TextBox)main.FindName("ContentText")).Text)!["properties"]!.Int("model_type"));
            Assert.Equal(originalBytesText, ((TextBox)main.FindName("RawText")).Text);
            var assetList = await Call("assets", new() { ["document"] = doc.SessionId.ToString(), ["limit"] = 200 });
            Assert.Equal(doc.PreviewDocument.Assets.Count, assetList["total"]!.GetValue<int>());
            var listedModel = assetList["items"]!.AsArray().Single(a => a!["Kind"]!.GetValue<string>() == "Model" && a["Index"]!.GetValue<int>() == 1)!;
            Assert.Equal(doc.Document.Assets.Single(a => a.Kind == AssetKind.Model && a.Index == 1).Offset, listedModel["sourceOffset"]!.GetValue<long>());
            Assert.NotEqual(listedModel["sourceOffset"]!.GetValue<long>(), listedModel["Offset"]!.GetValue<long>());
            Assert.Equal(0,doc.PreviewDocument.Scene!.Models[1].Metadata.Int("model_type")); Assert.Equal(1,doc.Document.Scene!.Models[1].Metadata.Int("model_type"));
            var inspection = await Call("inspect_asset",new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Model", ["index"] = 1 });
            Assert.Equal(1,inspection["source"]!["properties"]!["model_type"]!.GetValue<int>()); Assert.Equal(0,inspection["edited"]!["properties"]!["model_type"]!.GetValue<int>());
            var stale = await Job("model_replace",new() { ["document"] = doc.SessionId.ToString(), ["revision"] = 0, ["manifest"] = manifest },"failed"); Assert.Equal("revision_conflict",stale["code"]!.GetValue<string>());
            foreach (string action in new[] { "undo", "redo" })
            {
                await Call("undo_redo",new() { ["document"] = doc.SessionId.ToString(),["revision"] = doc.Revision,["action"] = action }); Assert.Equal(action=="redo",doc.IsDirty);
                Assert.Same(sourceModelRow, doc.SelectedAsset);
                Assert.Equal(action == "undo" ? 1 : 0, main.OpenPropertiesWindow.CurrentJson!["properties"]!.Int("model_type"));
                Assert.Equal(doc.PreviewDocument.Assets.Count, doc.Assets.Count);
                Assert.Equal(originalBytesText, ((TextBox)main.FindName("RawText")).Text);
            }
            await Job("select_asset", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Material", ["index"] = 1 });
            Assert.Equal("New record: no original source bytes.", ((TextBox)main.FindName("RawText")).Text);
            Assert.Equal(1, main.OpenPropertiesWindow.CurrentJson!.Int("index")); // Selection did not retarget the pinned model.
            Assert.Equal("Model", main.OpenPropertiesWindow.CurrentJson!["kind"]!.GetValue<string>());
            await Call("properties_open", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Material", ["index"] = 1 });
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" });
            Assert.NotNull(main.OpenPropertiesWindow.CurrentJson!["unavailable"]); Assert.DoesNotContain(doc.Assets, a => a.Record.Kind == AssetKind.Material && a.Index == 1);
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "redo" });
            Assert.Equal("Material", main.OpenPropertiesWindow.CurrentJson!["kind"]!.GetValue<string>());
            var newInspection = await Call("inspect_asset", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "TextureReference", ["index"] = 0 });
            Assert.Null(newInspection["source"]); Assert.Equal("new_shell", newInspection["edited"]!["name"]!.GetValue<string>());
            await Job("save_document",new() { ["document"] = doc.SessionId.ToString(),["revision"] = doc.Revision }); Assert.False(doc.IsDirty); Assert.False(doc.ModelEdits!.HasExternalChanges());
            var saved = await FormatRegistry.Default.OpenAsync(source,token); Assert.Equal(0,saved.Scene!.Models[1].Metadata.Int("model_type"));
            var png = await FormatRegistry.Default.OpenAsync(texture,token); Assert.Single(png.Assets);
            string copies = Path.Combine(root, "copies");
            resolver.EditOwnership.Acquire(otherOwner, "destination editor", [Path.Combine(copies, "texture2.zbd")]);
            originalRevision = doc.Revision;
            ownershipFailure = await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["modelDirectory"] = copies }, "failed");
            Assert.Contains("owned", ownershipFailure.ToJsonString()); Assert.False(Directory.Exists(copies)); Assert.Equal(originalRevision, doc.Revision);
            resolver.EditOwnership.Release(otherOwner);
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["modelDirectory"] = copies });
            Assert.Throws<InvalidOperationException>(() => resolver.EditOwnership.Acquire(otherOwner, "destination editor", [Path.Combine(copies, "texture2.zbd")]));
            File.Delete(source); File.Delete(texture);
            main.ViewModel.CheckExternalChanges(); Assert.False(doc.IsStale);
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" });
            await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            Assert.Equal(ModelFixture.GameZ(), await File.ReadAllBytesAsync(Path.Combine(copies, "gamez.zbd"), token));
            Assert.Equal(ModelFixture.Texture(), await File.ReadAllBytesAsync(Path.Combine(copies, "texture2.zbd"), token));
            Assert.False(doc.IsDirty); main.ViewModel.CheckExternalChanges(); Assert.False(doc.IsStale);
            Assert.Throws<InvalidOperationException>(() => resolver.EditOwnership.Acquire(otherOwner, "source editor", [texture]));
            await Job("reload_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision }); doc = main.ViewModel.Documents.Single();
            Assert.Equal(Path.Combine(copies, "gamez.zbd"), doc.Path);
            await File.AppendAllTextAsync(Path.Combine(copies, "gamez.zbd"), "external change", token);
            main.ViewModel.CheckExternalChanges(); Assert.True(doc.IsStale);
            await Call("close_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
            resolver.EditOwnership.Acquire(otherOwner, "next editor", [texture, Path.Combine(copies, "texture2.zbd")]); resolver.EditOwnership.Release(otherOwner);
            async Task<JsonNode> Call(string name, Dictionary<string,object?> arguments)
            {
                var result = await client.CallToolAsync("zstudio_" + name,arguments,cancellationToken:token); Assert.False(result.IsError == true,string.Join(";",result.Content.OfType<TextContentBlock>().Select(c=>c.Text)));
                return JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string,object?> arguments, string expected = "completed")
            {
                var job = await Call(name,arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10,token); job = await Call("operation",new() { ["id"] = id }); }
                Assert.Equal(expected,job["State"]!.GetValue<string>()); return job["result"]!;
            }
        }
        finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); Directory.Delete(root,true); }
    }
}
