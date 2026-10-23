using System.IO;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Formats;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private readonly Dictionary<Guid, bool> resourceExpansion = [];
    private Guid? selectedResourceNode;
    private static bool SelectResourceNode(ResourceTreeItem item, Guid node)
    {
        if (item.Node.Id == node) { item.IsSelected = true; return true; }
        var path = item.Children.FirstOrDefault(c => c.Node.Find(node) != null);
        if (path == null) return false;
        item.IsExpanded = true; return SelectResourceNode(path, node);
    }
    private static ResourceMembers ResourceSession(DocumentModel doc) => doc.ResourceMembers ?? throw new StudioCommandException("unsupported", "Open an intact ZAR archive or ZRD resource.");
    private async Task<ZrdNode> ResourceTreeAsync(DocumentModel doc, Guid member, CancellationToken token)
    {
        var resources = ResourceSession(doc); var item = resources.Member(member); long revision = doc.Revision;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, doc.Lifetime.Token, shutdownToken, PreviewOperation.Current);
        var root = await Task.Run(() => resources.Tree(item, cancellation.Token), cancellation.Token);
        CheckResourceContext(doc, revision); return root;
    }
    private void CheckResourceContext(DocumentModel doc, long revision)
    {
        if (doc.IsDisposed || !ViewModel.Documents.Contains(doc)) throw new StudioCommandException("stale_document", "The document was closed.");
        if (doc.Revision != revision) throw new StudioCommandException("revision_conflict", "The document changed. Read its current revision and retry.");
    }
    private static readonly StudioParameter MemberParameter = P("member", "string", "Stable member UUID from archive_members or assets.", true);
    private void RegisterResourceCommands(StudioCommands r)
    {
        Register(r, "archive_members", "List the members of a compiled archive or ZRD in stored order with stable identities and source indices. Duplicate names are allowed. Standalone ZRD has one member.", false,
            [DocumentParameter, .. PageParameters], a => { var d = TargetDocument(a); return Result(new { d.Revision, members = Page(ResourceSession(d).Members.Select((m, i) => (Member: m, Index: i)), a, x => x.Member.Name,
                x => new { member = x.Member.Id, index = x.Index, x.Member.SourceIndex, x.Member.Name, bytes = x.Member.Data.Length }).Data }); });
        RegisterJob(r, "resource_select", "Select a member and optional ZRD node by stable UUID in the visible Data tree. Properties retains its pinned target.",
            [DocumentParameter, MemberParameter, P("node", "string", "Optional ZRD node UUID.")], false, async (a, token) =>
            {
                RequireNoDrafts(); var d = TargetDocument(a); Guid member = GuidArg(a,"member"), node = GuidArg(a,"node");
                _ = ResourceSession(d).Member(member);
                if (node != Guid.Empty && (await ResourceTreeAsync(d, member, token)).Find(node) == null) throw new InvalidDataException("Node no longer exists.");
                // Input may have arrived while the tree was read; changing the shown document would decide it.
                RequireNoDrafts();
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
        RegisterJob(r, "zrd_nodes", "Read a typed ZRD root, or page the immediate children of a node. Values use invariant text; strings are JSON-quoted, floats include raw bits. Formatting is bounded to a 4096-character prefix with valueTruncated; truncated text may be incomplete JSON. Node IDs stay stable while the document is open.",
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
    }
}
