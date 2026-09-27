using System.Text.Json.Nodes;
using System.Windows.Controls;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Desktop;

/// <summary>Properties stay attached to a member/node identity across reordering and undo.</summary>
public sealed class ResourcePropertiesEditor : FieldEditor, IDisposable
{
    private const int MaximumFieldCharacters = 16384;
    private readonly DocumentModel document;
    private readonly Guid memberId;
    private readonly Guid? nodeId;
    private readonly Func<string, string, Task> edit;
    private string? form;
    public event Action? Changed;
    public Guid MemberId => memberId;
    public Guid? NodeId => nodeId;
    private ResourceMember? Member => document.ResourceEdits!.Current.Members.SingleOrDefault(m => m.Id == memberId);
    private ZrdNode? Node
    {
        get
        {
            try { return nodeId is Guid id && Member is { } member ? document.ResourceEdits!.Tree(member).Find(id) : null; }
            catch (System.IO.InvalidDataException) { return null; }
        }
    }
    public string TargetLabel => Member == null ? "Deleted member" : Member.Name + (nodeId == null ? "" : Node == null ? " · deleted node" : " · " + Node.Kind);
    public JsonObject Json
    {
        get
        {
            var node = Node; var preview = node?.PreviewValue(4096);
            return new() { ["member"] = memberId.ToString(), ["node"] = nodeId?.ToString(), ["name"] = Member?.Name, ["kind"] = node?.Kind.ToString(), ["value"] = preview?.Value, ["value_truncated"] = preview?.Truncated ?? false, ["bytes"] = Member?.Data.Length, ["save_destination"] = document.ResourceEdits!.TargetPath };
        }
    }
    public ResourcePropertiesEditor(DocumentModel document, Guid member, Guid? node, Func<string, string, Task> edit)
    {
        this.document = document; memberId = member; nodeId = node; this.edit = edit;
        document.ResourceEditsChanged += RefreshProperties; RefreshProperties();
    }
    protected override void RefreshProperties()
    {
        if (disposed || committingDraft) return;
        var member = Member; var node = Node;
        var preview = node?.PreviewValue(MaximumFieldCharacters);
        string next = member == null ? "missing-member" : nodeId != null && node == null ? "missing-node" : node?.Kind.ToString() ?? "member";
        if (preview?.Truncated == true) next += "-large";
        if (form != next && !HasPendingDrafts)
        {
            form = next; draftInputs.Clear(); valueRefresh.Clear(); ClearAutomationFields("properties");
            StackPanel panel = new() { Margin = new(12) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            if (member == null || nodeId != null && node == null) Label(panel, "This record was deleted. Undo restores its identity.");
            else
            {
                Label(panel, "Enter to apply · Escape to restore. Changes remain unsaved until Save.");
                if (nodeId == null)
                    Input(panel, "Name", member.Name, _ => { }, !document.ResourceEdits!.IsArchive, getter: () => Member?.Name ?? "", asyncCommit: value => edit("rename", value));
                else
                {
                    Input(panel, "Type", node!.Kind.ToString(), _ => { }, true);
                    if (node.Kind != ZrdKind.Array)
                    {
                        bool large = preview!.Value.Truncated;
                        Input(panel, "Value", preview.Value.Value, _ => { }, readOnly: large,
                            hint: large ? "This prefix is read-only because the value exceeds 16,384 displayed characters. Use Change type in the Data tree to replace the complete value, or export the resource for external editing." : node.Kind == ZrdKind.String ? "JSON-quoted Latin-1 string, including escaped NUL bytes." : node.Kind == ZrdKind.Float ? "Finite decimal or 0x followed by eight raw float-bit hex digits." : "Signed 32-bit integer.",
                            getter: () => Node?.PreviewValue(MaximumFieldCharacters).Value ?? "", asyncCommit: value => edit("set", value));
                    }
                    else Label(panel, "Use the Data tree context menu to add, move or remove ordered children.");
                }
                Input(panel, "Save to", document.ResourceEdits!.TargetPath, _ => { }, true, getter: () => document.ResourceEdits!.TargetPath);
            }
        }
        else foreach (var refresh in valueRefresh.ToArray()) refresh();
        Changed?.Invoke();
    }
    public void Dispose()
    { if (disposed) return; disposed = true; document.ResourceEditsChanged -= RefreshProperties; Content = null; draftInputs.Clear(); GC.SuppressFinalize(this); }
}

public sealed class ResourceTreeItem(ZrdNode node, int index, ResourceTreeItem? parent, IDictionary<Guid, bool> expansion) : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private bool selected;
    public bool IsSelected { get => selected; set { selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    public ZrdNode Node { get; } = node;
    public ResourceTreeItem? Parent { get; } = parent;
    public int Index { get; } = index;
    public string Label
    {
        get
        {
            var preview = Node.PreviewValue(200);
            return (Parent == null ? "Root" : "[" + Index + "]") + " · " + Node.Kind + " · " + preview.Value + (preview.Truncated ? "…" : "");
        }
    }
    public bool IsExpanded { get => expansion.TryGetValue(Node.Id, out bool value) ? value : Parent == null; set { expansion[Node.Id] = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); } }
    private IReadOnlyList<ResourceTreeItem>? children;
    public IReadOnlyList<ResourceTreeItem> Children => children ??= Node.Children.Select((c, i) => new ResourceTreeItem(c, i, this, expansion)).ToArray();
}
