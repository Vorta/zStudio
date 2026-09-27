using System.IO;
using System.IO.Pipes;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Export;
using Recoil.Zbd.Desktop;
using Recoil.Zbd.Mcp;
using Recoil.Zbd.Tests;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class ContentEditingMcpChecks
{
    internal static async Task Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "zstudio-content-protocol-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var token = deadline.Token;
        var main = new MainWindow { Left = -12000, ShowInTaskbar = false }; main.Show();
        try
        {
            string source = Path.Combine(root,"interp.zbd"), copy = Path.Combine(root,"copy.zbd");
            byte[] original = ContentFixture.Scripts(); await File.WriteAllBytesAsync(source,original,token);
            await using var host = new LocalMcpHost(main.Commands,"test");
            await using var pipe = new NamedPipeClientStream(".",host.Instance.Pipe,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);await pipe.ConnectAsync(token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(pipe,pipe),cancellationToken:token);
            await Job("open_document",new() { ["path"]=source });var doc=main.ViewModel.Documents.Single();var edits=doc.ScriptEdits!;
            var scripts=await Call("script_records",Args()); Guid entry=Guid.Parse(scripts["scripts"]!["items"]![0]!["script"]!.GetValue<string>());
            var instructions=await Call("script_records",Args(("script",entry)));Guid instruction=Guid.Parse(instructions["instructions"]!["items"]![0]!["instruction"]!.GetValue<string>());
            await Job("script_select",Args(("script",entry),("instruction",instruction)));
            var structured=(TabControl)main.FindName("StructuredPanel");Assert.Equal("Instructions",((TabItem)structured.SelectedItem).Header);
            var grid=Assert.IsType<DataGrid>(((TabItem)structured.SelectedItem).Content);Assert.NotNull(grid.SelectedItem);
            await Job("script_properties",Args(("action","open"),("script",entry),("instruction",instruction)));
            var pinned=main.OpenPropertiesWindow!.ScriptFields!;Assert.Equal(instruction,pinned.InstructionId);
            var command=Descendants(pinned).OfType<TextBox>().First(b=>!b.IsReadOnly);command.Text="unquoted";
            Assert.Equal("pending_drafts",(await Job("script_instruction_edit",Args(("script",entry),("instruction",instruction),("action","delete")),"failed"))["code"]!.GetValue<string>());
            var draft=await Call("drafts",new() { ["target"]="properties" });
            var invalid=await client.CallToolAsync("zstudio_resolve_drafts",new Dictionary<string,object?> { ["document"]=doc.SessionId.ToString(),["target"]="properties",["token"]=draft["drafts"]!["token"]!.GetValue<string>(),["action"]="apply" },cancellationToken:token);
            Assert.True(invalid.IsError);Assert.True(pinned.HasPendingDrafts);Assert.False(doc.IsDirty);
            command.Text="\"NewCommand\"";
            draft=await Call("drafts",new() { ["target"]="properties" });await Call("resolve_drafts",Args(("target","properties"),("token",draft["drafts"]!["token"]!.GetValue<string>()),("action","apply")));
            Assert.True(doc.IsDirty);Assert.Equal("NewCommand",edits.Entry(entry).Instructions[0].Tokens[0]);Assert.False(pinned.HasPendingDrafts);
            var fields=await Job("script_properties",Args(("action","fields"),("script",entry),("instruction",instruction)));
            string add=fields["fields"]!["actions"]!.AsArray().Single(a=>a!["Label"]!.GetValue<string>()=="Add argument")!["Id"]!.GetValue<string>();
            await Job("script_properties",Args(("action","invoke"),("script",entry),("instruction",instruction),("propertyAction",add)));
            Assert.Equal(4,edits.Entry(entry).Instructions[0].Tokens.Count);
            await Job("script_instruction_edit",Args(("script",entry),("instruction",instruction),("action","duplicate")));
            await Job("script_instruction_edit",Args(("script",entry),("instruction",instruction),("action","move"),("position",1)));
            Assert.Equal(instruction,pinned.InstructionId);Assert.Equal(instruction,edits.Entry(entry).Instructions[1].Id);
            await Job("script_instruction_edit",Args(("script",entry),("instruction",instruction),("action","delete")));Assert.Contains("deleted",pinned.TargetLabel);
            await Call("undo_redo",Args(("action","undo")));Assert.DoesNotContain("deleted",pinned.TargetLabel);
            var contextRow=doc.SelectedAsset!;
            await Job("script_entry_edit",Args(("script",entry),("action","move"),("position",1)));Assert.Equal(entry,doc.SelectedAsset!.ResourceId);
            Assert.Equal(instruction,pinned.InstructionId);Assert.Equal(0,doc.OriginalAsset(doc.SelectedAsset.Record)!.Index);
            const System.Reflection.BindingFlags reflection=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
            typeof(MainWindow).GetField("contextAsset",reflection)!.SetValue(main,contextRow);typeof(MainWindow).GetField("assetContextDocument",reflection)!.SetValue(main,doc);
            ((MenuItem)main.FindName("AssetPropertiesMenu")).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            while(main.OpenPropertiesWindow!.ScriptFields!.InstructionId!=null)await Task.Delay(10,token);
            Assert.Equal(entry,main.OpenPropertiesWindow.ScriptFields.EntryId);
            await Job("script_properties",Args(("action","open"),("script",entry),("instruction",instruction)));pinned=main.OpenPropertiesWindow.ScriptFields!;
            await Job("script_entry_edit",Args(("script",entry),("action","timestamp"),("fileTime",uint.MaxValue)));
            await Job("script_entry_edit",Args(("script",entry),("action","rename"),("name","renamed.zrd")));
            Capture(main,"script-instructions"); Capture(main.OpenPropertiesWindow!,"script-properties");
            await Job("save_document",Args(("destination",copy)));Assert.False(doc.IsDirty);Assert.Equal(original,await File.ReadAllBytesAsync(source,token));
            File.Delete(source);Assert.False(edits.HasExternalChanges());
            await Job("script_entry_edit",Args(("script",entry),("action","rename"),("name","saved-again.zrd")));
            await Job("save_document",Args());Assert.False(doc.IsDirty);
            await Job("script_entry_edit",Args(("script",entry),("action","rename"),("name","third-save.zrd")));await Job("save_document",Args());
            Assert.Equal("third-save.zrd",(await FormatRegistry.Default.OpenAsync(copy,token)).Assets[1].Name);
            Assert.Same(edits.Current.Documents[doc.Path],await main.ViewModel.Resolver!.OpenCachedAsync(doc.Path,token));
            await Call("close_document",Args());

            string first=Path.Combine(root,"texture16.zbd"),second=Path.Combine(root,"rtexture16.zbd"),png=Path.Combine(root,"replace.png");
            await File.WriteAllBytesAsync(first,ContentFixture.Texture(2,1,true),token);await File.WriteAllBytesAsync(second,ContentFixture.Texture(1,1,true),token);
            await File.WriteAllBytesAsync(png,PngEncoder.Encode(new(2,1,[0,0,0,255,0,0,255,128]),token),token);
            await Job("open_document",new() { ["path"]=second });var mirror=main.ViewModel.Documents.Single();
            await Job("open_document",new() { ["path"]=first });doc=main.ViewModel.Documents.Single(d=>d.Path==first);var textures=doc.TextureEdits!;
            var targets=await Job("texture_targets",Args(("index",0)));Assert.Equal(2,targets["targets"]!["total"]!.GetValue<int>());
            var targetList=new[] {new {path=first,index=0},new {path=second,index=0}};
            await Job("texture_import",Args(("path",png),("index",0),("targets",targetList)));
            Assert.True(doc.IsDirty);Assert.True(mirror.IsContentMirror);Assert.Same(textures.Current.Documents[second],mirror.PreviewDocument);
            Assert.Equal((byte)255,TextureDecoder.Decode(doc.PreviewDocument,doc.PreviewDocument.Assets[0],token).Rgba[3]);
            Capture(main,"texture-batch");
            var owner=doc;doc=mirror;
            var conflict=await Job("texture_import",Args(("path",png),("index",0)),"failed");Assert.Equal("owned_resource",conflict["code"]!.GetValue<string>());Assert.False(mirror.IsDirty);doc=owner;
            var stale=Args(("path",png),("index",0));stale["revision"]=0;Assert.Equal("revision_conflict",(await Job("texture_import",stale,"failed"))["code"]!.GetValue<string>());
            await Call("undo_redo",Args(("action","undo")));Assert.False(doc.IsDirty);Assert.Equal(ContentFixture.Texture(1,1,true),mirror.PreviewDocument.Bytes.ToArray());
            await Call("undo_redo",Args(("action","redo")));Assert.Same(textures.Current.Documents[second],mirror.PreviewDocument);
            await Job("save_document",Args());Assert.False(doc.IsDirty);Assert.False(textures.HasExternalChanges());
            await Job("texture_import",Args(("path",png),("name","added")));Assert.Equal(3,doc.Assets.Count);
            await Call("undo_redo",Args(("action","undo")));Assert.Equal(2,doc.Assets.Count);Assert.False(doc.IsDirty);
            string copies=Path.Combine(root,"copies");Directory.CreateDirectory(copies);
            var destinations=new Dictionary<string,string> { [first]=Path.Combine(copies,"texture16.zbd"),[second]=Path.Combine(copies,"rtexture16.zbd") };
            await Job("save_document",Args(("destinations",destinations)));
            await Job("open_document",new() { ["path"]=destinations[first] });var copyView=main.ViewModel.Documents.Single(d=>d.Path==destinations[first]);Assert.True(copyView.IsContentMirror);
            await Job("texture_import",Args(("path",png),("name","copy-visible")));Assert.Equal(3,copyView.Assets.Count);Assert.All(copyView.Assets,a=>Assert.Equal(copyView.Path,a.Record.Id.File));
            await Call("undo_redo",Args(("action","undo")));Assert.Equal(2,copyView.Assets.Count);
            await Call("close_document",new() { ["document"]=copyView.SessionId.ToString(),["revision"]=copyView.Revision });
            await Call("close_document",Args());await mirror.ContentMirrorWork;
            Assert.Equal(await File.ReadAllBytesAsync(second,token),mirror.PreviewDocument.Bytes.ToArray());
            doc=mirror;await Job("reload_document",Args());doc=main.ViewModel.Documents.Single();Assert.False(doc.IsContentMirror);
            Assert.Equal(await File.ReadAllBytesAsync(second,token),doc.PreviewDocument.Bytes.ToArray());await Call("close_document",Args());

            Dictionary<string,object?> Args(params(string Key,object Value)[] values)
            {var a=new Dictionary<string,object?> { ["document"]=doc.SessionId.ToString(),["revision"]=doc.Revision };foreach(var(k,v)in values)a[k]=v is Guid id?id.ToString():v;return a;}
            async Task<JsonNode> Call(string name,Dictionary<string,object?> a)
            {
                if(name is "script_records" or "resolve_drafts")a.Remove("revision");
                var result=await client.CallToolAsync("zstudio_"+name,a,cancellationToken:token);Assert.False(result.IsError==true,string.Join(";",result.Content.OfType<TextContentBlock>().Select(c=>c.Text)));return JsonNode.Parse(result.Content.OfType<TextContentBlock>().Single().Text)!;
            }
            async Task<JsonNode> Job(string name,Dictionary<string,object?> a,string expected="completed")
            {
                if(name is "script_select" or "texture_targets")a.Remove("revision");
                var job=await Call(name,a);string id=job["id"]!.GetValue<string>();
                while(job["State"]!.GetValue<string>()is "queued" or "running"){await Task.Delay(10,token);job=await Call("operation",new() { ["id"]=id });}
                Assert.True(job["State"]!.GetValue<string>()==expected,job.ToJsonString());return job["result"]!;
            }
        }
        finally { foreach(var doc in main.ViewModel.Documents.ToArray())main.ViewModel.CloseResolved(doc);main.Close();Directory.Delete(root,true); }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++){var child=VisualTreeHelper.GetChild(root,i);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
    private static void Capture(Window window,string name)
    {
        string? directory=Environment.GetEnvironmentVariable("ZSTUDIO_CONTENT_CAPTURE");if(string.IsNullOrEmpty(directory))return;
        Directory.CreateDirectory(directory);window.UpdateLayout();
        var bitmap=new System.Windows.Media.Imaging.RenderTargetBitmap((int)window.ActualWidth,(int)window.ActualHeight,96,96,PixelFormats.Pbgra32);bitmap.Render(window);
        var encoder=new System.Windows.Media.Imaging.PngBitmapEncoder();encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
        using var stream=File.Create(Path.Combine(directory,name+".png"));encoder.Save(stream);
    }
}
