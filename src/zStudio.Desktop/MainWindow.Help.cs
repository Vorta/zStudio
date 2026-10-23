using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using zStudio.Automation;

namespace zStudio.Desktop;

/// <summary>
/// Help: the user guides and release notes are online, pinned to the installed version's tag; About shows the licence and
/// third-party notices that also ship beside zStudio.exe. MCP help returns the same links without opening anything.
/// </summary>
public partial class MainWindow
{
    private const string RepositoryUrl = "https://github.com/Vorta/zStudio";
    /// <summary>The guides of the Help menu, in its order; each guide item's Tag is its path.</summary>
    internal static readonly (string Title, string Path)[] OnlineGuides =
    [
        ("Overview", "README.md"), ("Source projects", "docs/source-project.md"), ("Map editor", "docs/map-editor.md"),
        ("3D navigation", "docs/3d-navigation.md"), ("Blender extension", "blender/zstudio_recoil/README.md"), ("MCP", "docs/mcp.md"),
        ("Compiled files", "docs/compiled-files.md"),
    ];
    /// <summary>The installed version without build metadata (its release tag is v plus this).</summary>
    internal static string StudioVersion => typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    private static string GuideUrl(string path) => $"{RepositoryUrl}/blob/v{StudioVersion}/{path}";
    private static string ReleaseNotesUrl => $"{RepositoryUrl}/releases/tag/v{StudioVersion}";
    /// <summary>Tests observe the link a Help action opens instead of starting a browser.</summary>
    internal Action<string>? OpenLinkOverride { get; set; }

    private void OnlineGuideClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path } && OnlineGuides.Any(g => g.Path == path)) OpenLink(GuideUrl(path));
    }
    private void ReleaseNotesClick(object sender, RoutedEventArgs e) => OpenLink(ReleaseNotesUrl);
    private void OpenLink(string url)
    {
        if (OpenLinkOverride is { } open) { open(url); return; }
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        { MessageBox.Show(this, $"Windows could not open {url}:\n{ex.Message}", "zStudio", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    private static string EmbeddedText(string name)
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(name) ?? throw new InvalidOperationException("Missing embedded " + name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().ReplaceLineEndings();
    }
    private static string? BesideExecutable(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? path : null;
    }

    private void AboutClick(object sender, RoutedEventArgs e)
    {
        Grid grid = new() { Margin = new(18) };
        foreach (var height in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto }) grid.RowDefinitions.Add(new() { Height = height });
        StackPanel header = new() { Margin = new(0, 0, 0, 12) };
        header.Children.Add(new TextBlock { Text = $"zStudio {StudioVersion}", FontSize = 20, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "Map editor and source-project tools for Zipper Interactive's RECOIL. Game assets are not included.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) });
        header.Children.Add(new TextBlock { Text = "Built with .NET, WPF, Vortice.Windows, CommunityToolkit.Mvvm, NAudio and the MCP C# SDK. zStudio's licence and the third-party notices follow; LICENSE and THIRD-PARTY-NOTICES.md beside zStudio.exe hold the same text.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 4, 0, 0) });
        grid.Children.Add(header);
        TextBox text = new()
        {
            IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, AcceptsReturn = true,
            Text = EmbeddedText("zStudio.LICENSE") + Environment.NewLine + EmbeddedText("zStudio.THIRD-PARTY-NOTICES.md"),
        };
        System.Windows.Automation.AutomationProperties.SetName(text, "Licence and third-party notices");
        Grid.SetRow(text, 1); grid.Children.Add(text);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 12, 0, 0) };
        Button notes = new() { Content = "Release notes", Margin = new(0, 0, 8, 0) };
        notes.Click += (_, _) => OpenLink(ReleaseNotesUrl);
        Button close = new() { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 80 };
        buttons.Children.Add(notes); buttons.Children.Add(close);
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        Window about = new() { Owner = this, Title = "About zStudio", Width = 760, Height = 640, MinWidth = 420, MinHeight = 320, Content = grid,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false };
        close.Click += (_, _) => about.Close();
        about.ShowDialog();
    }

    private void RegisterHelpCommands(StudioCommands r)
    {
        Register(r, "help", "Read what the Help menu offers, without opening anything: the installed version, its online user guides and release notes (GitHub, pinned to the version's tag v<version>) and the licence and third-party notice files beside zStudio.exe that About shows (null when absent, as in a build output).", false,
            [], _ => Result(new
            {
                version = StudioVersion,
                guides = OnlineGuides.Select(g => new { title = g.Title, path = g.Path, url = GuideUrl(g.Path) }).ToArray(),
                releaseNotes = ReleaseNotesUrl,
                license = BesideExecutable("LICENSE"), notices = BesideExecutable("THIRD-PARTY-NOTICES.md"),
            }));
    }
}
