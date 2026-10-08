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

/// <summary>Accepted large producers must retain retrievable completion results after every source/model/content file is saved.</summary>
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
            await SourceSave();
            await ModelAndContentSaves();

            async Task SourceSave()
            {
                using var fixture = new SourceWorldFixture();
                string folder = "data/" + new string('&', 190);
                string original = "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"external\"}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0}";
                for (int i = 0; i < 4; i++) fixture.Write($"{folder}/model{i}.gltf", original);
                byte[] sentinel = await File.ReadAllBytesAsync(fixture.Path("gamegen/m1.gs"), token);
                try
                {
                    await Job("open_root", new() { ["path"] = fixture.Project, ["project"] = true });
                    await Job("source_world_open", new() { ["mission"] = "m1" });
                    for (int group = 0; group < 4; group++)
                    {
                        var checkout = await Job("source_blender_checkout", new() { ["model"] = $"{folder}/model{group}.gltf" });
                        string outbox = checkout["outbox"]!.GetValue<string>();
                        var gltf = JsonNode.Parse(original)!;
                        JsonArray buffers = [];
                        for (int i = 0; i < 1000; i++)
                        {
                            string name = $"buffer{group}_{i}.bin";
                            await File.WriteAllBytesAsync(Path.Combine(outbox, name), new byte[] { (byte)group, (byte)i, 1, 2 }, token);
                            buffers.Add(new JsonObject { ["uri"] = name, ["byteLength"] = 4 });
                        }
                        gltf["buffers"] = buffers;
                        await File.WriteAllTextAsync(Path.Combine(outbox, $"model{group}.gltf"), gltf.ToJsonString(), token);
                        var doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
                        await Job("source_blender_update", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["checkout"] = checkout["id"]!.GetValue<string>() });
                    }
                    var edited = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
                    var sourceState = await Call("state", new() { ["limit"] = 1 });
                    Assert.True(sourceState["documents"]![0]!.ToJsonString().Length <= StateDocumentPage.MaximumRowBytes(edited));
                    var workspace = edited.SourceWorld!.Workspace;
                    var expected = workspace.DirtyFiles.ToDictionary(p => p, p => workspace.Read(p, token) ?? throw new InvalidDataException("A fixture update unexpectedly deleted a file."));
                    Assert.Equal(4004, expected.Count);
                    Assert.True(JsonSerializer.SerializeToNode(new { written = expected.Keys })!.ToJsonString().Length > 4 * 1024 * 1024);
                    Assert.Equal(sentinel, await File.ReadAllBytesAsync(fixture.Path("gamegen/m1.gs"), token));
                    Assert.All(Enumerable.Range(0, 4), i => Assert.Equal(original, File.ReadAllText(fixture.Path($"{folder}/model{i}.gltf"))));
                    // The same admitted producer must also have a usable refusal when another program creates
                    // the pending buffers. Both Core reload and the actual MCP job retain history and identities.
                    string[] conflicts = [.. expected.Keys.Where(p => p.EndsWith(".bin", StringComparison.Ordinal))];
                    byte[] external = [9, 8, 7, 6];
                    long revision = workspace.Revision;
                    var history = workspace.History.ToArray();
                    foreach (string path in conflicts) await File.WriteAllBytesAsync(fixture.Path(path), external, token);
                    try
                    {
                        Assert.True(JsonSerializer.Serialize(string.Join(", ", conflicts)).Length > 4 * 1024 * 1024);
                        var failure = await Assert.ThrowsAsync<SourceFileChangedException>(() => workspace.ReloadAsync(token));
                        Assert.Equal(conflicts.Order(StringComparer.Ordinal), failure.Files.Order(StringComparer.Ordinal));
                        Assert.True(failure.Message.Length < 4096); Assert.Contains("more not shown", failure.Message);
                        var refused = await Job("reload_document", new() { ["document"] = edited.SessionId.ToString(), ["revision"] = edited.Revision }, "failed");
                        Assert.Equal("unsaved_changes", refused["code"]!.GetValue<string>());
                        Assert.True(Encoding.UTF8.GetByteCount(refused.ToJsonString()) < 32 * 1024);
                        Assert.Contains("more not shown", refused.ToJsonString());
                        Assert.Equal(revision, workspace.Revision); Assert.Equal(history, workspace.History);
                        Assert.Same(edited, main.ViewModel.Documents.Single(d => d.SourceWorld != null));
                        foreach (string path in conflicts)
                        {
                            Assert.Equal(expected[path], workspace.Read(path, token));
                            Assert.Equal(external, await File.ReadAllBytesAsync(fixture.Path(path), token));
                        }
                    }
                    finally { foreach (string path in conflicts) File.Delete(fixture.Path(path)); }
                    var saved = await Job("save_document", new() { ["document"] = edited.SessionId.ToString(), ["revision"] = edited.Revision });
                    Assert.Equal(4004, saved["writtenCount"]!.GetValue<int>());
                    Assert.True(saved["writtenTruncated"]!.GetValue<bool>()); Assert.Equal(64, saved["written"]!.AsArray().Count);
                    Assert.False(workspace.IsDirty);
                    foreach (var (path, bytes) in expected) Assert.Equal(bytes, await File.ReadAllBytesAsync(fixture.Path(path), token));
                    Assert.Equal(sentinel, await File.ReadAllBytesAsync(fixture.Path("gamegen/m1.gs"), token));
                    await VerifyRetained(saved);
                    // A completed ordinary small save preserves its complete path list and clears truncation.
                    var empty = await Job("save_document", new() { ["document"] = edited.SessionId.ToString(), ["revision"] = edited.Revision });
                    Assert.Equal(0, empty["writtenCount"]!.GetValue<int>()); Assert.False(empty["writtenTruncated"]!.GetValue<bool>());
                }
                finally { CloseDocuments(); }
            }

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
                    string[] packs = [.. Enumerable.Range(0, 4000).Select(i => Path.Combine(folder, $"texture{i:D4}.zbd"))];
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
                    Assert.Equal(4000, replacement["texturePackCount"]!.GetValue<int>()); Assert.True(replacement["texturePacksTruncated"]!.GetValue<bool>());
                    Assert.Equal(64, replacement["texturePacks"]!.AsArray().Count);
                    Assert.Equal(4000, doc.ModelEdits!.Current.Textures.Count);
                    Assert.True(JsonSerializer.SerializeToNode(new { texturePacks = doc.ModelEdits.Current.Textures.Keys })!.ToJsonString().Length > 4 * 1024 * 1024);
                    Assert.Equal(originalWorld, await File.ReadAllBytesAsync(world, token));
                    foreach (string pack in packs) Assert.Equal(originalPack, await File.ReadAllBytesAsync(pack, token));
                    var expected = doc.ModelEdits.Documents.ToDictionary(d => d.Path, d => d.Bytes.ToArray());
                    var saved = await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
                    CheckSaved(saved["models"]!, 4001); Assert.False(doc.IsDirty);
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
                    Assert.Equal(4000, imported!["fileCount"]!.GetValue<int>()); Assert.True(imported["filesTruncated"]!.GetValue<bool>());
                    Assert.Equal(64, imported["files"]!.AsArray().Count);
                    var content = imported["document"]!["contentEdits"]!;
                    Assert.Equal(4000, content["fileCount"]!.GetValue<int>()); Assert.True(content["filesTruncated"]!.GetValue<bool>());
                    Assert.Equal(64, content["files"]!.AsArray().Count);
                    var state = await Call("state", new()); Assert.Equal(4000, state["documents"]![0]!["contentEdits"]!["fileCount"]!.GetValue<int>());
                    Assert.Empty(state["documents"]![0]!["contentEdits"]!["files"]!.AsArray());
                    Assert.True(state["documents"]![0]!["contentEdits"]!["filesTruncated"]!.GetValue<bool>());
                    foreach (string pack in packs) Assert.Equal(expected[pack], await File.ReadAllBytesAsync(pack, token));
                    var contentExpected = doc.ContentEdits!.Documents.ToDictionary(d => d.Path, d => d.Bytes.ToArray());
                    saved = await Job("save_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision });
                    CheckSaved(saved["result"]!, 4000); Assert.False(doc.IsDirty);
                    Assert.Equal(0, saved["result"]!["RemainingPathCount"]!.GetValue<int>());
                    Assert.False(saved["result"]!["RemainingPathsTruncated"]!.GetValue<bool>());
                    foreach (var (path, bytes) in contentExpected) Assert.Equal(bytes, await File.ReadAllBytesAsync(path, token));
                    Assert.Equal(expected[world], await File.ReadAllBytesAsync(world, token));
                    await VerifyRetained(saved);
                    CloseDocuments();

                    // Each individual owner fits its preview bound; aggregation must not multiply
                    // those bounded path rows across every open document in global state.
                    for (int group = 0; group < 30; group++)
                    {
                        string groupFolder = Path.Combine(folder, $"group{group:D2}"); Directory.CreateDirectory(groupFolder);
                        string[] groupPacks = [.. Enumerable.Range(0, 64).Select(i => Path.Combine(groupFolder, $"texture{i:D2}.zbd"))];
                        foreach (string path in groupPacks) await File.WriteAllBytesAsync(path, expected[packs[0]], token);
                        await Job("open_document", new() { ["path"] = groupPacks[0] });
                        var owner = main.ViewModel.Documents.Single(d => d.Path == groupPacks[0]);
                        var groupImport = await Job("texture_import", new()
                        {
                            ["document"] = owner.SessionId.ToString(), ["revision"] = owner.Revision,
                            ["path"] = png, ["index"] = 0,
                            ["targets"] = groupPacks.Select(path => new { path, index = 0 }).ToArray()
                        });
                        Assert.Equal(64, groupImport["fileCount"]!.GetValue<int>());
                        Assert.False(groupImport["filesTruncated"]!.GetValue<bool>());
                        Assert.Equal(64, groupImport["files"]!.AsArray().Count);
                    }
                    Assert.Equal(30, main.ViewModel.Documents.Count);
                    var unbounded = new { documents = main.ViewModel.Documents.Select(d => new
                    {
                        contentEdits = new { files = d.ContentEdits!.Documents.Select(f => new { f.Path, destination = d.ContentEdits.TargetPath(f.Path) }) }
                    }) };
                    Assert.True(JsonSerializer.SerializeToNode(unbounded)!.ToJsonString().Length > 4 * 1024 * 1024);
                    state = await Call("state", new());
                    Assert.Equal(30, state["documents"]!.AsArray().Count);
                    foreach (var summary in state["documents"]!.AsArray())
                    {
                        Assert.Equal(64, summary!["contentEdits"]!["fileCount"]!.GetValue<int>());
                        Assert.Empty(summary["contentEdits"]!["files"]!.AsArray());
                        Assert.True(summary["contentEdits"]!["filesTruncated"]!.GetValue<bool>());
                    }
                    Assert.All(main.ViewModel.Documents, d => Assert.True(d.IsDirty));
                    var identities = main.ViewModel.Documents.Select(d => (Id: d.SessionId.ToString(), d.Path)).ToArray();
                    List<(string Id, string Path)> paged = []; int pageOffset = 0;
                    do
                    {
                        var page = await Call("state", new() { ["offset"] = pageOffset, ["limit"] = 7 });
                        Assert.Equal(30, page["total"]!.GetValue<int>()); Assert.Equal(pageOffset, page["offset"]!.GetValue<int>());
                        Assert.InRange(page["documents"]!.AsArray().Count, 1, 7);
                        paged.AddRange(page["documents"]!.AsArray().Select(d => (d!["id"]!.GetValue<string>(), d["Path"]!.GetValue<string>())));
                        pageOffset = page["nextOffset"]?.GetValue<int>() ?? -1;
                    } while (pageOffset >= 0);
                    Assert.Equal(identities, paged);
                    var maximum = await Call("state", new() { ["limit"] = 64 });
                    Assert.Equal(30, maximum["documents"]!.AsArray().Count); Assert.Null(maximum["nextOffset"]);
                    var end = await Call("state", new() { ["offset"] = 30, ["limit"] = 1 });
                    Assert.Equal(30, end["total"]!.GetValue<int>()); Assert.Empty(end["documents"]!.AsArray()); Assert.Null(end["nextOffset"]);
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
                    for (int i = 0; i < 4000; i++)
                        documents.Add(new(FormatRegistry.Default.OpenBytes(@"C:\" + new string('&', 190) + $"\\t{i:D4}.zbd", bytes)));
                    Assert.True(JsonSerializer.SerializeToNode(new { documents = documents.Select(d => new { id = d.SessionId, d.Path }) })!.ToJsonString().Length > 4 * 1024 * 1024);
                    int offset = 0; List<Guid> ids = [];
                    do
                    {
                        var page = StateDocumentPage.Select(documents, offset, 64);
                        Assert.Equal(4000, page.Total); Assert.InRange(page.Documents.Length, 1, 64);
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
