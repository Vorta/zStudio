using System.IO;
using System.Reflection;
using System.Text.Json.Nodes;
using zStudio.Core.Export;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Desktop;
using Xunit;

namespace zStudio.Desktop.Tests;

internal static class SourceResourceOperationChecks
{
    internal static async Task Run(MainWindow main, Func<string, Dictionary<string, object?>, string, Task<JsonNode>> job, CancellationToken token)
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-source-resource-operations-" + Guid.NewGuid().ToString("N"));
        string source = Path.Combine(root, "source"), destination = Path.Combine(root, "export");
        Directory.CreateDirectory(source);
        try
        {
            foreach (string file in new[] { "resource.zrd", "definition.zad", "compiled.zrd" })
            {
                byte[] bytes = file == "compiled.zrd" ? ZrdWriter.Write(ZrdText.Parse("VALUE ( 1 )", token), token) : "VALUE ( 1 )\n"u8.ToArray();
                await File.WriteAllBytesAsync(Path.Combine(source, file), bytes, token);
            }
            await job("open_root", new() { ["path"] = source }, "completed");
            foreach (string file in new[] { "resource.zrd", "definition.zad", "compiled.zrd" })
            {
                string path = Path.Combine(source, file);
                await job("open_document", new() { ["path"] = path }, "completed");
                var doc = main.ViewModel.Documents.Single(d => d.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                var arguments = new Dictionary<string, object?> { ["document"] = doc.SessionId.ToString() };
                Assert.Empty(Assert.IsType<JsonArray>((await job("validate", arguments, "completed"))["items"]));
                // Exercise the same helper reached by the native Validate menu, without opening a physical dialog.
                var guiValidation = (Task<List<StudioProblem>>)typeof(MainWindow)
                    .GetMethod("ValidateDocumentSourceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, [doc, token])!;
                Assert.Empty(await guiValidation);

                var exported = await job("export", new() { ["document"] = doc.SessionId.ToString(), ["destination"] = destination }, "completed");
                Assert.Equal(1, exported["Completed"]!.GetValue<int>());
                Assert.Empty(exported["Errors"]!.AsArray());
                string raw = Path.Combine(exported["Directory"]!.GetValue<string>(), "Zrd", "00000_" + file);
                Assert.Equal(doc.Document.Bytes.ToArray(), await File.ReadAllBytesAsync(raw, token));
                var companion = JsonNode.Parse(await File.ReadAllBytesAsync(raw + ".json", token))!;
                Assert.Equal(1, companion["children"]![1]!["children"]![0]!["value"]!.GetValue<int>());

                // The GUI's export helper exports the same document as the MCP command does.
                var guiExport = (Task<ExportResult>)typeof(MainWindow).GetMethod("ExportAssetsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(main, [doc, doc.Document.Assets.ToArray(), destination, true, null, 0, token])!;
                var jsonExport = await guiExport;
                Assert.Equal(1, jsonExport.Completed); Assert.Empty(jsonExport.Errors);
                string jsonFile = Path.Combine(jsonExport.Directory, "Zrd", "00000_" + file + ".json");
                Assert.Equal(1, JsonNode.Parse(await File.ReadAllBytesAsync(jsonFile, token))!["tree"]!["children"]![1]!["children"]![0]!["value"]!.GetValue<int>());
                // Neither validation nor export writes the original.
                Assert.Empty(Assert.IsType<JsonArray>((await job("validate", arguments, "completed"))["items"]));
                var original = await FormatRegistry.Default.OpenAsync(path, token);
                Assert.Equal(1u, ZrdDecoder.ReadAsset(original, Assert.Single(original.Assets), token).Children[1].Children[0].Bits);
                main.ViewModel.CloseResolved(doc);
            }
        }
        finally
        {
            foreach (var doc in main.ViewModel.Documents.Where(d => d.Path.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).ToArray())
                main.ViewModel.CloseResolved(doc);
            Directory.Delete(root, true);
        }
    }
}
