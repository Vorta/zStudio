using System.Windows;
using System.Windows.Controls;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Desktop;

/// <summary>
/// Chooses the export Update from Blender export applies: every checkout with exports in its outbox and, of each, the
/// newest exports source_blender_checkouts lists (<see cref="ExportsPerCheckout"/>), newest first. The newest export of
/// all starts chosen. Update maps to source_blender_update with the chosen checkout id and export path.
/// </summary>
internal sealed class BlenderUpdateDialog : Window
{
    internal const int PageSize = 64;
    /// <summary>The exports of one checkout source_blender_checkouts lists.</summary>
    internal const int ExportsPerCheckout = 16;
    private readonly (BlenderCheckout Checkout, BlenderExport Export)[] entries;
    private readonly int checkouts;
    private readonly StackPanel list = new();
    private readonly TextBlock count = new() { Margin = new(0, 6, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly Button previous = new() { Content = "Previous", MinWidth = 80 }, next = new() { Content = "Next", MinWidth = 80, Margin = new(6, 0, 0, 0) };
    private int page, chosen;
    public (BlenderCheckout Checkout, BlenderExport Export) Chosen => entries[chosen];

    /// <param name="listed">The project's checkouts with their exports, newest first (<see cref="SourceBlender.CheckoutExports(string, CancellationToken)"/>); at least one export.</param>
    public BlenderUpdateDialog(IReadOnlyList<(BlenderCheckout Checkout, IReadOnlyList<BlenderExport> Exports)> listed, Size? available = null)
    {
        entries = [.. listed.SelectMany(c => c.Exports.Take(ExportsPerCheckout).Select(e => (c.Checkout, e)))];
        if (entries.Length == 0) throw new ArgumentException("No Blender export to choose.", nameof(listed));
        checkouts = listed.Count(c => c.Exports.Count > 0);
        for (int i = 1; i < entries.Length; i++) if (entries[i].Export.WrittenUtc > entries[chosen].Export.WrittenUtc) chosen = i;
        page = chosen / PageSize;
        Title = "Update from Blender export"; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; ShowInTaskbar = false;
        StackPanel panel = new();
        panel.Children.Add(new TextBlock { Text = "Export to apply", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock
        {
            Text = "The checkout's model, its buffer and changed textures are replaced in the project's unsaved edits; Save writes them. Each checkout lists its newest 16 exports.",
            TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new(0, 6, 0, 6)
        });
        panel.Children.Add(count); panel.Children.Add(list);
        StackPanel pages = new() { Orientation = Orientation.Horizontal, Margin = new(0, 6, 0, 0) };
        pages.Children.Add(previous); pages.Children.Add(next); panel.Children.Add(pages);
        previous.Click += (_, _) => { page--; Fill(); };
        next.Click += (_, _) => { page++; Fill(); };
        WrapPanel buttons = new() { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Button ok = new() { Content = "Update", IsDefault = true, MinWidth = 88, Margin = new(0, 0, 8, 0) };
        ok.Click += (_, _) => DialogResult = true;
        Button cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 88 };
        buttons.Children.Add(ok); buttons.Children.Add(cancel);
        Content = DialogLayout.WithActions(panel, buttons);
        DialogLayout.Constrain(this, new(560, 560), available);
        Fill();
    }

    /// <summary>Only this page gets controls; the choice survives paging.</summary>
    private void Fill()
    {
        page = Math.Clamp(page, 0, (entries.Length - 1) / PageSize);
        list.Children.Clear();
        for (int i = page * PageSize; i < Math.Min((page + 1) * PageSize, entries.Length); i++)
        {
            var (checkout, export) = entries[i]; int index = i;
            StackPanel text = new();
            text.Children.Add(new TextBlock { Text = JsonData.ShownText(export.Relative, 160) + $" · {export.WrittenUtc.ToLocalTime():g}", TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock { Text = JsonData.ShownText(checkout.Model, 160) + $" · checkout {JsonData.ShownText(checkout.Id, 64)}", TextTrimming = TextTrimming.CharacterEllipsis, Opacity = 0.75 });
            RadioButton choice = new() { Content = text, GroupName = "export", IsChecked = index == chosen, Margin = new(0, 3, 0, 3) };
            System.Windows.Automation.AutomationProperties.SetName(choice, $"{JsonData.ShownText(checkout.Id, 64)}: {JsonData.ShownText(export.Relative, 256)}");
            choice.Checked += (_, _) => chosen = index;
            list.Children.Add(choice);
        }
        previous.IsEnabled = page > 0; next.IsEnabled = (page + 1) * PageSize < entries.Length;
        count.Text = $"Showing {page * PageSize + 1}–{Math.Min((page + 1) * PageSize, entries.Length)} of {entries.Length:N0} exports of {checkouts:N0} checkouts.";
    }
}
