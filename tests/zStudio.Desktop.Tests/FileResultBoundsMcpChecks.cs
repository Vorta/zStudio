using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

/// <summary>Producers crossing the path-preview cap must retain retrievable completion results after every file is saved.</summary>
internal static class FileResultBoundsMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(10)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        string? lastOperation = null;
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            CheckDocumentPages();
            await ModelAndContentSaves();


            async Task ModelAndContentSaves()
            {
                string root = Path.Combine(Path.GetTempPath(), "zstudio-large-file-results-" + Guid.NewGuid().ToString("N"));
                string folder = Path.Combine(root, new string('&', 190)), input = Path.Combine(root, "input");
                Directory.CreateDirectory(folder); Directory.CreateDirectory(input);
                try
                {
                    string world = Path.Combine(folder, "gamez.zbd");
                    byte[] originalWorld = ModelFixture.GameZ(), originalPack = ModelFixture.Texture();
                    await File.WriteAllBytesAsync(world, originalWorld, token);
                    string[] packs = [.. Enumerable.Range(0, 65).Select(i => Path.Combine(folder, $"texture{i:D4}.zbd"))];
                    foreach (string pack in packs) await File.WriteAllBytesAsync(pack, originalPack, token);
                    string png = Path.Combine(input, "diffuse.png");
                    await File.WriteAllBytesAsync(png, PngEncoder.Encode(new(1, 1, [255, 0, 0, 255]), token), token);
                    await File.WriteAllTextAsync(Path.Combine(input, "mesh.obj"), "v -.3 0 0\nv .3 0 0\nv 0 2 .1\nvt 0 0\nvt 1 0\nvt .5 1\nvn 0 0 1\nf 1/1/1 2/2/1 3/3/1\n", token);
                    string manifest = Path.Combine(input, "replacement.json");
                    await File.WriteAllTextAsync(manifest, JsonSerializer.Serialize(new ModelImportManifest(1, Convert.ToHexString(SHA256.HashData(originalWorld)), "large_result", "diffuse.png", [new(1, "mesh.obj")])), token);
                    await Job("open_root", new() { ["path"] = root });
                    await Job("open_document", new() { ["path"] = world });
                    var doc = main.ViewModel.Documents.Single();
                    var replacement = await Job("model_replace", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["manifest"] = manifest });
                    Assert.Equal(65, replacement["texturePackCount"]!.GetValue<int>()); Assert.True(replacement["texturePacksTruncated"]!.GetValue<bool>());
                    Assert.Equal(64, replacement["texturePacks"]!.AsArray().Count);
                    Assert.Equal(65, doc.ModelEdits!.Current.Textures.Count);
                    Assert.Equal(originalWorld, await File.ReadAllBytesAsync(world, token));
                    foreach (string pack in packs) Assert.Equal(originalPack, await File.ReadAllBytesAsync(pack, token));
                    var expected = doc.ModelEdits.Documents.ToDictionary(d => d.Path, d => d.Bytes.ToArray());
                    var saved = await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
                    CheckSaved(saved["models"]!, 66); Assert.False(doc.IsDirty);
                    foreach (var (path, bytes) in expected) Assert.Equal(bytes, await File.ReadAllBytesAsync(path, token));
                    await VerifyRetained(saved); CloseDocuments();

                    await Job("open_document", new() { ["path"] = packs[0] });
                    doc = main.ViewModel.Documents.Single();
                    var green = TextureDecoder.Decode(doc.PreviewDocument, doc.PreviewDocument.Assets[0], token);
                    for (int pixel = 0; pixel < green.Rgba.Length; pixel += 4)
                    { green.Rgba[pixel] = 0; green.Rgba[pixel + 1] = 255; green.Rgba[pixel + 2] = 0; green.Rgba[pixel + 3] = 255; }
                    await File.WriteAllBytesAsync(png, PngEncoder.Encode(green, token), token);
                    JsonNode? imported = null;
                    for (int offset = 1; offset < packs.Length; offset += 63)
                    {
                        var targets = new[] { packs[0] }.Concat(packs.Skip(offset).Take(63)).Select(path => new { path, index = 0 }).ToArray();
                        imported = await Job("texture_import", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["path"] = png, ["index"] = 0, ["targets"] = targets });
                    }
                    Assert.Equal(65, imported!["fileCount"]!.GetValue<int>()); Assert.True(imported["filesTruncated"]!.GetValue<bool>());
                    Assert.Equal(64, imported["files"]!.AsArray().Count);
                    var content = imported["document"]!["contentEdits"]!;
                    Assert.Equal(65, content["fileCount"]!.GetValue<int>()); Assert.True(content["filesTruncated"]!.GetValue<bool>());
                    Assert.Equal(64, content["files"]!.AsArray().Count);
                    var state = await Call("state", new()); Assert.Equal(65, state["documents"]![0]!["contentEdits"]!["fileCount"]!.GetValue<int>());
                    Assert.Empty(state["documents"]![0]!["contentEdits"]!["files"]!.AsArray());
                    Assert.True(state["documents"]![0]!["contentEdits"]!["filesTruncated"]!.GetValue<bool>());
                    foreach (string pack in packs) Assert.Equal(expected[pack], await File.ReadAllBytesAsync(pack, token));
                    var contentExpected = doc.ContentEdits!.Documents.ToDictionary(d => d.Path, d => d.Bytes.ToArray());
                    saved = await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
                    CheckSaved(saved["result"]!, 65); Assert.False(doc.IsDirty);
                    Assert.Equal(0, saved["result"]!["RemainingPathCount"]!.GetValue<int>());
                    Assert.False(saved["result"]!["RemainingPathsTruncated"]!.GetValue<bool>());
                    foreach (var (path, bytes) in contentExpected) Assert.Equal(bytes, await File.ReadAllBytesAsync(path, token));
                    Assert.Equal(expected[world], await File.ReadAllBytesAsync(world, token));
                    await VerifyRetained(saved);
                    CloseDocuments();

                    // Each individual owner fits its preview bound; aggregation must not multiply
                    // those bounded path rows across every open document in global state.
                    for (int group = 0; group < 2; group++)
                    {
                        string groupFolder = Path.Combine(folder, $"group{group:D2}"); Directory.CreateDirectory(groupFolder);
                        string[] groupPacks = [.. Enumerable.Range(0, 2).Select(i => Path.Combine(groupFolder, $"texture{i:D2}.zbd"))];
                        foreach (string path in groupPacks) await File.WriteAllBytesAsync(path, expected[packs[0]], token);
                        await Job("open_document", new() { ["path"] = groupPacks[0] });
                        var owner = main.ViewModel.Documents.Single(d => d.Path == groupPacks[0]);
                        var groupImport = await Job("texture_import", new()
                        {
                            ["document"] = owner.SessionId.ToString(), ["revision"] = owner.Revision,
                            ["path"] = png, ["index"] = 0,
                            ["targets"] = groupPacks.Select(path => new { path, index = 0 }).ToArray()
                        });
                        Assert.Equal(2, groupImport["fileCount"]!.GetValue<int>());
                        Assert.False(groupImport["filesTruncated"]!.GetValue<bool>());
                        Assert.Equal(2, groupImport["files"]!.AsArray().Count);
                    }
                    Assert.Equal(2, main.ViewModel.Documents.Count);
                    state = await Call("state", new());
                    Assert.Equal(2, state["documents"]!.AsArray().Count);
                    foreach (var summary in state["documents"]!.AsArray())
                    {
                        Assert.Equal(2, summary!["contentEdits"]!["fileCount"]!.GetValue<int>());
                        Assert.Empty(summary["contentEdits"]!["files"]!.AsArray());
                        Assert.True(summary["contentEdits"]!["filesTruncated"]!.GetValue<bool>());
                    }
                    Assert.All(main.ViewModel.Documents, d => Assert.True(d.IsDirty));
                    var identities = main.ViewModel.Documents.Select(d => (Id: d.SessionId.ToString(), d.Path)).ToArray();
                    List<(string Id, string Path)> paged = []; int pageOffset = 0;
                    do
                    {
                        var page = await Call("state", new() { ["offset"] = pageOffset, ["limit"] = 1 });
                        Assert.Equal(2, page["total"]!.GetValue<int>()); Assert.Equal(pageOffset, page["offset"]!.GetValue<int>());
                        Assert.Single(page["documents"]!.AsArray());
                        paged.AddRange(page["documents"]!.AsArray().Select(d => (d!["id"]!.GetValue<string>(), d["Path"]!.GetValue<string>())));
                        pageOffset = page["nextOffset"]?.GetValue<int>() ?? -1;
                    } while (pageOffset >= 0);
                    Assert.Equal(identities, paged);
                    var maximum = await Call("state", new() { ["limit"] = 64 });
                    Assert.Equal(2, maximum["documents"]!.AsArray().Count); Assert.Null(maximum["nextOffset"]);
                    var end = await Call("state", new() { ["offset"] = 2, ["limit"] = 1 });
                    Assert.Equal(2, end["total"]!.GetValue<int>()); Assert.Empty(end["documents"]!.AsArray()); Assert.Null(end["nextOffset"]);
                }
                finally { CloseDocuments(); Directory.Delete(root, true); }
            }

            static void CheckSaved(JsonNode result, int count)
            {
                Assert.Equal(count, result["SavedPathCount"]!.GetValue<int>()); Assert.True(result["SavedPathsTruncated"]!.GetValue<bool>());
                Assert.Equal(64, result["SavedPaths"]!.AsArray().Count); Assert.Empty(result["Errors"]!.AsArray());
                Assert.Equal(0, result["ErrorCount"]!.GetValue<int>()); Assert.False(result["ErrorsTruncated"]!.GetValue<bool>());
            }
            static void CheckDocumentPages()
            {
                List<DocumentModel> documents = [];
                try
                {
                    byte[] bytes = ModelFixture.Texture();
                    for (int i = 0; i < 66; i++)
                        documents.Add(new(FormatRegistry.Default.OpenBytes(@"C:\" + new string('&', 190) + $"\\t{i:D4}.zbd", bytes)));
                    int offset = 0; List<Guid> ids = [];
                    do
                    {
                        var page = StateDocumentPage.Select(documents, offset, 64);
                        Assert.Equal(66, page.Total); Assert.InRange(page.Documents.Length, 1, 64);
                        Assert.True(page.Documents.Sum(StateDocumentPage.MaximumRowBytes) <= StateDocumentPage.MaximumBytes);
                        ids.AddRange(page.Documents.Select(d => d.SessionId)); offset = page.NextOffset ?? -1;
                    } while (offset >= 0);
                    Assert.Equal(documents.Select(d => d.SessionId), ids);
                    // A legal Windows-length identity can shorten a page without clipping any identity.
                    string longPath = @"C:\" + string.Join("\\", Enumerable.Repeat(new string('&', 199), 160));
                    foreach (var d in documents.Take(64)) d.LastSavedCopy = longPath;
                    var large = StateDocumentPage.Select(documents, 0, 64);
                    Assert.InRange(large.Documents.Length, 1, 63); Assert.Equal(large.Documents.Length, large.NextOffset);
                    Assert.All(large.Documents, d => Assert.Equal(longPath, d.LastSavedCopy));
                    Assert.True(large.Documents.Sum(StateDocumentPage.MaximumRowBytes) <= StateDocumentPage.MaximumBytes);
                    var options = new JsonSerializerOptions { IncludeFields = true };
                    options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                    var projected = large.Documents.Select(d => MainWindow.DocumentState(d, includeContentDetails: false)).ToArray();
                    Assert.True(JsonSerializer.SerializeToNode(projected, options)!.ToJsonString().Length <= StateDocumentPage.MaximumBytes);
                    for (int i = 0; i < projected.Length; i++)
                        Assert.True(JsonSerializer.SerializeToNode(projected[i], options)!.ToJsonString().Length <= StateDocumentPage.MaximumRowBytes(large.Documents[i]));
                    var single = StateDocumentPage.Select(documents, 0, 1);
                    Assert.Single(single.Documents); Assert.Equal(1, single.NextOffset);
                    Assert.Empty(StateDocumentPage.Select(documents, int.MaxValue, 64).Documents);
                    Assert.Throws<Recoil.Zbd.Automation.StudioCommandException>(() => StateDocumentPage.Select(documents, -1, 64));
                    Assert.Throws<Recoil.Zbd.Automation.StudioCommandException>(() => StateDocumentPage.Select(documents, 0, 65));
                    documents[0].LastSavedCopy = new string('&', StateDocumentPage.MaximumBytes);
                    Assert.Throws<Recoil.Zbd.Automation.StudioCommandException>(() => StateDocumentPage.Select(documents, 0, 1));
                }
                finally { foreach (var document in documents) document.Dispose(); }
            }
            void CloseDocuments() { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); }
            async Task VerifyRetained(JsonNode expected)
            {
                var operation = await Call("operation", new() { ["id"] = lastOperation });
                Assert.Equal("completed", operation["State"]!.GetValue<string>()); Assert.True(JsonNode.DeepEquals(expected, operation["result"]));
            }
            async Task<JsonNode> Call(string name, Dictionary<string, object?> args)
            {
                var response = await client.CallToolAsync("zstudio_" + name, args, cancellationToken: token);
                string text = response.Content.OfType<TextContentBlock>().Single().Text;
                Assert.False(response.IsError == true, text); Assert.True(text.Length < 1024 * 1024, $"{name} result exceeds 1 MiB");
                return JsonNode.Parse(text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> args, string expectedState = "completed")
            {
                var operation = await Call(name, args); lastOperation = operation["id"]!.GetValue<string>();
                while (operation["State"]!.GetValue<string>() is "queued" or "running")
                { await Task.Delay(20, token); operation = await Call("operation", new() { ["id"] = lastOperation }); }
                Assert.True(operation["State"]!.GetValue<string>() == expectedState, operation.ToJsonString());
                return operation["result"]!;
            }
        }
        finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); }
    }
}
