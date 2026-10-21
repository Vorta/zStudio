using zStudio.Core.Formats;

namespace zStudio.Desktop;

/// <summary>A row of the Data tree: one decoded ZRD node, with its expansion remembered by node identity.</summary>
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
