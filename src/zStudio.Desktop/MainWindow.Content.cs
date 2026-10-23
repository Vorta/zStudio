using System.IO;
using zStudio.Automation;
using zStudio.Core.Formats;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private static PreparedScriptPackage ScriptPackage(DocumentModel doc) => doc.Document.Scripts ?? throw new StudioCommandException("unsupported", "Open an intact v7 prepared-script pack.");
    private static PreparedScriptEntry ScriptEntry(DocumentModel doc, Guid id) => ScriptPackage(doc).Entries.SingleOrDefault(e => e.Id == id) ?? throw new InvalidDataException("The script no longer exists.");
    private void RegisterContentCommands(StudioCommands r)
    {
        var scriptParameter = P("script", "string", "Stable script UUID from script_records.", true);
        Register(r, "script_records", "Page scripts in stored order, or instructions of one script. UUIDs stay stable while the document is open. Token previews are capped at 64 characters and 16 tokens; source offsets refer to the file's bytes.", false,
            [DocumentParameter, P("script","string","Optional script UUID to list instructions."), .. PageParameters], a =>
            {
                var d = TargetDocument(a); var scripts = ScriptPackage(d);
                if (!a.ContainsKey("script")) return Result(new { d.Revision, scripts = Page(scripts.Entries.Select((e,i) => (Entry:e,Index:i)),a,x=>x.Entry.Name,
                    x => new { script=x.Entry.Id, index=x.Index, x.Entry.SourceIndex, x.Entry.Name, x.Entry.FileTime, instructions=x.Entry.Instructions.Count }).Data });
                var entry = ScriptEntry(d, GuidArg(a,"script"));
                return Result(new { d.Revision, instructions = Page(entry.Instructions.Select((i,n) => (Instruction:i,Index:n)),a,x=>x.Instruction.Tokens.FirstOrDefault()??"", x => new { instruction=x.Instruction.Id,index=x.Index,sourceOffset=x.Instruction.SourceOffset,tokens=x.Instruction.Tokens.Take(16).Select(t=>t.Length>64?t[..64]:t),truncated=x.Instruction.Tokens.Count>16||x.Instruction.Tokens.Any(t=>t.Length>64) }).Data });
            });
        RegisterJob(r,"script_select","Select a script and optional instruction in the visible workspace, retaining the pinned Properties target.",
            [DocumentParameter,scriptParameter,P("instruction","string","Optional instruction UUID.")],false,async(a,token)=>
            {
                RequireNoDrafts();var d=TargetDocument(a);Guid entry=GuidArg(a,"script"),instruction=GuidArg(a,"instruction");var script=ScriptEntry(d,entry);
                if(instruction!=Guid.Empty&&!script.Instructions.Any(i=>i.Id==instruction))throw new InvalidDataException("Instruction no longer exists.");
                ViewModel.SelectedDocument=d;d.Query="";d.KindFilter="All types";d.SelectedAsset=d.Assets.Single(x=>x.ResourceId==entry);SelectNavigatorSection(1);await previewWork;
                if(shownDocument!=d||d.SelectedAsset?.ResourceId!=entry)throw new StudioCommandException("context_changed","Script selection changed.");
                RefreshScriptGrid(d,d.SelectedAsset.Record); scriptGrid.SelectedItem=scriptGrid.Items.OfType<ScriptRow>().FirstOrDefault(i=>i.Id==instruction); StructuredPanel.SelectedItem=scriptTab;
                if(scriptGrid.SelectedItem!=null)scriptGrid.ScrollIntoView(scriptGrid.SelectedItem);return Result(DocumentState(d));
            });
    }
}
