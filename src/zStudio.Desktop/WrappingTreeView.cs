using System.Windows;
using System.Windows.Controls;

namespace Recoil.Zbd.Desktop;

/// <summary>Retains the native tree template while letting headers wrap within the viewport.</summary>
public sealed class WrappingTreeView : TreeView
{
    protected override DependencyObject GetContainerForItemOverride() => new WrappingItem();

    private sealed class WrappingItem : TreeViewItem
    {
        protected override DependencyObject GetContainerForItemOverride() => new WrappingItem();

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();
            // Fluent puts PART_Header in an Auto column beside an unused star column.
            // Include that remaining space so WPF measures wrapped text with a finite
            // width. Reapply on theme/template changes, keeping native states/automation.
            if (GetTemplateChild("PART_Header") is FrameworkElement header && header.Parent is Grid grid)
            {
                int column = Grid.GetColumn(header);
                if (column < grid.ColumnDefinitions.Count && grid.ColumnDefinitions[column].Width.IsAuto
                    && grid.ColumnDefinitions.Skip(column + 1).Any(c => c.Width.IsStar))
                    Grid.SetColumnSpan(header, grid.ColumnDefinitions.Count - column);
            }
        }
    }
}
