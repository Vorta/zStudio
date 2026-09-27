using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private Task resourceWork = Task.CompletedTask;
    private readonly Dictionary<Guid, bool> resourceExpansion = [];
    private Guid? selectedResourceNode;
    private static bool SelectResourceNode(ResourceTreeItem item, Guid node)
    {
        if (item.Node.Id == node) { item.IsSelected = true; return true; }
        var path = item.Children.FirstOrDefault(c => c.Node.Find(node) != null);
        if (path == null) return false;
        item.IsExpanded = true; return SelectResourceNode(path, node);
    }
    private static ResourceEditSession ResourceSession(DocumentModel doc) => doc.ResourceEdits ?? throw new StudioCommandException("unsupported", "Open an intact ZAR archive or ZRD resource.");
    private async Task<ZrdNode> ResourceTreeAsync(DocumentModel doc, Guid member, CancellationToken token)
    {
        var edits = ResourceSession(doc); var item = edits.Member(member); long revision = doc.Revision;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token, shutdownToken, PreviewOperation.Current);
        var root = await Task.Run(() => edits.Tree(item, cancellation.Token), cancellation.Token);
        CheckResourceContext(doc, revision); return root;
    }
    private void CheckResourceContext(DocumentModel doc, long revision)
    {
        if (doc.IsDisposed || !ViewModel.Documents.Contains(doc)) throw new StudioCommandException("stale_document", "The document was closed.");
        if (doc.Revision != revision) throw new StudioCommandException("revision_conflict", "The document changed. Read its current revision and retry.");
    }
    private async Task ApplyResourceAsync(DocumentModel doc, Func<CancellationToken, Task<PreparedResourceEdit>> prepare, long revision, CancellationToken token)
    {
        CheckResourceContext(doc, revision);
        using var exclusion = BeginDocumentSave();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token, shutdownToken, PreviewOperation.Current);
        var edits = ResourceSession(doc);
        if (edits.TargetStamp != FileStamp.Read(edits.TargetPath)) throw new IOException("The resource changed on disk. Save your changes as a copy, then reload before editing.");
        var prepared = await prepare(cancellation.Token);
        cancellation.Token.ThrowIfCancellationRequested(); CheckResourceContext(doc, revision);
        edits.Accept(prepared);
        // Accepted snapshots outlive the request which prepared them.
        using (PreviewOperation.Begin(CancellationToken.None)) await RefreshResourceDependentsAsync(doc);
    }
    private async Task RefreshResourceDependentsAsync(DocumentModel doc)
    {
        if (aiPropertiesArchive != null && (aiPropertiesArchive.Equals(doc.Path, StringComparison.OrdinalIgnoreCase) || aiPropertiesArchive.Equals(doc.ResourceEdits?.TargetPath, StringComparison.OrdinalIgnoreCase))) propertiesWindow?.MarkAiSnapshotStale();
        foreach (var open in ViewModel.Documents) { open.InvalidateMissionContext(); if (open != doc) open.InvalidateCleanPickupEdits(); }
        await previewWork;
        if (shownDocument != doc && animation != null) await animation.RefreshModelContextAsync(resourceChanges: true);
        else if (shownDocument != doc && shownDocument is { } shown && scene != null && shownAsset != null) await RefreshStaticSceneAsync(shown, shownAsset);
        UpdateDocumentCommands();
    }
    private async Task UndoResourcesAsync(DocumentModel doc, bool redo)
    {
        if (doc.IsDisposed || !await ResolvePropertiesDraftsAsync(doc)) return;
        using var exclusion = BeginDocumentSave(); ResourceSession(doc).UndoRedo(redo);
        using (PreviewOperation.Begin(CancellationToken.None)) await RefreshResourceDependentsAsync(doc);
    }
    private async Task<string> SaveResourcesAsync(DocumentModel doc, string? destination, CancellationToken token)
    {
        using var exclusion = BeginDocumentSave();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token, shutdownToken, PreviewOperation.Current);
        var edits = ResourceSession(doc); doc.ClaimResourcePaths([doc.Path, edits.TargetPath, destination ?? edits.TargetPath]);
        string path = await edits.SaveAsync(destination, cancellation.Token);
        if (ViewModel.Resolver is { } resolver) await resolver.InvalidateAsync([path], doc.Lifetime.Token);
        doc.LastSavedCopy = path; doc.IsStale = false;
        await RefreshResourceDependentsAsync(doc); ViewModel.Status = "Saved and verified " + path; return path;
    }
    private async Task<bool> SaveResourceDocumentAsync(DocumentModel doc, bool saveAs)
    {
        if (!await ResolvePropertiesDraftsAsync(doc)) return false;
        try
        {
            var edits = ResourceSession(doc); string? destination = null;
            if (saveAs || PickupPlacementEditSession.IsProtectedPath(edits.TargetPath))
            {
                SaveFileDialog dialog = new() { Title = "Save resource as a new file", FileName = Path.GetFileName(edits.TargetPath), Filter = edits.IsArchive ? "ZAR archive|*.zbd" : "ZRD resource|*.zrd", OverwritePrompt = false };
                if (dialog.ShowDialog(this) != true) return false; destination = dialog.FileName;
            }
            await SaveResourcesAsync(doc, destination, CancellationToken.None); return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException) { Report(ex); MessageBox.Show(this, ex.Message, "Resource save"); return false; }
    }
    private ResourcePropertiesEditor ResourceAdapter(DocumentModel doc, Guid member, Guid? node)
        => new(doc, member, node, async (action, value) =>
        {
            var edits = ResourceSession(doc); long revision = doc.Revision;
            await ApplyResourceAsync(doc, token => node is Guid id ? edits.PrepareZrdAsync(member, id, action, value: value, token: token) : edits.PrepareArchiveAsync(action, member, value, token: token), revision, CancellationToken.None);
        });
    private async Task<PropertiesWindow?> OpenResourcePropertiesAsync(DocumentModel doc, Guid member, Guid? node, CancellationToken token = default, bool automation = false)
    {
        long request = ++propertyRequest;
        if (automation) RequireNoDrafts(); else if (!await ResolvePropertiesDraftsAsync()) return null;
        _ = ResourceSession(doc).Member(member);
        if (node is Guid id && (await ResourceTreeAsync(doc, member, token)).Find(id) == null) throw new InvalidDataException("This node no longer exists.");
        if (request != propertyRequest || doc.IsDisposed) return null;
        if (automation) RequireNoDrafts(); else if (!await ResolvePropertiesDraftsAsync()) return null;
        token.ThrowIfCancellationRequested();
        if (request != propertyRequest || doc.IsDisposed) return null;
        _ = ResourceSession(doc).Member(member);
        var window = GetPropertiesWindow(); var adapter = ResourceAdapter(doc, member, node);
        bool accepted = window.SetResource(doc, adapter); if (!accepted) adapter.Dispose();
        PresentProperties(window, accepted); return accepted ? window : null;
    }
    private static readonly StudioParameter MemberParameter = P("member", "string", "Stable member UUID from archive_members or assets.", true);
    private static readonly StudioParameter NodeParameter = P("node", "string", "Stable ZRD node UUID from zrd_nodes.", true);
    private void RegisterResourceCommands(StudioCommands r)
    {
        Register(r, "archive_members", "List edited resource members in stored order with stable identities and original source indices. Duplicate names are allowed. Standalone ZRD has one member.", false,
            [DocumentParameter, .. PageParameters], a => { var d = TargetDocument(a); return Result(new { d.Revision, members = Page(ResourceSession(d).Current.Members.Select((m, i) => new { member = m.Id, index = i, m.SourceIndex, m.Name, bytes = m.Data.Length }), a, m => m.Name).Data }); });
        RegisterJob(r, "archive_edit", "Add, replace, rename, duplicate, delete or reorder a ZAR member as one undoable edit. add_zrd creates an empty array. Names use Latin-1 and are not identities. Move position is the final zero-based index.",
            [DocumentParameter, RevisionParameter, P("action", "string", "Member operation.", true, "add", "add_zrd", "replace", "rename", "duplicate", "delete", "move"), P("member", "string", "Required member UUID except for add/add_zrd."), P("name", "string", "Required for add/add_zrd/rename/duplicate; 1–63 Latin-1 characters."), P("path", "string", "Input file for add/replace."), new("position", "integer", "Final index for move.", Minimum: 0, Maximum: int.MaxValue)], false,
            async (a, token) => { var d = TargetDocument(a, true); var edits = ResourceSession(d); await ApplyResourceAsync(d, ct => edits.PrepareArchiveAsync(Text(a,"action"), GuidArg(a,"member"), Text(a,"name"), Text(a,"path"), Int(a,"position",-1), ct), d.Revision, token); return Result(DocumentState(d)); });
        RegisterJob(r, "resource_select", "Select a member and optional ZRD node by stable UUID in the visible Data tree. Properties retains its pinned target.",
            [DocumentParameter, MemberParameter, P("node", "string", "Optional ZRD node UUID.")], false, async (a, token) =>
            {
                RequireNoDrafts(); var d = TargetDocument(a); Guid member = GuidArg(a,"member"), node = GuidArg(a,"node");
                _ = ResourceSession(d).Member(member);
                if (node != Guid.Empty && (await ResourceTreeAsync(d, member, token)).Find(node) == null) throw new InvalidDataException("Node no longer exists.");
                ViewModel.SelectedDocument = d; d.Query = ""; d.KindFilter = "All types"; d.SelectedAsset = d.Assets.Single(x => x.ResourceId == member); SelectNavigatorSection(1);
                await previewWork;
                if (shownDocument != d || d.SelectedAsset?.ResourceId != member) throw new StudioCommandException("context_changed", "Resource selection changed.");
                if (node != Guid.Empty)
                {
                    var root = CentralTree.Items.OfType<ResourceTreeItem>().SingleOrDefault();
                    if (root == null || !SelectResourceNode(root, node)) throw new InvalidDataException("ZRD preview is unavailable.");
                    selectedResourceNode = node; StructuredPanel.SelectedIndex = 0;
                }
                return Result(new { d.Revision, member, node = node == Guid.Empty ? (Guid?)null : node });
            });
        RegisterJob(r, "zrd_nodes", "Read an edited typed ZRD root, or page the immediate children of a node. Values use invariant editor text; strings are JSON-quoted, floats include raw bits. Formatting is bounded to a 4096-character prefix with valueTruncated; truncated text may be incomplete JSON. Node IDs survive edits and undo.",
            [DocumentParameter, MemberParameter, P("node", "string", "Optional parent node UUID; omit to return the root."), .. PageParameters], false, async (a, token) =>
            {
                var d = TargetDocument(a); var root = await ResourceTreeAsync(d, GuidArg(a,"member"), token); Guid id = GuidArg(a,"node");
                var parent = id == Guid.Empty ? null : root.Find(id) ?? throw new InvalidDataException("Node no longer exists.");
                long revision = d.Revision;
                var page = await Task.Run(() => Page((parent == null ? new[] { root } : parent.Children).Select((n, i) => { token.ThrowIfCancellationRequested(); return (Node: n, Index: i); }), a,
                    row => { token.ThrowIfCancellationRequested(); return row.Node.PreviewValue(4096).Value; }, row =>
                {
                    token.ThrowIfCancellationRequested(); var n = row.Node;
                    var preview = n.PreviewValue(4096);
                    return new { node = n.Id, parent = parent?.Id, index = row.Index, kind = n.Kind.ToString(), value = preview.Value, valueTruncated = preview.Truncated, bits = $"0x{n.Bits:X8}", children = n.Children.Count, originalOffset = n.SourceOffset };
                }), token);
                token.ThrowIfCancellationRequested(); CheckResourceContext(d, revision);
                return Result(new { d.Revision, nodes = page.Data });
            });
        RegisterJob(r, "zrd_edit", "Edit typed ZRD values or structure as one undoable operation. add inserts in the selected array. move uses parent UUID and final position after removal. Root deletion/duplication and cycles are rejected. Changing type replaces the old value/children.",
            [DocumentParameter, RevisionParameter, MemberParameter, NodeParameter, P("action", "string", "Node operation.", true, "set", "type", "add", "duplicate", "delete", "move"), P("kind", "string", "Type for add/type; default String.", false, "Int", "Float", "String", "Array"), P("value", "string", "Invariant int/float, explicit 0xXXXXXXXX float bits, or JSON-quoted Latin-1 string."), P("parent", "string", "Destination array UUID for move."), new("position", "integer", "Final child index; add defaults to append.", Minimum: 0, Maximum: int.MaxValue)], false,
            async (a, token) => { var d = TargetDocument(a, true); var edits = ResourceSession(d); var kind = a.ContainsKey("kind") ? Enum.Parse<ZrdKind>(Text(a,"kind")) : ZrdKind.String; await ApplyResourceAsync(d, ct => edits.PrepareZrdAsync(GuidArg(a,"member"), GuidArg(a,"node"), Text(a,"action"), kind, Text(a,"value"), GuidArg(a,"parent"), Int(a,"position",-1), ct), d.Revision, token); return Result(DocumentState(d)); });
        RegisterJob(r, "resource_properties", "Read generated resource Properties fields, open the pinned Properties window, or edit one current field. Node omitted targets the member. Values exceeding 16384 displayed characters use a read-only prefix; zrd_edit can replace the complete value and export retains full data. Editing requires revision and rejects pending drafts.",
            [DocumentParameter, MemberParameter, P("node", "string", "Optional ZRD node UUID."), P("action", "string", "Properties operation.", true, "fields", "open", "edit"), new("revision", "integer", "Required current revision for edit.", Minimum: 0, Maximum: long.MaxValue), P("field", "string", "Current field ID for edit."), P("value", "string", "Invariant field editor text for edit.")], false, async (a, token) =>
            {
                string action = Text(a,"action"); var d = TargetDocument(a, action == "edit"); Guid member = GuidArg(a,"member"); Guid? node = a.ContainsKey("node") ? GuidArg(a,"node") : null;
                _ = ResourceSession(d).Member(member);
                if (node is Guid id && (await ResourceTreeAsync(d, member, token)).Find(id) == null) throw new InvalidDataException("Node no longer exists.");
                if (action == "open")
                {
                    var window = await OpenResourcePropertiesAsync(d, member, node, token, true);
                    if (window?.ResourceFields is not { } pinnedFields || pinnedFields.MemberId != member || pinnedFields.NodeId != node)
                        throw new StudioCommandException("context_changed", "Properties was superseded before opening.");
                    return Result(DocumentState(d));
                }
                using var fields = ResourceAdapter(d, member, node);
                if (action == "edit") await fields.WriteAutomationFieldAsync(Text(a,"field"), Text(a,"value"));
                return Result(new { d.Revision, fields = fields.DescribeAutomationFields() });
            });
    }
}
