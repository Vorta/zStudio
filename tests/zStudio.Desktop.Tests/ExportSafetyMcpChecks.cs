using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Mcp;
using zStudio.Tests;
using Xunit;

namespace zStudio.Desktop.Tests;

/// <summary>
/// What the GUI and MCP promise around exports and source projects: path arguments are full paths, the Blender checkout is
/// an operation, a late Cancel in Initialize does not open the project, and lookup changes are compared with what was saved.
/// </summary>
internal static class ExportSafetyMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(120)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly); await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: token);
            await LateCancelChecks(main, token);
            using (SourceWorldFixture legacy = new())
            {
                legacy.Write("data/m1/meta/zones.json", "{}");
                string priorRoot = main.ViewModel.RootPath;
                Assert.Equal(SourceProject.LegacyFormatMessage, (await Assert.ThrowsAsync<InvalidDataException>(() => main.ViewModel.OpenRootAsync(legacy.Project, token))).Message);
                Assert.Equal(priorRoot, main.ViewModel.RootPath);
                foreach (bool project in new[] { false, true })
                {
                    var refusal = await Job("open_root", new() { ["path"] = legacy.Project, ["project"] = project }, "failed");
                    Assert.Equal(SourceProject.LegacyFormatMessage, refusal["message"]!.GetValue<string>());
                    Assert.Equal(priorRoot, main.ViewModel.RootPath);
                }
                Assert.Equal("{}", File.ReadAllText(legacy.Path("data/m1/meta/zones.json")));
                Assert.False(Directory.Exists(Path.Combine(legacy.Project, ".zstudio")));
            }
            await RelativePathChecks(main, Job);
            await SourceResourceOperationChecks.Run(main, Job, token);
            await ExportChoiceContextChecks(main, token);
            await TextureFailureAndMissingInputChecks(main, Job, token);
            await RgbaPackChecks(main, Job, token);
            await FontArchiveChecks(main, Job, token);
            await RejectedAnimationChecks(main, Job, token);
            await LookupBaselineChecks(main, Call, Job, token);

            async Task<JsonNode> Call(string name, Dictionary<string, object?> arguments)
            {
                var result = await client.CallToolAsync("zstudio_" + name, arguments, cancellationToken: token);
                Assert.False(result.IsError == true, string.Join(";", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
                return JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name, Dictionary<string, object?> arguments, string expected = "completed")
            {
                var job = await Call(name, arguments); string id = job["id"]!.GetValue<string>();
                while (job["State"]!.GetValue<string>() is "queued" or "running") { await Task.Delay(10, token); job = await Call("operation", new() { ["id"] = id }); }
                Assert.True(job["State"]!.GetValue<string>() == expected, job.ToJsonString()); return job["result"]!;
            }
        }
        finally { foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc); main.Close(); }
    }

    /// <summary>Must-have GUI/MCP parity: both targets discover and export the same opaque font bytes.</summary>
    private static async Task FontArchiveChecks(MainWindow main, Func<string, Dictionary<string, object?>, string, Task<JsonNode>> job, CancellationToken token)
    {
        using SourceWorldFixture fixture = new();
        byte[] font = [0, 1, 0, 0, 255, 128, 0];
        fixture.Write("data/common/fonts/Face.ttf", font);
        await main.ViewModel.OpenRootAsync(fixture.Project, token);
        var status = await job("source_status", new() { ["query"] = "zrdr.zbd" }, "completed");
        Assert.Contains("data/common/fonts/Face.ttf", status.ToJsonString());
        foreach (var target in ExportTargets.All)
        {
            var check = await job("source_export", new() { ["target"] = target.Name(), ["outputs"] = new[] { "zrdr.zbd" } }, "completed");
            Assert.Equal(0, check["failed"]!.GetValue<int>());
            var gui = await main.ExportSourceProjectAsync(null, ["zrdr.zbd"], false, token, target: target);
            Assert.Equal(0, gui.Failed); Assert.Equal(1, gui.Built);
            string destination = Path.Combine(fixture.Root, target.Name());
            var written = await job("source_export", new() { ["target"] = target.Name(), ["destination"] = destination, ["outputs"] = new[] { "zrdr.zbd" } }, "completed");
            Assert.True(written["written"]!.GetValue<bool>());
            var archive = await FormatRegistry.Default.OpenAsync(Path.Combine(destination, "zrdr.zbd"), token);
            var member = Assert.Single(archive.Assets);
            Assert.Equal("Face.ttf", member.Name);
            Assert.Equal(font, archive.Slice(member.Offset, member.Length).ToArray());
        }
    }

    private static async Task RejectedAnimationChecks(MainWindow main, Func<string, Dictionary<string, object?>, string, Task<JsonNode>> job, CancellationToken token)
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("data/m1/zrdr/gates.zad", "ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( ground ) ANIMATION_ROOT_NAME ( absent ) ) ) )");
        await main.ViewModel.OpenRootAsync(fixture.Project, token);
        var check = await job("source_export", new() { ["target"] = "v1", ["outputs"] = new[] { "m1/anim.zbd" } }, "completed");
        Assert.Equal(1, check["failed"]!.GetValue<int>());
        Assert.Contains("absent", check.ToJsonString());
        var gui = await main.ExportSourceProjectAsync(null, ["m1/anim.zbd"], false, token, target: ExportTarget.V1);
        Assert.Equal(1, gui.Failed);
        Assert.Contains(main.ViewModel.Problems, p => p.Severity == "Error" && p.Message.Contains("rejects", StringComparison.Ordinal));
        string destination = Path.Combine(fixture.Root, "export");
        await job("source_export", new() { ["target"] = "v1", ["destination"] = destination, ["outputs"] = new[] { "m1/anim.zbd" } }, "failed");
        Assert.Empty(Directory.GetFileSystemEntries(destination));
    }

    private static async Task TextureFailureAndMissingInputChecks(MainWindow main, Func<string, Dictionary<string, object?>, string, Task<JsonNode>> job, CancellationToken token)
    {
        using SourceWorldFixture fixture = new();
        const string model = "data/m1/models/m1.gltf", definition = "data/m1/zrdr/gates.zad";
        byte[] original = File.ReadAllBytes(fixture.Path(model)); fixture.Write(model, "invalid JSON");
        await main.ViewModel.OpenRootAsync(fixture.Project, token);
        var check = await job("source_export", new() { ["target"] = "v1", ["outputs"] = new[] { "m1/rtexture16.zbd" } }, "completed");
        Assert.Equal(1, check["failed"]!.GetValue<int>());
        var gui = await main.ExportSourceProjectAsync(null, ["m1/rtexture16.zbd"], false, token, target: ExportTarget.V1);
        Assert.Equal(1, gui.Failed);
        Assert.Contains(main.ViewModel.Problems, p => p.Severity == "Error" && p.Message.Contains("world does not assemble", StringComparison.Ordinal));
        string destination = Path.Combine(fixture.Root, "export");
        await job("source_export", new() { ["target"] = "v1", ["destination"] = destination, ["outputs"] = new[] { "m1/rtexture16.zbd" } }, "failed");
        Assert.Empty(Directory.GetFileSystemEntries(destination));
        fixture.Write(model, original);
        byte[] definitions = File.ReadAllBytes(fixture.Path(definition)); File.Delete(fixture.Path(definition));
        await job("source_world_open", new() { ["mission"] = "m1" }, "completed");
        var doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
        Assert.Contains(definition, doc.SourceBuild!.MissingInputs); Assert.False(doc.SourceInputsChanged());
        fixture.Write(definition, definitions); Assert.True(doc.SourceInputsChanged());
        main.ViewModel.CloseResolved(doc);
        await job("source_world_open", new() { ["mission"] = "m1" }, "completed");
        doc = main.ViewModel.Documents.Single(d => d.SourceWorld != null);
        string path = fixture.Path(model); DateTime stamp = File.GetLastWriteTimeUtc(path);
        string json = File.ReadAllText(path); Assert.Contains("ground", json);
        fixture.Write(model, json.Replace("ground", "GROUND", StringComparison.Ordinal)); File.SetLastWriteTimeUtc(path, stamp);
        Assert.True(await Task.Run(() => doc.SourceInputsChanged(token), token));
        main.ViewModel.CloseResolved(doc);

        fixture.Write(model, original);
        string stale = Path.Combine(destination, "m1/rtexture16.zbd"); Directory.CreateDirectory(Path.GetDirectoryName(stale)!); File.WriteAllBytes(stale, [1, 2, 3]);
        var exported = await job("source_export", new() { ["target"] = "v1", ["destination"] = destination, ["outputs"] = new[] { "m1/rtexture2.zbd" } }, "completed");
        Assert.Contains("m1/rtexture16.zbd", exported.ToJsonString());
        var exportedGui = await main.ExportSourceProjectAsync(destination, ["m1/rtexture2.zbd"], true, token, target: ExportTarget.V1);
        Assert.Contains(exportedGui.Notes, n => n.StartsWith("m1/rtexture16.zbd", StringComparison.Ordinal));
    }

    /// <summary>
    /// Must-have (required GUI/MCP contract): the 32-bit packs through the shared commands. source_status lists them,
    /// source_export_selection gives them the window's grid identities (rgbaimage.zbd among the shared files, the full-size
    /// tier's column in the rgbatexture section), the GUI's check and source_export build them, and the written files reopen.
    /// </summary>
    private static async Task RgbaPackChecks(MainWindow main, Func<string, Dictionary<string, object?>, string, Task<JsonNode>> job, CancellationToken token)
    {
        using SourceWorldFixture fixture = new();
        byte[] rgba = new byte[5 * 3 * 4];
        for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 17);
        byte[] flare = zStudio.Core.Export.PngEncoder.Encode(new(5, 3, rgba), token);
        fixture.Write("data/common/images/flare.png", flare);
        await main.ViewModel.OpenRootAsync(fixture.Project, token);
        var status = await job("source_status", new() { ["query"] = "rgba" }, "completed");
        Assert.Equal(["m1/rgbatexture64.zbd:rgbatextures", "m2/rgbatexture64.zbd:rgbatextures", "rgbaimage.zbd:rgbaimages"],
            status["outputs"]!["items"]!.AsArray().Select(o => $"{o!["path"]}:{o["family"]}").Order(StringComparer.Ordinal));
        var selection = await job("source_export_selection", new() { ["query"] = "rgba", ["target"] = "v2" }, "completed");
        var kind = selection["kinds"]!.AsArray().Single(k => k!["kind"]!.GetValue<string>() == "full-size rgbatexture")!;
        Assert.Equal(["rgbatexture", "full", "2"], new[] { "section", "label", "total" }.Select(k => kind[k]!.ToString()));
        var rows = selection["outputs"]!["items"]!.AsArray();
        Assert.Equal("shared", rows.Single(r => r!["path"]!.GetValue<string>() == "rgbaimage.zbd")!["section"]!.GetValue<string>());
        Assert.Equal("full-size rgbatexture", rows.Single(r => r!["path"]!.GetValue<string>() == "m1/rgbatexture64.zbd")!["kind"]!.GetValue<string>());
        var check = await main.ExportSourceProjectAsync(null, ["m1/rgbatexture64.zbd", "rgbaimage.zbd"], false, token, target: ExportTarget.V2);
        Assert.Equal((2, 0), (check.Built, check.Failed));
        string destination = Path.Combine(fixture.Root, "export");
        // MCP builds for one game: only target v2 (reRECOIL v2) writes the 32-bit packs.
        Assert.Equal("invalid_argument", (await job("source_export", new() { ["target"] = "v1", ["destination"] = destination, ["outputs"] = new[] { "rgbaimage.zbd" } }, "failed"))["code"]!.GetValue<string>());
        var written = await job("source_export", new() { ["destination"] = destination, ["outputs"] = new[] { "m1/rgbatexture64.zbd", "rgbaimage.zbd" }, ["target"] = "v2" }, "completed");
        Assert.True(written["written"]!.GetValue<bool>());
        Assert.All(written["outputs"]!.AsArray(), o => Assert.Equal(0, o!["reducedCount"]!.GetValue<int>()));
        var images = zStudio.Core.Formats.FormatRegistry.Default.OpenBytes("rgbaimage.zbd", await File.ReadAllBytesAsync(Path.Combine(destination, "rgbaimage.zbd"), token), token: token);
        Assert.Empty(images.Diagnostics);
        Assert.Equal(rgba, zStudio.Core.Formats.TextureDecoder.Decode(images, Assert.Single(images.Assets), token).Rgba);
        var textures = zStudio.Core.Formats.FormatRegistry.Default.OpenBytes("rgbatexture64.zbd", await File.ReadAllBytesAsync(Path.Combine(destination, "m1", "rgbatexture64.zbd"), token), token: token);
        Assert.Empty(textures.Diagnostics);
        Assert.Equal(["rock"], textures.Assets.Select(a => a.Name));

        // The window's pack boxes as MCP selects them (design/export-zbd-files-rgba.md §10): packs and sections intersect
        // with groups, the summaries count them with their sizes, and newThirtyTwoBit says how a 32-bit file the plan adds starts.
        static int Selected(JsonNode result, string summary, string key, string value) => result[summary]!.AsArray().Single(s => s![key]!.GetValue<string>() == value)!["selected"]!.GetValue<int>();
        // Each target's boxes (design/export-target-v1-v2.md §10.3): v1 direct3d and software, v2 direct3d and 32-bit.
        var all = await job("source_export_selection", new() { ["action"] = "get", ["target"] = "v2" }, "completed");
        var v1 = await job("source_export_selection", new() { ["action"] = "get" }, "completed");
        Assert.Equal(("v1", "v1", "v2", "v1"), (v1["target"]!.GetValue<string>(), v1["windowTarget"]!.GetValue<string>(), all["target"]!.GetValue<string>(), all["windowTarget"]!.GetValue<string>()));
        Assert.Equal(["direct3d", "software"], v1["packs"]!.AsArray().Select(f => f!["pack"]!.GetValue<string>()));
        Assert.Equal(["direct3d", "32-bit"], all["packs"]!.AsArray().Select(f => f!["pack"]!.GetValue<string>()));
        Assert.Equal(3, all["packs"]![1]!["total"]!.GetValue<int>());
        Assert.All(all["packs"]!.AsArray().Concat(v1["packs"]!.AsArray()), p => Assert.True(p!["estimatedBytes"]!.GetValue<long>() > 0));
        Assert.Equal("selected", all["newThirtyTwoBit"]!.GetValue<string>());
        Assert.Null(v1["newThirtyTwoBit"]);
        var items = new JsonArray([.. all["outputs"]!["items"]!.AsArray().Concat(v1["outputs"]!["items"]!.AsArray()).DistinctBy(r => r!["path"]!.GetValue<string>()).Select(r => r!.DeepClone())]);
        var row = items.Single(r => r!["path"]!.GetValue<string>() == "m1/rgbatexture64.zbd")!;
        Assert.Equal(("32-bit", "32-bit", true, 64), (row["pack"]!.GetValue<string>(), row["format"]!.GetValue<string>(), row["fullSize"]!.GetValue<bool>(), row["budgetMegabytes"]!.GetValue<int>()));
        Assert.Equal(["direct3d", "software"], new[] { "image.zbd", "m1/texturemax.zbd" }.Select(p => items.Single(r => r!["path"]!.GetValue<string>() == p)!["pack"]!.GetValue<string>()));
        // The estimates are the sizes the export writes, for every pack: m1's Direct3D, software and 32-bit packs and the image packs.
        string[] estimated = [.. items.Where(r => r!["estimatedBytes"] != null && r["path"]!.GetValue<string>() is var p && (p.StartsWith("m1/", StringComparison.Ordinal) || !p.Contains('/'))).Select(r => r!["path"]!.GetValue<string>())];
        Assert.Contains("m1/rtexture2.zbd", estimated); Assert.Contains("m1/texture2.zbd", estimated); Assert.Contains("image.zbd", estimated);
        string everyPack = Path.Combine(fixture.Root, "export-packs");
        // The 16-bit packs for v1, the 32-bit ones for v2, into one folder.
        string[] thirtyTwoBit = [.. estimated.Where(p => Path.GetFileName(p).StartsWith("rgba", StringComparison.Ordinal))];
        Assert.True((await job("source_export", new() { ["target"] = "v1", ["destination"] = everyPack, ["outputs"] = estimated.Except(thirtyTwoBit).ToArray() }, "completed"))["written"]!.GetValue<bool>());
        Assert.True((await job("source_export", new() { ["destination"] = everyPack, ["outputs"] = thirtyTwoBit, ["target"] = "v2" }, "completed"))["written"]!.GetValue<bool>());
        foreach (string pack in estimated)
            Assert.Equal(new FileInfo(Path.Combine(everyPack, pack)).Length, items.Single(r => r!["path"]!.GetValue<string>() == pack)!["estimatedBytes"]!.GetValue<long>());
        var sixteen = await job("source_export_selection", new() { ["action"] = "clear", ["packs"] = new[] { "direct3d", "software" } }, "completed");
        Assert.Equal((0, 0), (Selected(sixteen, "packs", "pack", "direct3d"), Selected(sixteen, "packs", "pack", "software")));
        // The other target's choices are its own: v2's Direct3D packs stay checked.
        Assert.Equal(3, Selected(await job("source_export_selection", new() { ["target"] = "v2" }, "completed"), "packs", "pack", "32-bit"));
        Assert.True(Selected(await job("source_export_selection", new() { ["target"] = "v2" }, "completed"), "packs", "pack", "direct3d") > 0);
        await job("source_export_selection", new() { ["action"] = "select", ["packs"] = new[] { "direct3d", "software" } }, "completed");
        // A box or section of the other target is refused, naming the target that has it.
        var refusedSoftware = await job("source_export_selection", new() { ["action"] = "clear", ["target"] = "v2", ["packs"] = new[] { "software" } }, "failed");
        Assert.Equal("invalid_argument", refusedSoftware["code"]!.GetValue<string>()); Assert.Contains("Target v1 has them", refusedSoftware["message"]!.GetValue<string>());
        Assert.Contains("Target v2 has it", (await job("source_export_selection", new() { ["action"] = "select", ["sections"] = new[] { "rgbatexture" } }, "failed"))["message"]!.GetValue<string>());
        Assert.Contains("target v1 builds it", (await job("source_export_selection", new() { ["action"] = "clear", ["target"] = "v2", ["outputs"] = new[] { "m1/texturemax.zbd" } }, "failed"))["message"]!.GetValue<string>());
        var cleared = await job("source_export_selection", new() { ["action"] = "clear", ["target"] = "v2", ["packs"] = new[] { "32-bit" } }, "completed");
        Assert.Equal((0, "clear"), (Selected(cleared, "packs", "pack", "32-bit"), cleared["newThirtyTwoBit"]!.GetValue<string>()));
        Assert.True(main.ViewModel.Settings.ExportRgbaClearedV2);
        var m2 = await job("source_export_selection", new() { ["action"] = "select", ["target"] = "v2", ["sections"] = new[] { "rgbatexture" }, ["groups"] = new[] { "m2" } }, "completed");
        Assert.Equal((1, "selected"), (Selected(m2, "sections", "section", "rgbatexture"), m2["newThirtyTwoBit"]!.GetValue<string>()));
        // m1's full-size pack cleared stays cleared when bigger textures move it from rgbatexture64 to rgbatexture128; its new
        // rgbatexture64, a smaller tier, starts checked.
        await job("source_export_selection", new() { ["action"] = "clear", ["target"] = "v2", ["kinds"] = new[] { "full-size rgbatexture" }, ["groups"] = new[] { "m1" } }, "completed");
        byte[] large = zStudio.Core.Export.PngEncoder.Encode(new(2048, 2048, new byte[2048 * 2048 * 4]), token);
        for (int i = 0; i < 5; i++) fixture.Write($"data/m1/textures/large{i}.png", large);
        var moved = await job("source_export_selection", new() { ["action"] = "get", ["target"] = "v2", ["query"] = "m1/rgbatexture" }, "completed");
        var files = moved["outputs"]!["items"]!.AsArray().ToDictionary(r => r!["path"]!.GetValue<string>(), r => (Full: r!["fullSize"]!.GetValue<bool>(), Selected: r["selected"]!.GetValue<bool>()));
        Assert.Equal((true, false), files["m1/rgbatexture128.zbd"]);
        Assert.Equal((false, true), files["m1/rgbatexture64.zbd"]);
        // The window: the Export for switch (the first tab stop) shows each target's boxes only, and the panel's boxes select
        // their packs' files.
        var dialog = main.CreateExportSelectionDialog();
        Modal(dialog, async () =>
        {
            while (dialog.Selection == null) { token.ThrowIfCancellationRequested(); await Task.Delay(10, token); }
            CheckBox? Box(string id) => FindAll<CheckBox>(dialog, b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == id).SingleOrDefault();
            RadioButton Segment(string id) => Find<RadioButton>(dialog, b => System.Windows.Automation.AutomationProperties.GetAutomationId(b) == id);
            Assert.Equal((ExportTarget.V1, true, false), (dialog.Target, Segment("TargetV1").IsChecked == true, Segment("TargetV2").IsTabStop));
            var (direct3D, softwarePacks) = (Box("Direct3DPacks")!, Box("SoftwarePacks")!);
            Assert.Null(Box("ThirtyTwoBitPacks"));
            Assert.Equal((true, true), (direct3D.IsChecked, softwarePacks.IsChecked));
            softwarePacks.IsChecked = false;
            Assert.Equal((0, dialog.Selection.Direct3DPacks.Files.Count), (dialog.Selection.SoftwarePacks.Selected, dialog.Selection.Direct3DPacks.Selected));
            Assert.Equal(dialog.Selection.SoftwarePacks.Files.Count, dialog.Selection.Sections.Single(s => s.Id == SourceExportSelection.SoftwareSection).Files.Count);
            direct3D.IsChecked = false;
            Assert.Equal(0, dialog.Selection.SixteenBitPacks.Selected);
            var v1Selection = dialog.Selection;
            // Choosing v2 shows its model: the 32-bit box instead of the software one, its own choices.
            Segment("TargetV2").IsChecked = true;
            Assert.Equal(ExportTarget.V2, dialog.Target); Assert.NotSame(v1Selection, dialog.Selection);
            Assert.Null(Box("SoftwarePacks"));
            var thirtyTwo = Box("ThirtyTwoBitPacks")!;
            Assert.Equal((true, (bool?)null), (Box("Direct3DPacks")!.IsChecked, thirtyTwo.IsChecked));
            thirtyTwo.IsChecked = true; // A mixed box selects all of its files.
            Assert.Equal(dialog.Selection.ThirtyTwoBitPacks.Files.Count, dialog.Selection.ThirtyTwoBitPacks.Selected);
            Assert.DoesNotContain(dialog.Selection.Files, f => f.Pack == SourceExportSelection.SoftwarePack);
            v1Selection.SetAll(true);
            dialog.Close();
        });
        // Closing remembered both targets' choices and the target shown last.
        Assert.False(main.ViewModel.Settings.ExportRgbaClearedV2);
        Assert.Equal("v2", StudioSettings.Load().ExportTarget);
        Assert.Empty(StudioSettings.Load().ExportUncheckedOutputs);
        var chosen = await job("source_export_selection", new() { ["action"] = "target", ["target"] = "v1" }, "completed");
        Assert.Equal(("v1", "v1", "v1"), (chosen["target"]!.GetValue<string>(), chosen["windowTarget"]!.GetValue<string>(), StudioSettings.Load().ExportTarget));
        await job("source_export_selection", new() { ["action"] = "select", ["target"] = "v2" }, "completed");
        // The destination cautions (design/export-target-v1-v2.md §6): v1 sees m1's version-200 animations, left by a v2
        // export beside the world it writes; v2's folder that is v1's too is said while it writes worlds or animations.
        string mixed = Path.Combine(fixture.Root, "mixed");
        await job("source_export", new() { ["target"] = "v2", ["destination"] = mixed, ["outputs"] = new[] { "m1/anim.zbd" } }, "completed");
        await job("source_export_selection", new() { ["action"] = "destination", ["destination"] = mixed }, "completed");
        var version200 = await job("source_export_selection", new() { ["action"] = "clear", ["outputs"] = new[] { "m1/anim.zbd" } }, "completed");
        Assert.Equal(["m1/anim.zbd"], version200["destinationVersion200"]!.AsArray().Select(p => p!["path"]!.GetValue<string>()));
        Assert.Equal((1, false), (version200["destinationVersion200Count"]!.GetValue<int>(), version200["destinationSharedWithV1"]!.GetValue<bool>()));
        var shared = await job("source_export_selection", new() { ["action"] = "destination", ["target"] = "v2", ["destination"] = mixed }, "completed");
        Assert.Equal((true, 0), (shared["destinationSharedWithV1"]!.GetValue<bool>(), shared["destinationVersion200Count"]!.GetValue<int>()));
        Assert.Equal((mixed, mixed), (StudioSettings.Load().ExportDestination, StudioSettings.Load().ExportDestinationV2));
        await job("source_export_selection", new() { ["action"] = "select", ["outputs"] = new[] { "m1/anim.zbd" } }, "completed");
        await job("source_export_selection", new() { ["action"] = "destination" }, "completed");
        await job("source_export_selection", new() { ["action"] = "destination", ["target"] = "v2" }, "completed");
    }

    private static async Task ExportChoiceContextChecks(MainWindow main, CancellationToken token)
    {
        using var original = new SourceWorldFixture(); using var replacement = new SourceWorldFixture();
        await main.ViewModel.OpenRootAsync(original.Project, token);
        long generation = main.ViewModel.WorkspaceGeneration;
        // The root changes while the GUI's off-thread output plan/destination confirmation is pending.
        await main.ViewModel.OpenRootAsync(replacement.Project, token);
        string destination = Path.Combine(replacement.Project, "..", "unexpected-export");
        var error = await Assert.ThrowsAsync<zStudio.Automation.StudioCommandException>(() =>
            main.ExportSourceProjectAsync(destination, ["m1/gamez.zbd"], true, token, expectedGeneration: generation));
        Assert.Equal("context_changed", error.Code);
        Assert.False(Directory.Exists(destination));
        // Returning to the same path must not revive the stale confirmation either.
        await main.ViewModel.OpenRootAsync(original.Project, token);
        await Assert.ThrowsAsync<zStudio.Automation.StudioCommandException>(() =>
            main.ExportSourceProjectAsync(destination, ["m1/gamez.zbd"], true, token, expectedGeneration: generation));
    }

    /// <summary>
    /// Cancel pressed once the work has passed its last check: the project is written, but the dialog does not open it (as
    /// source_reconstruct leaves a project cancelled while it opens); it says where the project is and stays open.
    /// </summary>
    private static async Task LateCancelChecks(MainWindow main, CancellationToken token)
    {
        string project = Path.Combine(Path.GetTempPath(), "zstudio-late-" + Guid.NewGuid().ToString("N"));
        TaskCompletionSource<SourceReconstructionReport> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = false;
        var dialog = new SourceInitializeDialog(main, Path.GetTempPath(), (_, _, _, _) => { started = true; return finished.Task; });
        string? shown = null; bool stayedOpen = false;
        bool? result = Modal(dialog, async () =>
        {
            while (!dialog.IsLoaded) await Task.Delay(10, token);
            Find<TextBox>(dialog, t => System.Windows.Automation.AutomationProperties.GetName(t) == "Source project folder").Text = project;
            Click(Find<Button>(dialog, b => b.Content as string == "Initialize and open"));
            while (!started) { token.ThrowIfCancellationRequested(); await Task.Delay(10, token); }
            // Cancel, then the work (which no longer looks at its token) finishes.
            var cancel = Find<Button>(dialog, b => b.Content as string == "Cancel");
            Click(cancel);
            finished.SetResult(new(project, 1, new Dictionary<string, int>(), [], []));
            var error = Find<TextBlock>(dialog, t => t.Foreground == System.Windows.Media.Brushes.IndianRed);
            while (dialog.IsVisible && error.Text.Length == 0) { token.ThrowIfCancellationRequested(); await Task.Delay(10, token); }
            shown = error.Text; stayedOpen = dialog.IsVisible;
            if (dialog.IsVisible) Click(cancel);
        });
        Assert.NotEqual(true, result);
        Assert.Null(dialog.Report);
        Assert.True(stayedOpen);
        Assert.Contains(project, shown);
        Assert.Contains("was not opened", shown);
        Assert.False(main.ViewModel.HasRoot);
    }

    /// <summary>File and folder arguments resolve against nothing but themselves: a relative one is refused before anything is read or written.</summary>
    private static async Task RelativePathChecks(MainWindow main, Func<string, Dictionary<string, object?>, string, Task<JsonNode>> job)
    {
        using var fixture = new SourceFixture();
        await job("open_root", new() { ["path"] = fixture.Corpus }, "completed");
        await Refused("open_document", new() { ["path"] = "m1/zrdr.zbd" });
        string archivePath = Path.Combine(fixture.Corpus, "m1", "zrdr.zbd");
        var archive = Document(await job("open_document", new() { ["path"] = archivePath }, "completed"));
        var asset = archive.Document.Assets[0];
        await Refused("export", new() { ["document"] = Id(archive), ["destination"] = "exported" });
        await Refused("model_bundle_export", new() { ["document"] = Id(archive), ["kind"] = asset.Kind.ToString(), ["index"] = asset.Index, ["destination"] = "bundle" });
        // Nothing was written beside zStudio.
        foreach (string name in new[] { "exported", "bundle" })
            Assert.False(Path.Exists(Path.Combine(AppContext.BaseDirectory, name)) || Path.Exists(Path.Combine(Environment.CurrentDirectory, name)), name);
        foreach (var doc in main.ViewModel.Documents.ToArray()) main.ViewModel.CloseResolved(doc);

        async Task Refused(string name, Dictionary<string, object?> arguments)
        {
            var failure = await job(name, arguments, "failed");
            Assert.Equal("invalid_argument", failure["code"]!.GetValue<string>());
            Assert.Contains("as a full path", failure["message"]!.GetValue<string>());
        }
        DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == state["id"]!.GetValue<string>());
    }

    /// <summary>
    /// The Blender checkout is an operation like the other long source operations; and lookup changes are compared with what
    /// was saved: read from the world the document holds (not its build's files, which another program may hold), and, for a
    /// world showing a build older than the saved sources, only from a build that reads saved sources alone.
    /// </summary>
    private static async Task LookupBaselineChecks(MainWindow main, Func<string, Dictionary<string, object?>, Task<JsonNode>> call, Func<string, Dictionary<string, object?>, string, Task<JsonNode>> job, CancellationToken token)
    {
        using var fixture = new SourceWorldFixture();
        // Both missions load m1's database. Two selectable crate holders reference the same lid file;
        // copying the whole holder changes each mission's lid lookup without editing inside an external source boundary.
        fixture.WriteReferencingDatabase(twice: true);
        foreach (string mission in new[] { "m1", "m2" })
        {
            fixture.Write($"gamegen/{mission}_zbd.gs", $"source support\\tex_fx{mission}.gw\r\nQuit\r\n");
            fixture.Write($"gamegen/support/tex_fx{mission}.gw", "FindNode lid\r\nQuit\r\n");
        }
        fixture.Write("gamegen/m2.gs", string.Join("\r\n", "set worldName world", "SetModelDirectory ..\\data\\m1\\models", "SetTextureDirectory ..\\data\\m1\\textures",
            "NewWorld %worldName%", "FindNode %worldName%", "GameGenSetWorld %worldName%", "FindNode %worldName%", "WorldOrigin 0.0 512.0", "WorldExtents 512.0 -512.0", "WorldPartition 256 -256",
            "LoadGameGen m1.flt m1.flt", "DeleteTree m1.flt", "GameZWriteZBDFile ..\\m2\\gamez.zbd", "Quit", ""));
        await job("open_root", new() { ["path"] = fixture.Project }, "completed");

        // The real shared command creates a graph checkout without changing source history.
        var checkout = await job("source_blender_checkout", new() { ["model"] = "data/m1/models/m1.gltf" }, "completed");
        Assert.Equal(2, checkout["version"]!.GetValue<int>());
        Assert.True(File.Exists(checkout["input"]!.GetValue<string>()));
        var extension = await call("source_blender_extension", new());
        Assert.Equal("5.2.0", extension["blenderMin"]!.GetValue<string>());
        var listedCheckouts = await call("source_blender_checkouts", new());
        Assert.Equal(1, listedCheckouts["checkoutCount"]!.GetValue<int>());
        var m2 = Document((await job("source_world_open", new() { ["mission"] = "m2" }, "completed"))["document"]!);
        var m1 = Document((await job("source_world_open", new() { ["mission"] = "m1" }, "completed"))["document"]!);
        Assert.True(m2.IsDisposed); Assert.Same(m1, Assert.Single(main.ViewModel.Documents));
        var checkedOut = SourceBlender.Find(fixture.Project, checkout["id"]!.GetValue<string>());
        string blenderSession = Path.Combine(fixture.Root, "blender-session");
        SourceBlender.PrepareBlenderImport(Path.Combine(checkedOut.Folder, "input"), checkedOut.Model, blenderSession, token);
        var manifest = JsonNode.Parse(File.ReadAllBytes(Path.Combine(blenderSession, "session.json")))!;
        JsonArray exportedModels = [];
        foreach (var entry in manifest["models"]!.AsArray())
        {
            string path = entry!["path"]!.GetValue<string>();
            string input = Path.Combine(blenderSession, "input", path);
            var json = JsonNode.Parse(File.ReadAllBytes(input))!;
            foreach (string kind in new[] { "nodes", "meshes", "materials" })
                foreach (var item in json[kind] as JsonArray ?? []) item!["extras"]![zStudio.Core.Worlds.WorldGltf.Key] ??= new JsonObject();
            json["scenes"]![0]!["extras"] ??= new JsonObject();
            json["scenes"]![0]!["extras"]![zStudio.Core.Worlds.WorldGltf.Key] ??= new JsonObject();
            var metadata = JsonNode.Parse(File.ReadAllBytes(Path.Combine(blenderSession, entry["metadata"]!.GetValue<string>())))!;
            foreach (var mesh in metadata["meshes"]!.AsArray())
            {
                int index = mesh!["index"]!.GetValue<int>(), polygon = 0, face = 0, primitive = 0;
                foreach (var item in mesh["primitives"]!.AsArray())
                {
                    JsonArray origins = [], zones = [], faces = [];
                    foreach (var count in item!["triangles"]!.AsArray())
                    {
                        for (int i = 0; i < count!.GetValue<int>(); i++)
                        { origins.Add(polygon); zones.Add(mesh["zones"]!["polygonZones"]![polygon]!.DeepClone()); faces.Add(++face); }
                        polygon++;
                    }
                    var primitiveJson = json["meshes"]![index]!["primitives"]![primitive++]!;
                    primitiveJson["extras"] ??= new JsonObject();
                    primitiveJson["extras"]!["zstudio_blender"] =
                        new JsonObject { ["origins"] = origins, ["zones"] = zones, ["faces"] = faces };
                }
            }
            if (path == checkedOut.Model)
            {
                json["scenes"]![0]!["extras"] ??= new JsonObject();
                json["scenes"]![0]!["extras"]![zStudio.Core.Worlds.WorldGltf.Key] ??= new JsonObject();
                json["scenes"]![0]!["extras"]![zStudio.Core.Worlds.WorldGltf.Key]!["loadRoot"] ??= new JsonObject { ["zone"] = "any" };
                json["scenes"]![0]!["extras"]![zStudio.Core.Worlds.WorldGltf.Key]!["loadRoot"]!["landmark"] = true;
            }
            File.WriteAllText(input, json.ToJsonString()); exportedModels.Add(new JsonObject { ["path"] = path });
        }
        string edits = Path.Combine(blenderSession, "input", "edit.json");
        File.WriteAllText(edits, new JsonObject { ["protocol"] = 1, ["session"] = blenderSession, ["models"] = exportedModels }.ToJsonString());
        SourceBlender.PrepareBlenderExport(edits, checkedOut.Outbox, token);
        byte[] beforeBlender = File.ReadAllBytes(fixture.Path(checkedOut.Model));
        m1 = Document((await job("source_blender_update", new() { ["document"] = Id(m1), ["revision"] = m1.Revision, ["checkout"] = checkedOut.Id }, "completed"))["document"]!);
        Assert.True(m1.IsDirty); Assert.Equal(beforeBlender, File.ReadAllBytes(fixture.Path(checkedOut.Model)));
        m1 = Document(await call("undo_redo", new() { ["document"] = Id(m1), ["revision"] = m1.Revision, ["action"] = "undo" }));
        Assert.DoesNotContain(main.ViewModel.Problems, p => Changed(p, "m1") || Changed(p, "m2"));
        // Copy the last holder: the new lid follows every prior lid, so the runtime lookup changes.
        m1 = await Duplicate(m1, "crate_b", "crate2");
        Assert.Contains(main.ViewModel.Problems, p => Changed(p, "m1") && p.Message.Contains("crate2/lid", StringComparison.Ordinal));

        // Saving persists source edits and establishes its in-memory lookup baseline without generated archives.
        await job("save_document", new() { ["document"] = Id(m1), ["revision"] = m1.Revision }, "completed");
        Assert.DoesNotContain(main.ViewModel.Problems, p => Changed(p, "m1") || Changed(p, "m2"));

        // Switching to m2 after the verified save establishes its baseline from saved sources.
        m2 = Document((await job("source_world_open", new() { ["mission"] = "m2" }, "completed"))["document"]!);
        Assert.True(m1.IsDisposed); Assert.DoesNotContain(main.ViewModel.Problems, p => Changed(p, "m2"));
        m1 = Document((await job("source_world_open", new() { ["mission"] = "m1" }, "completed"))["document"]!);
        // Another edit remains local; changing maps cannot silently carry it into a different document.
        int ground = m1.Document.Scene!.Nodes.First(n => n.Name == "ground" && m1.SourceBuild!.Provenance.ContainsKey(n.Index)).Index;
        m1 = Document((await job("source_world_object_edit", new() { ["document"] = Id(m1), ["revision"] = m1.Revision, ["node"] = ground, ["position"] = new Dictionary<string, object?> { ["x"] = 5, ["y"] = 0, ["z"] = -5 } }, "completed"))["document"]!);
        Assert.Equal("unsaved_changes", (await job("source_world_open", new() { ["mission"] = "m2" }, "failed"))["code"]!.GetValue<string>());
        Assert.Same(m1, Assert.Single(main.ViewModel.Documents));
        // m1's next copy, of crate2, is compared with what was saved: its lid is found now, not crate2's.
        m1 = await Duplicate(m1, "crate2", "crate3");
        Assert.Contains(main.ViewModel.Problems, p => Changed(p, "m1") && p.Message.Contains("crate3/lid", StringComparison.Ordinal));

        async Task<DocumentModel> Duplicate(DocumentModel doc, string source, string name)
        {
            int crate = doc.Document.Scene!.Nodes.First(n => n.Name == source && doc.SourceBuild!.Provenance.ContainsKey(n.Index)).Index;
            return Document((await job("source_world_object_edit", new() { ["document"] = Id(doc), ["revision"] = doc.Revision, ["node"] = crate, ["action"] = "duplicate", ["name"] = name }, "completed"))["document"]!);
        }
        bool Changed(StudioProblem p, string mission) => p.File == Path.Combine(fixture.Project, "gamegen", mission + ".gs")
            && p.Message.Contains($"FindNode lid in gamegen/support/tex_fx{mission}.gw finds", StringComparison.Ordinal) && p.Message.Contains("when the world was opened or last saved", StringComparison.Ordinal);
        DocumentModel Document(JsonNode state) => main.ViewModel.Documents.Single(d => d.SessionId.ToString() == state["id"]!.GetValue<string>());
    }

    private static string Id(DocumentModel d) => d.SessionId.ToString();
    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
    /// <summary>A modal dialog runs a nested message loop: the queued driver runs inside it until the dialog closes.</summary>
    private static bool? Modal(Window window, Func<Task> drive)
    {
        Exception? failure = null;
        _ = window.Dispatcher.InvokeAsync(async () =>
        {
            try { await drive(); }
            catch (Exception ex) { failure = ex; if (window.IsVisible) window.Close(); }
        });
        bool? shown = window.ShowDialog();
        if (failure != null) throw new Xunit.Sdk.XunitException("Driving the dialog failed: " + failure);
        return shown;
    }
    private static T Find<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is T found && match(found)) return found;
            try { return Find(node, match); } catch (InvalidOperationException) { }
        }
        throw new InvalidOperationException($"No {typeof(T).Name} matched.");
    }
    /// <summary>Every element of the logical tree under <paramref name="root"/> that matches (none for an element not in the window).</summary>
    private static IEnumerable<T> FindAll<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject
    {
        foreach (object child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject node) continue;
            if (node is T found && match(found)) yield return found;
            foreach (var inner in FindAll(node, match)) yield return inner;
        }
    }
}
