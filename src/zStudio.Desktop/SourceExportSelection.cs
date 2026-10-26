using System.ComponentModel;
using System.Globalization;
using zStudio.Core.Sources;

namespace zStudio.Desktop;

/// <summary>One exact output plan and its preview-only checkbox choices.</summary>
internal sealed class SourceExportSelection
{
    private static readonly StringComparer Natural = CultureInfo.InvariantCulture.CompareInfo.GetStringComparer(CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);
    internal IReadOnlyList<SourceExportSelectionRow> Rows { get; }
    internal IReadOnlyList<SourceExportSelectionRow> Files { get; }
    internal string Profile { get; }
    internal Func<bool> IsCurrent { get; }
    internal event Action? Changed;
    internal int SelectedCount => Files.Count(f => f.IsChecked == true);
    internal string[] SelectedPaths => Files.Where(f => f.IsChecked == true).Select(f => f.Path).ToArray();

    internal SourceExportSelection(IReadOnlyList<SourceOutputPlan> plan, StudioSettings settings, string profile, Func<bool> current)
    {
        Profile = profile; IsCurrent = current;
        var uncheckedPaths = settings.ExportUncheckedOutputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<SourceExportSelectionRow> rows = [], files = [];
        foreach (var group in plan.GroupBy(p => Group(p.Path)).OrderBy(g => g.Key == "Shared files" ? 0 : Mission(g.Key) ? 1 : 2).ThenBy(g => g.Key, Natural))
        {
            var heading = new SourceExportSelectionRow(group.Key, true, this);
            rows.Add(heading);
            foreach (var output in group.OrderBy(p => p.Path, Natural))
            {
                var row = new SourceExportSelectionRow(output.Path, false, this, !uncheckedPaths.Contains(Normalize(output.Path)), heading);
                heading.Children.Add(row); rows.Add(row); files.Add(row);
            }
        }
        Rows = rows; Files = files;
    }
    internal static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();
    private static bool Mission(string name) => name.Length > 1 && (name[0] is 'm' or 'M') && name.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;
    private static string Group(string path) { int slash = path.IndexOf('/'); return slash < 0 ? "Shared files" : path[..slash]; }
    internal void SetAll(bool value) { foreach (var row in Files) row.Set(value); Refresh(); }
    internal void Set(SourceExportSelectionRow row, bool value)
    {
        if (row.IsGroup) foreach (var child in row.Children) child.Set(value); else row.Set(value);
        Refresh();
    }
    internal void Refresh() { foreach (var row in Rows.Where(r => r.IsGroup)) row.Notify(); Changed?.Invoke(); }
    internal void Remember(StudioSettings settings)
    {
        var uncheckedPaths = settings.ExportUncheckedOutputs.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Files)
            if (file.IsChecked == true) uncheckedPaths.Remove(Normalize(file.Path)); else uncheckedPaths.Add(Normalize(file.Path));
        if (uncheckedPaths.Count > StudioSettings.MaximumExportChoices) throw new InvalidOperationException("Too many remembered export choices. Select more files before closing this window.");
        var previous = settings.ExportUncheckedOutputs;
        settings.ExportUncheckedOutputs = uncheckedPaths.Order(StringComparer.Ordinal).ToList();
        try { settings.Save(); }
        catch { settings.ExportUncheckedOutputs = previous; throw; }
    }
}

internal sealed class SourceExportSelectionRow(string path, bool group, SourceExportSelection owner, bool selected = true, SourceExportSelectionRow? parent = null) : INotifyPropertyChanged
{
    private bool selected = selected;
    public string Path { get; } = path;
    public bool IsGroup { get; } = group;
    public string Group => parent?.Path ?? Path;
    internal List<SourceExportSelectionRow> Children { get; } = [];
    public bool? IsChecked
    {
        get => !IsGroup ? selected : Children.All(c => c.selected) ? true : Children.All(c => !c.selected) ? false : null;
        set => owner.Set(this, value != false);
    }
    internal void Set(bool value) { if (selected == value) return; selected = value; Notify(); }
    internal void Notify() => PropertyChanged?.Invoke(this, new(nameof(IsChecked)));
    public event PropertyChangedEventHandler? PropertyChanged;
}
