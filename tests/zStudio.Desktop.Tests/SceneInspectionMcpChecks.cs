using System.IO;
using System.IO.Pipes;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Threading;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Rendering;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class SceneInspectionMcpChecks
{
    internal static async Task Run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        string root = Path.Combine(Path.GetTempPath(), "zstudio-inspection-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string archivePath = Path.Combine(root, "resources.zbd");
        var value = A(S("node_00"), A(I(12), A(F(1), F(2), F(3)), A(I(-7), I(-1), I(0))));
        byte[] zrd = ZrdWriter.Write(value);
        using (var data = new MemoryStream())
        {
            using var writer = new BinaryWriter(data, Encoding.Latin1, true); writer.Write(zrd);
            writer.Write(0); writer.Write(zrd.Length); byte[] entry = new byte[140]; Encoding.Latin1.GetBytes("net_01.zrd").CopyTo(entry, 0); writer.Write(entry);
            writer.Write(1); writer.Write(1); File.WriteAllBytes(archivePath, data.ToArray());
        }
        byte[] original = File.ReadAllBytes(archivePath);
        using var resolver = new AssetResolver(root);
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        using var viewport = new SceneViewport();
        var world = new ZbdDocument(Path.Combine(root, "gamez.zbd"), new(0, DateTime.MinValue), new(FormatFamily.GameZ, 15, Recognition.Supported, "fixture"), ReadOnlyMemory<byte>.Empty) { Scene = new() };
        world.Scene.Nodes.Add(new(0, "world", "world", null, [], [], new(), new()));
        var asset = world.Add(AssetKind.World, 0, "Whole world", 0, 0);
        // Real GameZ documents also have a model-edit session. Coordinate undo must
        // not take its model-replacement refresh path or expire the preview.
        typeof(ZbdDocument).GetProperty(nameof(ZbdDocument.GameZLayout))!.SetValue(world, new GameZSourceLayout(0, 0, 0, 0, 0, 0, 0, []));
        using var doc = new DocumentModel(world); doc.AttachResolver(resolver); main.ViewModel.Documents.Add(doc);
        typeof(MainViewModel).GetField("selectedDocument", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main.ViewModel, doc);
        var edits = await doc.GetPickupEditsAsync(resolver, deadline.Token);
        var archive = await resolver.OpenCachedAsync(archivePath, deadline.Token);
        var graph = MissionAiNetworks.Read(archive.Assets.Select(a => (archive, a)));
        var mission = MissionSceneLoader.Build(world, null, null, null, null);
        typeof(MissionSceneContext).GetProperty("AiNetworks")!.SetValue(mission, graph);
        typeof(SceneViewport).GetProperty("Mission")!.SetValue(viewport, mission);
        typeof(SceneViewport).GetProperty(nameof(SceneViewport.PreviewScene))!.SetValue(viewport, mission.Scene);
        viewport.SetAiNetworks(graph); viewport.SetAiOptions(true, true, null);
        Set("shownDocument", doc); Set("shownAsset", asset); Set("scene", viewport);
        ((ContentControl)main.FindName("SceneHost")).Content = viewport; ((ContentControl)main.FindName("SceneHost")).Visibility = Visibility.Visible;
        ((FrameworkElement)main.FindName("EmptyPreview")).Visibility = Visibility.Collapsed;
        Invoke("ConfigureAiScene", viewport); Invoke("AttachPickupEditor", doc);
        string preview = ((Guid)Get("previewId")!).ToString(); string target = "ai:" + graph.Id + ":" + graph.Networks[0].Nodes[0].Id;
        try
        {
            await using var host = new LocalMcpHost(main.Commands, "test");
            await using var pipe = new NamedPipeClientStream(".", host.Instance.Pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(deadline.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe, pipe), cancellationToken: deadline.Token);
            await Card("select", new() { ["target"] = target });
            await Card("select", new() { ["target"] = target, ["node"] = 0 }, "invalid_argument");
            var inspected = await Call("scene_inspect", new() { ["preview"] = preview });
            Assert.False(inspected["inspection"]!["Editable"]!.GetValue<bool>());
            var unlock = (ToggleButton)main.FindName("EditingUnlocked");
            Assert.False(unlock.IsChecked); Assert.True(doc.PickupsLocked);
            Assert.Equal("Unlock editing", AutomationProperties.GetName(unlock));
            Assert.Equal("Unlock", ((PreviewIcon)unlock.Content).Kind);
            await Edit("begin", error: "locked");
            await Lock(false);
            Assert.True(unlock.IsChecked); Assert.True(((PreviewIcon)unlock.Content).IsChecked);
            Assert.True((await Call("scene_inspect", new() { ["preview"] = preview }))["inspection"]!["Editable"]!.GetValue<bool>());
            unlock.IsChecked = false; Assert.True(doc.PickupsLocked);
            await Edit("begin", error: "locked");
            unlock.IsChecked = true; Assert.False(doc.PickupsLocked);
            var copy = await Card("copy", new() { ["field"] = "Authored placement XYZ" }); Assert.Contains("1", copy["text"]!.GetValue<string>());
            var tree = await Call("scene_tree", new() { ["document"] = doc.SessionId.ToString() });
            var begun = await Edit("begin"); string token = begun["draft"]!["token"]!.GetValue<string>();
            await Edit("cancel", new() { ["token"] = token });
            var restarted = await Edit("begin");
            Assert.NotEqual(token, restarted["draft"]!["token"]!.GetValue<string>());
            await Edit("cancel", new() { ["token"] = token }, "draft_conflict");
            token = restarted["draft"]!["token"]!.GetValue<string>();
            await Lock(true, "pending_drafts");
            Assert.False(doc.PickupsLocked); Assert.True(unlock.IsChecked);
            Assert.Equal(token, ((SceneInspectionCard)viewport.InspectionContent!).DraftToken);
            // Answer only this fixture thread's native XYZ prompt, without
            // moving/capturing the physical mouse or synthesizing keyboard input.
            bool canceled = false;
            var answer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
            answer.Tick += (_, _) => EnumThreadWindows(GetCurrentThreadId(), (handle, _) =>
            {
                var title = new StringBuilder(256); GetWindowText(handle, title, title.Capacity);
                if (title.ToString() != "Node position draft") return true;
                canceled = true; SendMessage(handle, 0x0111, 2, 0); return false; // WM_COMMAND, IDCANCEL
            }, 0);
            answer.Start();
            try { unlock.IsChecked = false; }
            finally { answer.Stop(); }
            Assert.True(canceled); Assert.False(doc.PickupsLocked); Assert.True(unlock.IsChecked);
            Assert.Equal(token, ((SceneInspectionCard)viewport.InspectionContent!).DraftToken);
            // Applying rechecks the lock even if it changed outside the GUI/MCP guard.
            doc.PickupsLocked = true;
            await Edit("apply", new() { ["token"] = token }, "locked");
            Assert.False(doc.IsDirty); doc.PickupsLocked = false;
            JsonObject TreeArgs(string action) => new() { ["document"] = doc.SessionId.ToString(), ["context"] = tree["context"]!.GetValue<string>(),
                ["preview"] = preview, ["action"] = action, ["node"] = 0 };
            await Call("scene_tree", TreeArgs("select"), "pending_drafts");
            await Call("scene_tree", TreeArgs("properties"), "pending_drafts");
            await Call("scene_tree", TreeArgs("reveal")); // Presentation must neither commit nor reject the draft.
            Assert.True((viewport.InspectionContent as SceneInspectionCard)!.HasDraft); Assert.False(doc.IsDirty);
            await Card("clear", new(), "pending_drafts");
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" }, "pending_drafts");
            await Call("close_document", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["discard"] = true }, "pending_drafts");
            await Edit("set", new() { ["token"] = token, ["position"] = new JsonArray(new string('1', 65), "6", "7") }, "invalid_argument");
            await Edit("set", new() { ["token"] = "old", ["position"] = new JsonArray("5", "6", "7") }, "draft_conflict");
            var partial = await Edit("set", new() { ["token"] = token, ["position"] = new JsonArray("-", "6", "7") });
            token = partial["draft"]!["token"]!.GetValue<string>();
            await Edit("apply", new() { ["token"] = token }, "invalid_argument"); Assert.False(doc.IsDirty);
            var valid = await Edit("set", new() { ["token"] = token, ["position"] = new JsonArray("5.125", "6", "7") });
            token = valid["draft"]!["token"]!.GetValue<string>();
            await Edit("apply", new() { ["token"] = token });
            Assert.Null(Get("inspectionDraft")); // Finished drafts must not retain a replaced viewport.
            Assert.True(doc.IsDirty); Assert.Equal(new Vector3(5.125f, 6, 7), edits.Position(edits.OtherCoordinates[0].Source));
            Assert.Equal(original, File.ReadAllBytes(archivePath));
            Assert.Equal(new Vector3(5.125f, 6, 7), viewport.AiNetworks.Networks[0].Nodes[0].Position);
            await Lock(true); Assert.False(unlock.IsChecked);
            await Edit("begin", error: "locked");
            await Call("scene_inspect", new() { ["preview"] = preview, ["target"] = target }, "stale_record");
            await Call("undo_redo", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["action"] = "undo" });
            Assert.Equal(preview, ((Guid)Get("previewId")!).ToString());
            Assert.False(doc.IsDirty); Assert.Equal(new Vector3(1, 2, 3), viewport.AiNetworks.Networks[0].Nodes[0].Position);
            await Lock(false);
            begun = await Edit("begin"); token = begun["draft"]!["token"]!.GetValue<string>();
            File.WriteAllBytes(archivePath, [.. original, 1]);
            await Edit("apply", new() { ["token"] = token }, "external_change");
            Assert.True((viewport.InspectionContent as SceneInspectionCard)!.HasDraft);
            await Edit("cancel", new() { ["token"] = token });
            File.WriteAllBytes(archivePath, original); File.SetLastWriteTimeUtc(archivePath, archive.Stamp.LastWriteUtc);
            await Card("clear", new());
            Assert.Null(viewport.SelectedInspection);

            Task<JsonNode> Card(string action, JsonObject args, string? error = null) { args["preview"] = preview; args["action"] = action; return Call("scene_card", args, error); }
            Task<JsonNode> Lock(bool locked, string? error = null) => Call("pickup_lock", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["locked"] = locked }, error);
            Task<JsonNode> Edit(string action, JsonObject? args = null, string? error = null)
            { args ??= new(); args["document"] = doc.SessionId.ToString(); args["revision"] = doc.Revision; return Card(action, args, error); }
            async Task<JsonNode> Call(string name, JsonObject args, string? error = null)
            {
                var response = await client.CallToolAsync("zstudio_" + name, args.ToDictionary(p => p.Key, p => (object?)p.Value), cancellationToken: deadline.Token);
                var json = JsonNode.Parse(response.Content.OfType<TextContentBlock>().Single().Text)!;
                if (error == null) Assert.False(response.IsError == true, json.ToJsonString()); else Assert.Equal(error, json["code"]!.GetValue<string>());
                return json;
            }
        }
        finally { (viewport.InspectionContent as SceneInspectionCard)?.CancelDraft(); while (edits.CanUndo) edits.Undo(); Set("scene", null); main.Close(); Directory.Delete(root, true); }
        object? Get(string name) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        void Set(string name, object? value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, value);
        void Invoke(string name, params object[] args) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, args);
    }
    private static ZrdNode A(params ZrdNode[] children) => ZrdNode.Create(ZrdKind.Array) with { Children = children };
    private delegate bool WindowCallback(nint handle, nint parameter);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint handle, uint message, nint wParam, nint lParam);
    private static ZrdNode S(string value) => ZrdNode.Create(ZrdKind.String) with { Text = value };
    private static ZrdNode I(int value) => ZrdNode.Create(ZrdKind.Int, value.ToString());
    private static ZrdNode F(float value) => ZrdNode.Create(ZrdKind.Float, value.ToString(System.Globalization.CultureInfo.InvariantCulture));
}
