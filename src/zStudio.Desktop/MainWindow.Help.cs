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

    /// <summary>Animation mode's keys (design §17), as Help → Controls and formats and MCP help topic=animation-mode list them.</summary>
    internal static readonly (string Keys, string Where, string Does)[] AnimationModeKeys =
    [
        ("Space", "Animation mode, outside text fields and focused buttons", "Play / Pause (over the Structure view a Space+drag pans)"),
        (", .", "Animation mode, outside text fields", "Step back / forward one 1/60 s tick (Shift: one second)"),
        ("← →", "Timing (lanes focused)", "Step one tick (Shift: one second); Ctrl+← → the previous / next mark on the selected lane"),
        ("Home End", "Timing; the 3D view", "Go to the playback range's start / end"),
        ("↑ ↓", "Structure; Timing", "Previous / next row (event rows, questions, headers) across nodes in column order; Timing: previous / next lane"),
        ("← →", "Structure", "Follow the selected row's edge out (→) or back to its source (←); on a header collapse / expand"),
        ("Enter / Ctrl+Enter", "Structure: a reference node", "Expand / Open (Back returns)"),
        ("G T F", "Structure: a condition's question row", "Game / True / False switch"),
        ("F2", "An event in Structure, Timing or the Log", "Focus the Inspector's Start threshold"),
        ("Alt+Enter", "Anywhere in Animation mode", "Focus the Inspector's first field"),
        ("Ctrl+0, Ctrl+=, Ctrl+−", "Structure", "Fit, zoom in, zoom out"),
        ("Ctrl+1 / Ctrl+2 / Ctrl+3", "The animation panel", "Structure / Timing / Split"),
        ("Alt+← / Alt+→", "Animation mode", "Back / Forward through opened definitions"),
        ("F6 / Shift+F6", "Animation mode", "Move the focus: 3D view → transport → panel → Inspector (Shift: the other way)"),
        ("Esc", "Animation mode", "Ends what is in progress (a gesture, Pick in view, a drop-window move, a flyout), then clears the selection; never exits"),
        ("Shift+Esc", "Animation mode", "Exit Animation mode (the scenario is kept)"),
    ];
    /// <summary>Animation mode's pointer actions, as Help and MCP help topic=animation-mode list them.</summary>
    internal static readonly (string Action, string Does)[] AnimationModePointer =
    [
        ("Click a node, row, edge or mark", "Selects it: the Inspector shows its card, the 3D view outlines the objects it names, Structure and Timing follow"),
        ("Double-click a reference node", "Expands it (presentation only); its ▷ Open switches to it"),
        ("Click a call row's port (● / ○)", "Includes or excludes that call path (and the paths below it)"),
        ("Click Game / True / False", "Sets the condition's switch for that call path; the run re-evaluates to the playhead"),
        ("Click a pip on an outcome line", "Seeks to that pass"),
        ("Wheel / Shift+wheel / Ctrl+wheel", "Structure: scroll / scroll sideways / zoom at the pointer; Timing: Shift pans, Ctrl zooms"),
        ("Middle drag, Space+drag or a drag on empty space", "Pans the Structure view"),
        ("Click or drag the ruler", "Seeks (the preview runs the scenario up to that time)"),
        ("Drag a start on the Starts lane", "Moves it (0.025 s grid; Shift 0.1 s; Ctrl free); right-click removes it"),
        ("Drag a threshold bracket's end (Thresholds on)", "Proposes the event's new start threshold in its own clock, through the source draft"),
        ("Drag the Inspector ruler's diamond", "Proposes the threshold (0.025 s grid; Shift 0.1 s; Ctrl free)"),
        ("Pick in view, then click a surface", "Places the scenario's custom player or beam origin (right-click or Esc cancels)"),
        ("Paths ▾ (the 3D view, after Perspective)", "Evaluate paths (silent: the playhead, camera and panel stay), Show guides, Show range tests, Contacts and impacts, Frame paths, Clear cached paths"),
        ("Hover or select a PLAYER_RANGE question or a range-gated activation", "The 3D view draws its ring around the attach node, the player pin and the dashed line with the distance and outcome (an edge arrow when the player is off screen)"),
        ("Click a drop window's start (cyan) or release (orange) marker in the view", "Selects the window: Inspector → Definition → Drop windows"),
        ("Move along path, then click the flight path", "Places the drop window's start there (Apply keeps it as one undo step; right-click or Esc cancels)"),
        ("Drag a bar on Timing's Drop windows lane", "Moves that window's start on the flight sequence's clock (0.025 s grid; Shift 0.1 s; Ctrl free; never its length); a click selects it"),
    ];

    private void RegisterHelpCommands(StudioCommands r)
    {
        Register(r, "help", "Read what the Help menu offers, without opening anything: the installed version, its online user guides and release notes (GitHub, pinned to the version's tag v<version>) and the licence and third-party notice files beside zStudio.exe that About shows (null when absent, as in a build output). topic animation-mode reads what Help → Controls and formats and Animation mode's legend (ⓘ) show for it: every key with where it works, the pointer actions, and the legend's edges, Timing marks, states and families.", false,
            [P("topic", "string", "animation-mode: Animation mode's keys, pointer actions and legend.", false, "animation-mode")], a =>
            {
                if (a.ContainsKey("topic"))
                {
                    if (Text(a, "topic") != "animation-mode") throw new StudioCommandException("invalid_argument", "The topic is animation-mode.");
                    return Result(new
                    {
                        topic = "animation-mode",
                        keys = AnimationModeKeys.Select(k => new { keys = k.Keys, where = k.Where, does = k.Does }).ToArray(),
                        pointer = AnimationModePointer.Select(p => new { action = p.Action, does = p.Does }).ToArray(),
                        legend = AnimationLegend.Entries,
                        firstEntryTip = "Animation mode keeps the map where it is. Exit returns to it; the scenario is kept. (Shown once per profile until dismissed.)",
                    });
                }
                return Result(new
                {
                    version = StudioVersion,
                    guides = OnlineGuides.Select(g => new { title = g.Title, path = g.Path, url = GuideUrl(g.Path) }).ToArray(),
                    releaseNotes = ReleaseNotesUrl,
                    license = BesideExecutable("LICENSE"), notices = BesideExecutable("THIRD-PARTY-NOTICES.md"),
                });
            });
    }
}
