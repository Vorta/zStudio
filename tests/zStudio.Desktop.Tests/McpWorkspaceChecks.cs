using System.IO;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class McpWorkspaceChecks
{
    internal static async Task Run(Application app)
    {
        var main = new MainWindow { Left=-12000,ShowInTaskbar=false }; main.Show();
        var package = new AnimationPackage { Prefix=new byte[72],Tail=[] };
        var entry = new AnimationEntry(new byte[308],0,0); entry.SetText(0,"mcp fixture");
        var sequence = new AnimationSequence(new byte[64]) { Name="sequence" }; sequence.Events.Add(AnimationCatalog.Create(10)); entry.Sequences.Add(sequence); package.Entries.Add(entry);
        var source = new ZbdDocument(Path.Combine(Path.GetTempPath(),"mcp-fixture.zbd"),new(0,DateTime.MinValue),new(FormatFamily.Animation,28,Recognition.Supported,"MCP fixture"),ReadOnlyMemory<byte>.Empty) { Animations=package };
        source.Add(AssetKind.Raw,0,"Metadata",0,0); source.Add(AssetKind.Raw,1,"Recovery fixture",0,0); var doc = new DocumentModel(source);
        main.ViewModel.Documents.Add(doc); main.ViewModel.SelectedDocument=doc;
        try
        {
            McpParityChecks.Run(main.Commands);
            byte[] imageBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVQIHWP4z8DwHwAFgAI/ScLttAAAAABJRU5ErkJggg==");
            main.Commands.Add(new("zstudio_test_image", "Synthetic protocol image", false, [], (_,_)=>Task.FromResult(new Recoil.Zbd.Automation.StudioResult(new JsonObject { ["image"]="fixture" },imageBytes))));
            await using var host = new LocalMcpHost(main.Commands,"test");
            await using var pipe = new NamedPipeClientStream(".",host.Instance.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            await pipe.ConnectAsync(5000);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe,pipe));
            var tools = await client.ListToolsAsync(); Assert.True(tools.Count>25);
            Assert.Contains(tools,t=>t.Name=="zstudio_property_edit");
            foreach(var tool in tools) Assert.Equal("object",tool.ProtocolTool.InputSchema.GetProperty("type").GetString());
            var imageResult = await client.CallToolAsync("zstudio_test_image");
            Assert.False(imageResult.IsError == true);
            Assert.Equal(imageBytes,imageResult.Content.OfType<ImageContentBlock>().Single().DecodedData.ToArray());
            var resources = await client.ListResourcesAsync(); Assert.Equal(2,resources.Count);
            var state=await Call("state",new()); Assert.Equal(doc.SessionId.ToString(),state["documents"]![0]!["id"]!.GetValue<string>());
            string originalPreview = state["preview"]!.GetValue<string>();
            await CancelPreviewJob("select_asset", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Raw", ["index"] = 1 }, "Raw #1:");
            Assert.Equal(1, doc.SelectedAsset!.Index);
            Assert.Equal(Visibility.Collapsed, ((TextBlock)main.FindName("EmptyPreview")).Visibility);
            Assert.Equal(Visibility.Visible, ((TabControl)main.FindName("StructuredPanel")).Visibility);
            await Call("inspect_asset", new() { ["document"] = doc.SessionId.ToString(), ["kind"] = "Raw", ["index"] = 1 });
            await Call("capture", new() { ["target"] = "preview" }, "stale_preview");
            await Call("capture", new() { ["target"] = "preview", ["preview"] = Guid.NewGuid().ToString() }, "stale_preview");
            main.WindowState = WindowState.Minimized;
            await Call("capture", new() { ["target"] = "window" }, "not_visible");
            main.WindowState = WindowState.Normal;
            var layoutBefore = await Call("workspace_view", new());
            string otherTheme = layoutBefore["theme"]!.GetValue<string>() == "Light" ? "Dark" : "Light";
            await Call("workspace_view", new() { ["changes"] = new JsonObject { ["theme"] = otherTheme, ["toolsTab"] = 999 } }, "unavailable_tab");
            Assert.True(JsonNode.DeepEquals(layoutBefore, await Call("workspace_view", new())));
            var filtered = await Call("animation_records", new() { ["document"] = doc.SessionId.ToString(), ["entry"] = 0, ["query"] = "SEQUENCE", ["limit"] = 1 });
            Assert.Equal(sequence.Id.ToString(), filtered["sequences"]!["items"]![0]!["id"]!.GetValue<string>());
            filtered = await Call("animation_records", new() { ["document"] = doc.SessionId.ToString(), ["entry"] = 0, ["query"] = "missing-sequence" });
            Assert.Equal(0, filtered["sequences"]!["total"]!.GetValue<int>());
            await Call("animation_structure", new() { ["document"] = doc.SessionId.ToString(), ["revision"] = doc.Revision, ["entry"] = 0, ["sequence"] = Guid.NewGuid().ToString(), ["action"] = "duplicate" }, "stale_record");
            Assert.Equal(0, doc.Revision);
            main.ViewModel.AddProblem("MCP search fixture", file: "fixture.zbd");
            filtered = await Call("problems", new() { ["query"] = "SEARCH FIXTURE" });
            Assert.Equal(1, filtered["total"]!.GetValue<int>());
            filtered = await Call("problems", new() { ["query"] = "missing-problem" });
            Assert.Equal(0, filtered["total"]!.GetValue<int>());
            var fields=await Call("property_fields",new() { ["document"]=doc.SessionId.ToString(),["entry"]=0 });
            string field=fields["fields"]!["fields"]!.AsArray().Single(x=>x!["Label"]!.GetValue<string>()=="Reset delay (s)")!["Id"]!.GetValue<string>();
            await Call("property_edit",new() { ["document"]=doc.SessionId.ToString(),["revision"]=doc.Revision,["entry"]=0,["field"]=field,["value"]="2.5" });
            Assert.Equal(2.5f,doc.AnimationEdits!.Package.Entries[0].F32(164)); Assert.True(doc.IsDirty);
            await Call("property_edit",new() { ["document"]=doc.SessionId.ToString(),["revision"]=0,["entry"]=0,["field"]=field,["value"]="3" },"revision_conflict");
            await Call("undo_redo",new() { ["document"]=doc.SessionId.ToString(),["revision"]=doc.Revision,["action"]="undo" });
            Assert.Equal(0,doc.AnimationEdits.Package.Entries[0].F32(164));
            main.OpenAnimationProperties(doc,0,Guid.Empty,Guid.Empty);
            var form=main.OpenPropertiesWindow!.AnimationFields!;
            var input=Descendants(form).OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="Reset delay (s)"); input.Text="-";
            await Call("property_edit",new() { ["document"]=doc.SessionId.ToString(),["revision"]=doc.Revision,["entry"]=0,["field"]=field,["value"]="3" },"pending_drafts");
            Assert.Equal("-",input.Text);
            var drafts=await Call("drafts",new() { ["target"]="properties" }); string token=drafts["drafts"]!["token"]!.GetValue<string>();
            await Call("resolve_drafts",new() { ["document"]=doc.SessionId.ToString(),["target"]="properties",["token"]=token,["action"]="apply" },"invalid_draft");
            Assert.True(form.HasPendingDrafts);
            await Call("resolve_drafts",new() { ["document"]=doc.SessionId.ToString(),["target"]="properties",["token"]=token,["action"]="discard" }); Assert.False(form.HasPendingDrafts);
            await Call("animation_structure",new() { ["document"]=doc.SessionId.ToString(),["revision"]=doc.Revision,["entry"]=0,["sequence"]=sequence.Id.ToString(),["action"]="add_event",["eventType"]=12 });
            Assert.Equal(2,doc.AnimationEdits.Package.Entries[0].Sequences[0].Events.Count);
            var keyframe = doc.AnimationEdits.Package.Entries[0].Sequences[0].Events[1];
            var keyFields = await Call("property_fields",new() { ["document"]=doc.SessionId.ToString(),["entry"]=0,["sequence"]=sequence.Id.ToString(),["event"]=keyframe.Id.ToString() });
            string addSegment = keyFields["fields"]!["actions"]!.AsArray().Single(x=>x!["Label"]!.GetValue<string>()=="+ segment")!["Id"]!.GetValue<string>();
            int originalSegments=keyframe.Keyframes().Count;
            await Call("property_action",new() { ["document"]=doc.SessionId.ToString(),["revision"]=doc.Revision,["entry"]=0,["sequence"]=sequence.Id.ToString(),["event"]=keyframe.Id.ToString(),["action"]=addSegment });
            Assert.Equal(originalSegments+1,doc.AnimationEdits.Package.Entries[0].Sequences[0].Events[1].Keyframes().Count);
            // A truncated payload leaves scheduling and catalog fields available
            // in the GUI. MCP must preserve the same partial inspection/edit path.
            var malformed = new AnimationEvent(keyframe.Bytes[..33]);
            byte[] malformedPayload = malformed.Bytes[32..];
            doc.AnimationEdits.Apply(0, "Malformed fixture", e => e.Sequences[0].Events.Add(malformed));
            var malformedTarget = new JsonObject { ["document"] = doc.SessionId.ToString(), ["entry"] = 0, ["sequence"] = sequence.Id.ToString(), ["event"] = malformed.Id.ToString() };
            var malformedFields = await Call("property_fields", malformedTarget);
            var diagnostic = malformedFields["fields"]!["fields"]!.AsArray().Single(f => f!["Label"]!.GetValue<string>() == "Keyframe diagnostic")!;
            Assert.True(diagnostic["readOnly"]!.GetValue<bool>());
            Assert.Contains("read-only", diagnostic["value"]!.GetValue<string>());
            Assert.Empty(malformedFields["fields"]!["actions"]!.AsArray());
            string thresholdField = malformedFields["fields"]!["fields"]!.AsArray().Single(f => f!["Label"]!.GetValue<string>() == "Threshold (s)")!["Id"]!.GetValue<string>();
            var badSegment = (JsonObject)malformedTarget.DeepClone(); badSegment["segment"] = 1;
            await Call("property_fields", badSegment, "invalid_argument");
            var editMalformed = (JsonObject)malformedTarget.DeepClone();
            editMalformed["revision"] = doc.Revision; editMalformed["field"] = thresholdField; editMalformed["value"] = "1.25";
            await Call("property_edit", editMalformed);
            var changedMalformed = doc.AnimationEdits.Package.Entries[0].Sequences[0].Events.Last();
            Assert.Equal(1.25f, changedMalformed.Threshold);
            Assert.Equal(malformedPayload, changedMalformed.Bytes[32..]);
            await Call("close_document",new() { ["document"]=doc.SessionId.ToString(),["revision"]=doc.Revision },"unsaved_changes");
            await Call("source_bytes",new() { ["document"]=doc.SessionId.ToString(),["offset"]=0,["length"]=4097 },"invalid_argument");
            await Call("state",new() { ["unexpected"]=true },"invalid_argument");
            await Call("close_document",new() { ["document"]=doc.SessionId.ToString(),["revision"]=doc.Revision,["discard"]=true });
            await Call("assets",new() { ["document"]=doc.SessionId.ToString() },"stale_document");
            await Call("capture", new() { ["target"] = "preview", ["preview"] = originalPreview }, "stale_preview");
            Assert.DoesNotContain(app.Windows.Cast<Window>(),w=>w.Title=="Resolve property input");

            // Cancel the real MCP operation during indexing, then prove that the
            // retained workspace can still open files after access is stopped.
            string root = Path.Combine(Path.GetTempPath(), "zstudio-mcp-cancel-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Task? shutdown = null;
            int indexed = 0;
            System.ComponentModel.PropertyChangedEventHandler changed = (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.Status) && main.ViewModel.Status.StartsWith("Indexed ", StringComparison.Ordinal))
                { indexed++; shutdown ??= main.StopMcpAsync(); }
            };
            try
            {
                for (int i = 0; i < 3; i++) File.WriteAllBytes(Path.Combine(root, i + ".zbd"), [1, 0, 0, 0, 0, 0, 0, 0]);
                main.ViewModel.PropertyChanged += changed;
                var job = await Call("open_root", new() { ["path"] = root });
                string id = job["id"]!.GetValue<string>();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (shutdown == null) await Task.Delay(10, deadline.Token);
                await shutdown.WaitAsync(deadline.Token);
                job = await Call("operation", new() { ["id"] = id });
                Assert.Equal("canceled", job["State"]!.GetValue<string>());
                Assert.Equal(1, indexed); Assert.False(main.ViewModel.IsBusy);
                var reopened = await main.ViewModel.OpenFileAsync(Path.Combine(root, "0.zbd"));
                Assert.NotNull(reopened); main.ViewModel.CloseResolved(reopened);
                main.ViewModel.PropertyChanged -= changed;
                string recoveryPath = Path.Combine(root, "recovery.zbd"); File.WriteAllBytes(recoveryPath, [255,255,255,255,255,255,255,255]);
                await CancelPreviewJob("open_document", new() { ["path"] = recoveryPath }, "Raw #0: recovery.zbd");
                Assert.Equal(recoveryPath, main.ViewModel.SelectedDocument!.Path);
                Assert.Equal(Visibility.Collapsed, ((TextBlock)main.FindName("EmptyPreview")).Visibility);
                main.ViewModel.CloseResolved(main.ViewModel.SelectedDocument);
                await CancelPreviewJob("open_document", new() { ["path"] = recoveryPath }, "Raw #0: recovery.zbd", close: true);
                Assert.Null(main.ViewModel.SelectedDocument);
            }
            finally { main.ViewModel.PropertyChanged -= changed; Directory.Delete(root, true); }

            async Task CancelPreviewJob(string name, JsonObject args, string statusPrefix, bool close = false)
            {
                Task? stopped = null; bool canceled = false;
                System.ComponentModel.PropertyChangedEventHandler cancel = (_, e) =>
                {
                    if (canceled || e.PropertyName != nameof(MainViewModel.Status) || !main.ViewModel.Status.StartsWith(statusPrefix, StringComparison.Ordinal)) return;
                    canceled = true;
                    stopped = main.StopMcpAsync();
                    if (close) main.ViewModel.CloseResolved(main.ViewModel.SelectedDocument!);
                };
                main.ViewModel.PropertyChanged += cancel;
                try
                {
                    var job = await Call(name, args);
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    do
                    {
                        await Task.Delay(10, deadline.Token);
                        job = await Call("operation", new() { ["id"] = job["id"]!.GetValue<string>() });
                    } while (job["State"]!.GetValue<string>() is "queued" or "running");
                    Assert.True(canceled, name + ": " + job.ToJsonString());
                    await stopped!;
                    Assert.Equal(close ? "failed" : "canceled", job["State"]!.GetValue<string>());
                    if (close) Assert.Equal("context_changed", job["result"]!["code"]!.GetValue<string>());
                }
                finally { main.ViewModel.PropertyChanged -= cancel; }
            }

            async Task<JsonNode> Call(string name,JsonObject arguments,string? error=null)
            {
                using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                var args=arguments.ToDictionary(p=>p.Key,p=>(object?)JsonSerializer.SerializeToElement(p.Value));
                var result=await client.CallToolAsync("zstudio_"+name,args,cancellationToken:timeout.Token);
                var json=JsonNode.Parse(result.Content.OfType<TextContentBlock>().First().Text)!;
                if(error==null) Assert.False(result.IsError == true,json.ToJsonString()); else { Assert.True(result.IsError); Assert.Equal(error,json["code"]!.GetValue<string>()); }
                return json;
            }
        }
        finally { main.OpenPropertiesWindow?.CloseResolved(); doc.AnimationEdits?.MarkSaved(); main.Close(); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i))) yield return child; }
}
