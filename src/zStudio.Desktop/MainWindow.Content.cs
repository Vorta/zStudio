using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using Microsoft.Win32;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private Task contentWork = Task.CompletedTask;
    private async void ContentWorkspaceChanged()
    {
        if (documentSaveDepth != 0 || ViewModel.SelectedDocument is not { TextureEdits: not null } doc || doc != shownDocument) return;
        await ShowAsset(doc, doc.SelectedAsset?.Record);
    }
    private static ScriptEditSession ScriptSession(DocumentModel doc) => doc.ScriptEdits ?? throw new StudioCommandException("unsupported", "Open an intact v7 prepared-script pack.");
    private static TextureEditSession TextureSession(DocumentModel doc) => doc.TextureEdits ?? throw new StudioCommandException("unsupported", "Open an intact texture pack.");
    private async Task ApplyContentAsync(DocumentModel doc, Func<CancellationToken, Task<PreparedContentEdit>> prepare, long revision, CancellationToken token)
    {
        CheckResourceContext(doc, revision);
        using var exclusion = BeginDocumentSave();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token, shutdownToken, PreviewOperation.Current);
        var edits = doc.ContentEdits!;
        if (doc.IsContentMirror) throw new StudioCommandException("owned_resource", "This pack is being edited by another open document. Save and close that document, then reload this pack before editing.");
        if (edits.HasExternalChanges()) throw new IOException("An affected file changed on disk. Reload or save your edits as a new copy.");
        var prepared = await prepare(cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested(); CheckResourceContext(doc, revision);
        var known = edits.Documents.Select(d => d.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (edits.HasExternalChanges() || prepared.Baselines.Values.Any(d => !known.Contains(d.Path) && FileStamp.Read(d.Path) != d.Stamp))
            throw new IOException("An affected file changed while preparing the edit. Reload before editing.");
        edits.Accept(prepared);
        using (PreviewOperation.Begin(CancellationToken.None)) await RefreshContentDependentsAsync(doc);
    }
    private async Task RefreshContentDependentsAsync(DocumentModel doc)
    {
        foreach (var open in ViewModel.Documents) { open.InvalidateMissionContext(); if (open != doc) open.InvalidateCleanPickupEdits(); }
        await previewWork;
        if (animation != null) await animation.RefreshModelContextAsync(resourceChanges: true);
        else if (shownDocument is { } shown && scene != null && shownAsset != null) await RefreshStaticSceneAsync(shown, shownAsset);
        else if (shownDocument is { ContentEdits: not null } current)
        {
            shownAsset = current.SelectedAsset?.Record;
            await ShowAsset(current, shownAsset);
        }
        if (propertiesWindow != null) await propertiesWindow.AssetRefreshWork;
        UpdateDocumentCommands();
    }
    private async Task UndoContentAsync(DocumentModel doc, bool redo)
    {
        if (doc.IsDisposed || !await ResolvePropertiesDraftsAsync(doc)) return;
        using var exclusion = BeginDocumentSave(); doc.ContentEdits!.UndoRedo(redo);
        using (PreviewOperation.Begin(CancellationToken.None)) await RefreshContentDependentsAsync(doc);
    }
    private async Task<ContentSaveResult> SaveContentAsync(DocumentModel doc, IReadOnlyDictionary<string, string>? destinations, CancellationToken token)
    {
        if (doc.IsContentMirror) throw new StudioCommandException("owned_resource", "Save from the document which owns the texture batch.");
        using var exclusion = BeginDocumentSave();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token, shutdownToken, PreviewOperation.Current);
        var result = await doc.ContentEdits!.SaveAsync(destinations, cancellation.Token);
        if (ViewModel.Resolver is { } resolver && result.SavedPaths.Count > 0) await resolver.InvalidateAsync(result.SavedPaths, doc.Lifetime.Token);
        doc.IsStale = false;
        foreach (var error in result.Errors) ViewModel.AddProblem(error, file: doc.Path);
        await RefreshContentDependentsAsync(doc);
        ViewModel.Status = result.Errors.Count == 0 ? "Saved and verified " + string.Join("; ", result.SavedPaths) : "Some files were saved; see Problems for the remaining file.";
        return result;
    }
    private async Task<bool> SaveContentDocumentAsync(DocumentModel doc, bool saveAs)
    {
        if (!await ResolvePropertiesDraftsAsync(doc)) return false;
        try
        {
            var edits = doc.ContentEdits!; var documents = edits.Documents.ToArray(); Dictionary<string, string>? targets = null;
            if (saveAs || documents.Any(d => PickupPlacementEditSession.IsProtectedPath(edits.TargetPath(d.Path))))
            {
                targets = new(StringComparer.OrdinalIgnoreCase);
                if (documents.Length == 1)
                {
                    SaveFileDialog dialog = new() { Title = "Save as a new file", FileName = Path.GetFileName(edits.TargetPath(doc.Path)), Filter = "ZBD file|*.zbd", OverwritePrompt = false };
                    if (dialog.ShowDialog(this) != true) return false; targets.Add(doc.Path, dialog.FileName);
                }
                else
                {
                    OpenFolderDialog dialog = new() { Title = "Save all affected texture packs into a new destination" };
                    if (dialog.ShowDialog(this) != true) return false;
                    foreach (var source in documents) targets.Add(source.Path, Path.Combine(dialog.FolderName, Path.GetFileName(source.Path)));
                }
            }
            return (await SaveContentAsync(doc, targets, CancellationToken.None)).Errors.Count == 0;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Report(ex); MessageBox.Show(this, ex.Message, "Save content"); return false; }
    }
    private ScriptPropertiesEditor ScriptAdapter(DocumentModel doc, Guid script, Guid? instruction) => new(doc, script, instruction, async (action, value, index) =>
    {
        var edits = ScriptSession(doc); long revision = doc.Revision;
        if (instruction == null)
        {
            uint time = action == "timestamp" ? uint.Parse(value, CultureInfo.InvariantCulture) : 0;
            await ApplyContentAsync(doc, ct => edits.PrepareEntryAsync(action, script, value, fileTime: time, token: ct), revision, CancellationToken.None);
        }
        else
        {
            var tokens = edits.Entry(script).Instructions.Single(i => i.Id == instruction).Tokens.ToList();
            switch (action)
            {
                case "token": tokens[index] = JsonSerializer.Deserialize<string>(value) ?? throw new InvalidDataException("Supply a JSON-quoted string."); break;
                case "add_argument": tokens.Add(""); break;
                case "remove_argument": tokens.RemoveAt(index); break;
                case "argument_up": (tokens[index - 1], tokens[index]) = (tokens[index], tokens[index - 1]); break;
                case "argument_down": (tokens[index + 1], tokens[index]) = (tokens[index], tokens[index + 1]); break;
                default: throw new InvalidDataException("Unknown token action.");
            }
            await ApplyContentAsync(doc, ct => edits.PrepareInstructionAsync(script, "set", instruction.Value, tokens, token: ct), revision, CancellationToken.None);
        }
    });
    private async Task<PropertiesWindow?> OpenScriptPropertiesAsync(DocumentModel doc, Guid script, Guid? instruction, bool automation = false)
    {
        long request = ++propertyRequest;
        if (automation) RequireNoDrafts(); else if (!await ResolvePropertiesDraftsAsync()) return null;
        if (request != propertyRequest || doc.IsDisposed) return null;
        var entry = ScriptSession(doc).Entry(script);
        if (instruction != null && !entry.Instructions.Any(i => i.Id == instruction)) throw new InvalidDataException("Instruction no longer exists.");
        var window = GetPropertiesWindow(); var adapter = ScriptAdapter(doc, script, instruction);
        bool accepted = window.SetScript(doc, adapter); if (!accepted) adapter.Dispose();
        PresentProperties(window, accepted); return accepted ? window : null;
    }
    private void RegisterContentCommands(StudioCommands r)
    {
        var scriptParameter = P("script", "string", "Stable script UUID from script_records.", true);
        RegisterJob(r, "texture_targets", "Discover sibling texture packs with matching names. Current Save As aliases are excluded because their source records already belong to this batch. Default replacement is this pack only. Duplicate matches require explicit record indices. Names are not identities.",
            [DocumentParameter, new("index", "integer", "Selected texture record index.", true, Minimum:0, Maximum:4095), .. PageParameters], false, async (a, token) =>
            {
                var d = TargetDocument(a); long revision = d.Revision;
                var targets = await TextureSession(d).DiscoverTargetsAsync(Int(a,"index"), ViewModel.Resolver!, token);
                CheckResourceContext(d, revision); return Result(new { d.Revision, targets = Page(targets, a, t => t.Path + " " + t.Name).Data });
            });
        RegisterJob(r, "texture_import", "Replace a texture from RGB/RGBA PNG (16 MiB; up to 4096×4096), or add a named texture when index is omitted. Replacement dimensions must match the selected record. Explicit sibling targets retain their dimensions, using premultiplied-alpha resampling and private palettes for indexed records. One undo step owns the complete batch.",
            [DocumentParameter, RevisionParameter, P("path","string","PNG input path.",true), new("index","integer","Texture to replace; omit to add.",Minimum:0,Maximum:4095), P("name","string","New texture name, required when adding."), new("targets","array","Optional explicit replacement targets, including the selected record; one per sibling pack, maximum 64.",Items:new("","object","Target identity.",Properties:[P("path","string","Texture pack path.",true),new("index","integer","Record index.",true,Minimum:0,Maximum:4095)]),MinItems:1,MaxItems:64)], false,
            async (a, token) =>
            {
                var d = TargetDocument(a, true); var edits = TextureSession(d);
                var targets = (a["targets"] as JsonArray)?.Select(n => new TextureTarget(Text((JsonObject)n!,"path"), Int((JsonObject)n!,"index"))).ToArray();
                await ApplyContentAsync(d, ct => edits.PrepareAsync(Text(a,"path"), a.ContainsKey("index") ? Int(a,"index") : null, Text(a,"name"), targets, ViewModel.Resolver!, ct), d.Revision, token);
                return Result(new { document = DocumentState(d), files = edits.Documents.Select(x => new { x.Path, destination = edits.TargetPath(x.Path), diagnostics = x.Diagnostics.Take(32) }) });
            });
        Register(r, "script_records", "Page scripts in stored order, or instructions of one script. UUIDs survive edits and undo. Token previews are capped at 64 characters and 16 tokens; source offsets refer to original bytes.", false,
            [DocumentParameter, P("script","string","Optional script UUID to list instructions."), .. PageParameters], a =>
            {
                var d = TargetDocument(a); var edits = ScriptSession(d);
                if (!a.ContainsKey("script")) return Result(new { d.Revision, scripts = Page(edits.Package.Entries.Select((e,i) => new { script=e.Id, index=i, e.SourceIndex, e.Name, e.FileTime, instructions=e.Instructions.Count }),a,e=>e.Name).Data });
                var entry = edits.Entry(GuidArg(a,"script"));
                return Result(new { d.Revision, instructions = Page(entry.Instructions.Select((i,n) => (Instruction:i,Index:n)),a,x=>x.Instruction.Tokens.FirstOrDefault()??"", x => new { instruction=x.Instruction.Id,index=x.Index,sourceOffset=x.Instruction.SourceOffset,tokens=x.Instruction.Tokens.Take(16).Select(t=>t.Length>64?t[..64]:t),truncated=x.Instruction.Tokens.Count>16||x.Instruction.Tokens.Any(t=>t.Length>64) }).Data });
            });
        RegisterJob(r, "script_entry_edit", "Add, duplicate, rename, delete, reorder or set the stored timestamp of a script. Unknown commands and unrelated bytes are preserved. No scripts execute and names/references are not rewritten. New timestamps default to zero.",
            [DocumentParameter,RevisionParameter,P("action","string","Entry action.",true,"add","duplicate","rename","delete","move","timestamp"),P("script","string","Required script UUID except add."),P("name","string","Name for add/duplicate/rename; 1–119 Latin-1 characters."),new("position","integer","Final index for move.",Minimum:0,Maximum:int.MaxValue),new("fileTime","integer","Raw stored timestamp.",Minimum:0,Maximum:uint.MaxValue)],false,async(a,token)=>
            {
                var d=TargetDocument(a,true); var edits=ScriptSession(d);
                await ApplyContentAsync(d,ct=>edits.PrepareEntryAsync(Text(a,"action"),GuidArg(a,"script"),Text(a,"name"),Int(a,"position",-1),a["fileTime"]?.GetValue<uint>()??0,ct),d.Revision,token);return Result(DocumentState(d));
            });
        RegisterJob(r, "script_instruction_edit", "Add, replace tokens, duplicate, delete or reorder an instruction. Tokens contain command followed by ordered arguments: 1–16 Latin-1 strings, no NUL, each at most 16384 characters. Move position is the final index; add defaults to append.",
            [DocumentParameter,RevisionParameter,scriptParameter,P("instruction","string","Required instruction UUID except add."),P("action","string","Instruction action.",true,"add","set","duplicate","delete","move"),new("tokens","array","Complete command and arguments for add/set.",Items:P("","string","Literal token."),MinItems:1,MaxItems:16),new("position","integer","Insertion/final index.",Minimum:0,Maximum:int.MaxValue)],false,async(a,token)=>
            {
                var d=TargetDocument(a,true); var edits=ScriptSession(d);var tokens=(a["tokens"]as JsonArray)?.Select(t=>t!.GetValue<string>()).ToArray();
                await ApplyContentAsync(d,ct=>edits.PrepareInstructionAsync(GuidArg(a,"script"),Text(a,"action"),GuidArg(a,"instruction"),tokens,Int(a,"position",-1),ct),d.Revision,token);return Result(DocumentState(d));
            });
        RegisterJob(r,"script_select","Select a script and optional instruction in the visible workspace, retaining the pinned Properties target.",
            [DocumentParameter,scriptParameter,P("instruction","string","Optional instruction UUID.")],false,async(a,token)=>
            {
                RequireNoDrafts();var d=TargetDocument(a);Guid entry=GuidArg(a,"script"),instruction=GuidArg(a,"instruction");var script=ScriptSession(d).Entry(entry);
                if(instruction!=Guid.Empty&&!script.Instructions.Any(i=>i.Id==instruction))throw new InvalidDataException("Instruction no longer exists.");
                ViewModel.SelectedDocument=d;d.Query="";d.KindFilter="All types";d.SelectedAsset=d.Assets.Single(x=>x.ResourceId==entry);SelectNavigatorSection(1);await previewWork;
                if(shownDocument!=d||d.SelectedAsset?.ResourceId!=entry)throw new StudioCommandException("context_changed","Script selection changed.");
                RefreshScriptGrid(d,d.SelectedAsset.Record); scriptGrid.SelectedItem=scriptGrid.Items.OfType<ScriptRow>().FirstOrDefault(i=>i.Id==instruction); StructuredPanel.SelectedItem=scriptTab;
                if(scriptGrid.SelectedItem!=null)scriptGrid.ScrollIntoView(scriptGrid.SelectedItem);return Result(DocumentState(d));
            });
        RegisterJob(r,"script_properties","Read generated fields/actions, open pinned script/instruction Properties, edit a field, or invoke an argument action. Field tokens are JSON-quoted. Mutations require revision and no pending drafts.",
            [DocumentParameter,scriptParameter,P("instruction","string","Optional instruction UUID."),P("action","string","Properties action.",true,"fields","open","edit","invoke"),new("revision","integer","Required revision for edit/invoke.",Minimum:0,Maximum:long.MaxValue),P("field","string","Field ID for edit."),P("value","string","Editor text."),P("propertyAction","string","Generated action ID for invoke.")],false,async(a,token)=>
            {
                string action=Text(a,"action");var d=TargetDocument(a,action is "edit" or "invoke");Guid entry=GuidArg(a,"script");Guid? instruction=a.ContainsKey("instruction")?GuidArg(a,"instruction"):null;
                var script=ScriptSession(d).Entry(entry);if(instruction!=null&&!script.Instructions.Any(i=>i.Id==instruction))throw new InvalidDataException("Instruction no longer exists.");
                if(action=="open") { var window=await OpenScriptPropertiesAsync(d,entry,instruction,true);if(window?.ScriptFields is not { } fields||fields.EntryId!=entry||fields.InstructionId!=instruction)throw new StudioCommandException("context_changed","Properties was superseded.");return Result(DocumentState(d)); }
                using var adapter=ScriptAdapter(d,entry,instruction);
                if(action=="edit")await adapter.WriteAutomationFieldAsync(Text(a,"field"),Text(a,"value"));
                if(action=="invoke")await adapter.InvokeAutomationActionAsync(Text(a,"propertyAction"));
                return Result(new { d.Revision,fields=adapter.DescribeAutomationFields() });
            });
    }
}
