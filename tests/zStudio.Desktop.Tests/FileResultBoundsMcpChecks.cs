using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Mcp;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

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
            await SourceSave();

            async Task SourceSave()
            {
                using var fixture = new SourceWorldFixture();
                string folder = "data/" + new string('&', 190);
                string original = "{\"asset\":{\"version\":\"2.0\"},\"nodes\":[{\"name\":\"external\"}],\"scenes\":[{\"nodes\":[0]}],\"scene\":0}";
                fixture.Write($"{folder}/model0.gltf", original);
                byte[] sentinel = await File.ReadAllBytesAsync(fixture.Path("gamegen/m1.gs"), token);
                try
                {
                    await Job("open_root", new() { ["path"] = fixture.Project, ["project"] = true });
                    await Job("source_world_open", new() { ["mission"] = "m1" });
                    // Seed an ordinary multi-file source transaction; the save/result contract is independent
                    // of Blender, whose checkout/update workflow is unavailable for linked source projects.
                    var sourceDocument = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
                    var sourceWorkspace = sourceDocument.SourceWorld!.Workspace;
                    List<(string Relative, byte[]? Content)> changes = [];
                    var gltf = JsonNode.Parse(original)!;
                    JsonArray buffers = [];
                    for (int i = 0; i < 65; i++)
                    {
                        string name = $"buffer{i}.bin";
                        changes.Add(($"{folder}/{name}", new byte[] { 0, (byte)i, 1, 2 }));
                        buffers.Add(new JsonObject { ["uri"] = name, ["byteLength"] = 4 });
                    }
                    gltf["buffers"] = buffers;
                    changes.Add(($"{folder}/model0.gltf", Encoding.UTF8.GetBytes(gltf.ToJsonString())));
                    sourceWorkspace.Apply("Multi-file source edit", changes, token);
                    var edited = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
                    var sourceState = await Call("state", new() { ["limit"] = 1 });
                    Assert.True(sourceState["documents"]![0]!.ToJsonString().Length <= StateDocumentPage.MaximumRowBytes(edited));
                    var workspace = edited.SourceWorld!.Workspace;
                    var expected = workspace.DirtyFiles.ToDictionary(p => p, p => workspace.Read(p, token) ?? throw new InvalidDataException("A fixture update unexpectedly deleted a file."));
                    Assert.Equal(66, expected.Count);
                    Assert.Equal(sentinel, await File.ReadAllBytesAsync(fixture.Path("gamegen/m1.gs"), token));
                    Assert.Equal(original, File.ReadAllText(fixture.Path($"{folder}/model0.gltf")));
                    // The same admitted producer must also have a usable refusal when another program creates
                    // the pending buffers. Both Core reload and the actual MCP job retain history and identities.
                    string[] conflicts = [.. expected.Keys.Where(p => p.EndsWith(".bin", StringComparison.Ordinal))];
                    byte[] external = [9, 8, 7, 6];
                    long revision = workspace.Revision;
                    var history = workspace.History.ToArray();
                    foreach (string path in conflicts) await File.WriteAllBytesAsync(fixture.Path(path), external, token);
                    try
                    {
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
                    Assert.Equal(66, saved["writtenCount"]!.GetValue<int>());
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
                    List<DocumentModel> longDocuments = [.. Enumerable.Range(0, 66).Select(i =>
                        new DocumentModel(FormatRegistry.Default.OpenBytes((i < 64 ? longPath : @"C:\short") + $"\\t{i:D4}.zbd", bytes)))];
                    documents.AddRange(longDocuments);
                    var large = StateDocumentPage.Select(longDocuments, 0, 64);
                    Assert.InRange(large.Documents.Length, 1, 63); Assert.Equal(large.Documents.Length, large.NextOffset);
                    Assert.All(large.Documents, d => Assert.StartsWith(longPath, d.Path, StringComparison.Ordinal));
                    Assert.True(large.Documents.Sum(StateDocumentPage.MaximumRowBytes) <= StateDocumentPage.MaximumBytes);
                    var options = new JsonSerializerOptions { IncludeFields = true };
                    options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
                    var projected = large.Documents.Select(MainWindow.DocumentState).ToArray();
                    Assert.True(JsonSerializer.SerializeToNode(projected, options)!.ToJsonString().Length <= StateDocumentPage.MaximumBytes);
                    for (int i = 0; i < projected.Length; i++)
                        Assert.True(JsonSerializer.SerializeToNode(projected[i], options)!.ToJsonString().Length <= StateDocumentPage.MaximumRowBytes(large.Documents[i]));
                    var single = StateDocumentPage.Select(documents, 0, 1);
                    Assert.Single(single.Documents); Assert.Equal(1, single.NextOffset);
                    Assert.Empty(StateDocumentPage.Select(documents, int.MaxValue, 64).Documents);
                    Assert.Throws<zStudio.Automation.StudioCommandException>(() => StateDocumentPage.Select(documents, -1, 64));
                    Assert.Throws<zStudio.Automation.StudioCommandException>(() => StateDocumentPage.Select(documents, 0, 65));
                    var oversized = new DocumentModel(FormatRegistry.Default.OpenBytes(@"C:\" + new string('&', StateDocumentPage.MaximumBytes) + ".zbd", bytes));
                    documents.Add(oversized);
                    Assert.Throws<zStudio.Automation.StudioCommandException>(() => StateDocumentPage.Select([oversized], 0, 1));
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
