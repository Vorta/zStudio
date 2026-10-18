using System.Globalization;
using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;
using zStudio.Rendering;

namespace zStudio.Desktop;

public partial class MainWindow
{
    /// <summary>
    /// The polygon zone tool: the source world it paints (null while it is off), the zone and mode, what the map's zones are,
    /// and what its last stroke did. Strokes apply one at a time; the view waits while one applies.
    /// </summary>
    private sealed class ZoneToolState
    {
        internal SourceWorldSession? Session { get; set; }
        internal byte Zone { get; set; }
        internal bool ZoneChosen { get; set; }
        internal bool Erase { get; set; }
        internal WorldHighlightMode RestoreHighlight { get; set; }
        internal long HighlightGeneration { get; set; }
        internal (SourceSceneState Build, string Master)? Master { get; set; }
        internal (SourceSceneState Build, Dictionary<string, int[]> Sites)? Sites { get; set; }
        internal (object Snapshot, IReadOnlyList<ZonePickerEntry> Zones)? Used { get; set; }
        internal (string Key, IReadOnlyDictionary<byte, string> Labels)? Labels { get; set; }
        internal ZonePaintStroke? LastStroke { get; set; }
        internal bool SkippedShown { get; set; }
        internal (int Changed, int Full)? Stroke { get; set; }
        internal bool Applying { get; set; }
        internal (string Text, bool Success)? Flash { get; set; }
    }
    private readonly ZoneToolState zoneTool = new();
    private ZonePicker? zonePicker;
    private readonly System.Windows.Threading.DispatcherTimer zoneFlashTimer = new() { Interval = TimeSpan.FromSeconds(2.6) };
    private bool synchronizingZoneTool;
    private string? polygonZonesTip;
    private bool ZoneToolEngaged => zoneTool.Session != null;

    private static string ZoneName(byte zone) => zone == 255 ? "Any" : "Zone " + zone.ToString(CultureInfo.InvariantCulture);
    private static void RequireCurrentZoneSources(DocumentModel document, CancellationToken token = default, bool verifyContent = true)
    {
        if (document.SourceInputsChanged(token, verifyContent))
            throw new StudioCommandException("stale_document", "Sources this world was built from changed; reload the world before inspecting or editing exact zone targets.");
    }

    // ---------------------------------------------------------------- the shown world's identities

    /// <summary>The map's master model for <paramref name="build"/>: its folder is the map's own (only its models take polygon zones).</summary>
    private string ZoneMaster(SourceWorldSession session, SourceSceneState build)
    {
        if (zoneTool.Master is { } known && ReferenceEquals(known.Build, build)) return known.Master;
        string master;
        try { master = SourceZoneEdits.MasterPath(session.Workspace, session.Mission); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        { throw new StudioCommandException("invalid_argument", "The map's master model could not be read: " + ex.Message); }
        zoneTool.Master = (build, master);
        return master;
    }
    private string ZoneFolder(SourceWorldSession session, SourceSceneState build) => SourceZoneEdits.FolderOf(ZoneMaster(session, build));
    private int[] ZoneSiteNodes(SourceSceneState build, int node)
    {
        if (zoneTool.Sites is not { } known || !ReferenceEquals(known.Build, build))
        {
            Dictionary<string, List<int>> sites = [];
            foreach (var (slot, origin) in build.Provenance)
                if (SourceZoneEdits.PolygonSite(origin) is { } site)
                {
                    if (!sites.TryGetValue(site, out var nodes)) sites[site] = nodes = [];
                    nodes.Add(slot);
                }
            zoneTool.Sites = known = (build, sites.ToDictionary(p => p.Key, p => p.Value.ToArray()));
        }
        return build.Provenance.TryGetValue(node, out var at) && SourceZoneEdits.PolygonSite(at) is { } key && known.Sites.TryGetValue(key, out var found) ? found : [];
    }
    /// <summary>The tool's view of the world <paramref name="session"/> shows now (each call reads its current build).</summary>
    private PolygonZoneBrush ZoneBrush(SourceWorldSession session) => new()
    {
        Reach = node =>
        {
            if (session.Owner?.SourceBuild is not { } build || !build.Provenance.TryGetValue(node, out var origin)) return PolygonZoneReach.PassThrough;
            if (origin.Terrain != null) return PolygonZoneReach.Blocked;
            try { return SourceZoneEdits.PolygonZoneRefusal(build, ZoneFolder(session, build), node) == null ? PolygonZoneReach.Paintable : PolygonZoneReach.PassThrough; }
            catch (StudioCommandException) { return PolygonZoneReach.PassThrough; }
        },
        SourcePolygons = node => session.Owner?.SourceBuild is { } build && build.Provenance.TryGetValue(node, out var origin) ? origin.ZonePolygons : null,
        Site = node => session.Owner?.SourceBuild is { } build && build.Provenance.TryGetValue(node, out var origin) ? SourceZoneEdits.PolygonSite(origin) : null,
        SiteNodes = node => session.Owner?.SourceBuild is { } build ? ZoneSiteNodes(build, node) : [],
    };

    /// <summary>The zones the map's polygons carry, as placed in the world: each placement counts its source polygons.</summary>
    private IReadOnlyList<ZonePickerEntry> UsedZones(SourceWorldSession session)
    {
        if (session.Owner is not { Document: SourceSceneSnapshot snapshot, SourceBuild: { } build }) return [];
        if (zoneTool.Used is { } known && ReferenceEquals(known.Snapshot, snapshot)) return known.Zones;
        long[] counts = new long[256];
        var slots = zStudio.Core.Worlds.GameZWriter.NodeSlots(snapshot.World);
        foreach (var (node, slot) in slots)
        {
            if (node.Model is not { } model) continue;
            var mapping = build.Provenance.TryGetValue(slot, out var origin) ? origin.ZonePolygons : null;
            HashSet<int> seen = [];
            for (int i = 0; i < model.Polygons.Count; i++)
            {
                if (mapping != null && (i >= mapping.Count || !seen.Add(mapping[i]))) continue;
                foreach (byte zone in SourceZoneEdits.PolygonZoneIds(model.Polygons[i].Zone) ?? []) counts[zone]++;
            }
        }
        var labels = ZoneLabels(session);
        List<ZonePickerEntry> zones = [];
        for (int zone = 0; zone < 256; zone++)
            if (counts[zone] > 0) zones.Add(new((byte)zone, labels.GetValueOrDefault((byte)zone), counts[zone]));
        zoneTool.Used = (snapshot, zones);
        return zones;
    }
    private IReadOnlyDictionary<byte, string> ZoneLabels(SourceWorldSession session)
    {
        // Labels live in the master model: they are read again only when its content changed (not after every stroke elsewhere).
        string key;
        try
        {
            key = session.Owner?.SourceBuild is { } build && ZoneMaster(session, build) is { } master && build.ReadHashes.TryGetValue(master, out var hash)
                ? master + "|" + hash : "revision|" + session.Workspace.ContentRevision.ToString(CultureInfo.InvariantCulture);
        }
        catch (StudioCommandException) { key = "revision|" + session.Workspace.ContentRevision.ToString(CultureInfo.InvariantCulture); }
        if (zoneTool.Labels is { } known && known.Key == key) return known.Labels;
        Dictionary<byte, string> labels = [];
        try { foreach (var label in SourceZoneEdits.Labels(session.Workspace, session.Mission)) if (label.Id != 255) labels[label.Id] = label.Label; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { Notice("The map's zone labels could not be read: " + Bounded(ex.Message, 256)); }
        zoneTool.Labels = (key, labels);
        return labels;
    }

    // ---------------------------------------------------------------- engaging and leaving

    /// <summary>Engages the tool on the shown source map (or changes its mode and zone).</summary>
    private void EngageZoneTool(DocumentModel document, bool? erase = null, byte? zone = null)
    {
        var session = SourceWorldOf(document);
        if (document != shownDocument || scene is not { } viewport || shownAsset?.Kind != AssetKind.World || !HasPublishedStaticScene || document.SourceBuild is not { } build)
            throw new StudioCommandException("not_ready", "Show this source map's Whole world before painting polygon zones.");
        bool engaged = zoneTool.Session == session && viewport.ZonePaintActive;
        if (!engaged)
        {
            if (viewport.IsPickupDragging || viewport.IsTerrainStroking || viewport.IsAiPathPointerTransform || viewport.IsParachutePointerMoving)
                throw new StudioCommandException("busy", "Finish the current drag first.");
            if (viewport.AiPathEditingNetwork != null || viewport.HasParachutePreview)
                throw new StudioCommandException("busy", "Finish the AI path or VTOL route editing before painting polygon zones.");
            ZoneFolder(session, build);
            if (flyCamera?.IsActive == true) flyCamera.End();
            if (terrainBrush != null) SetTerrainBrush(null);
        }
        if (erase is { } mode) zoneTool.Erase = mode;
        if (zone is { } chosen) { zoneTool.Zone = chosen; zoneTool.ZoneChosen = true; }
        else if (!zoneTool.ZoneChosen)
        {
            // The first time: the lowest zone the map uses, or 0.
            zoneTool.Zone = UsedZones(session).Select(z => z.Zone).DefaultIfEmpty((byte)0).Min(); zoneTool.ZoneChosen = true;
        }
        viewport.PaintZone = zoneTool.Zone; viewport.PaintErase = zoneTool.Erase;
        if (!engaged)
        {
            try { viewport.SetZoneBrush(ZoneBrush(session)); }
            catch (InvalidOperationException ex) { throw new StudioCommandException("busy", ex.Message); }
            zoneTool.Session = session;
            zoneTool.RestoreHighlight = worldHighlightMode;
            // Engaging shows the polygon zones; leaving restores the overlays unless they were changed meanwhile.
            if (worldHighlightMode != WorldHighlightMode.PolygonZones) SetWorldHighlightMode(WorldHighlightMode.PolygonZones);
            zoneTool.HighlightGeneration = worldHighlightGeneration;
            ClearZoneNotices();
            ViewModel.Status = "Polygon zones: drag over polygons to " + (zoneTool.Erase ? "erase " : "paint ") + ZoneName(zoneTool.Zone) + ".";
        }
        RefreshZoneToolChrome();
    }
    /// <summary>Another tool (AI path or VTOL route editing) takes the pointer: the polygon zone tool ends, unless a stroke is still applying.</summary>
    private void EndZoneToolFor(string tool)
    {
        if (!ZoneToolEngaged) return;
        if (scene?.IsZonePainting == true || zoneTool.Applying) throw new StudioCommandException("busy", $"Finish the polygon zone stroke before {tool}.");
        LeaveZoneTool();
    }
    private void LeaveZoneTool()
    {
        if (zoneTool.Session == null) return;
        zoneTool.Session = null;
        ZonePickerPopup.IsOpen = false;
        if (scene?.ZoneBrush != null) scene.SetZoneBrush(null);
        if (worldHighlightGeneration == zoneTool.HighlightGeneration && worldHighlightMode != zoneTool.RestoreHighlight) SetWorldHighlightMode(zoneTool.RestoreHighlight);
        ClearZoneNotices(); zoneTool.LastStroke = null; zoneTool.Stroke = null; zoneTool.Flash = null;
        CurrentInspectionCard?.SetSurfaceDetail(null);
        RefreshZoneToolChrome();
    }
    /// <summary>After the view was shown again (a rebuilt map): the tool follows its world, or ends with another view.</summary>
    private void RefreshZoneToolScene()
    {
        if (zoneTool.Session is not { } session) return;
        if (scene is not { } viewport || shownDocument?.SourceWorld != session || shownAsset?.Kind != AssetKind.World) { LeaveZoneTool(); return; }
        if (viewport.ZoneBrush == null)
        {
            viewport.PaintZone = zoneTool.Zone; viewport.PaintErase = zoneTool.Erase;
            try { viewport.SetZoneBrush(ZoneBrush(session)); }
            catch (InvalidOperationException) { LeaveZoneTool(); return; }
        }
        RefreshZoneToolChrome();
    }
    private void ConfigureZoneScene(SceneViewport viewport)
    {
        viewport.ZoneStrokeCompleted += stroke => ZoneStrokeFinished(viewport, stroke);
        viewport.ZoneStrokeProgress += (changed, full, stroking) =>
        {
            if (!ReferenceEquals(viewport, scene)) return;
            bool started = stroking && zoneTool.Stroke == null, ended = !stroking && zoneTool.Stroke != null;
            zoneTool.Stroke = stroking ? (changed, full) : null;
            if (started) { zoneTool.Flash = null; ClearZoneNotices(); }
            RefreshZoneBanner();
            // Undo and Redo wait while the button is held.
            if (started || ended) UpdateDocumentCommands();
        };
        viewport.ZoneHoverChanged += () => { if (ReferenceEquals(viewport, scene)) RefreshZoneReadout(); };
        viewport.ZonePickRequested += hover => { if (ReferenceEquals(viewport, scene)) PickZone(hover); };
        // Flycam and the other tools end it in the view; the command bar follows.
        viewport.ZonePaintActiveChanged += () => { if (ReferenceEquals(viewport, scene) && !viewport.ZonePaintActive && ZoneToolEngaged) LeaveZoneTool(); };
    }

    private void PolygonZoneToolClick(object sender, RoutedEventArgs e)
    {
        if (synchronizingZoneTool) return;
        if (ZoneToolEngaged) LeaveZoneTool();
        else if (ViewModel.SelectedDocument is { } document) RunZoneTool(() => EngageZoneTool(document));
        RefreshZoneToolChrome();
    }
    private void PaintZonesClick(object sender, RoutedEventArgs e) { if (ViewModel.SelectedDocument is { } document) RunZoneTool(() => EngageZoneTool(document, erase: false)); }
    private void EraseZonesClick(object sender, RoutedEventArgs e) { if (ViewModel.SelectedDocument is { } document) RunZoneTool(() => EngageZoneTool(document, erase: true)); }
    private void ZoneToolExitClick(object sender, RoutedEventArgs e) => LeaveZoneTool();
    private void ZoneModeChecked(object sender, RoutedEventArgs e)
    {
        if (synchronizingZoneTool || !ready || ViewModel.SelectedDocument is not { } document || !ZoneToolEngaged) return;
        RunZoneTool(() => EngageZoneTool(document, erase: sender == ZoneEraseMode));
    }
    private void RunZoneTool(Action action)
    {
        try { action(); }
        catch (StudioCommandException ex) { Notice(ex.Message); RefreshZoneToolChrome(); }
    }
    private void ChooseZone(byte zone)
    {
        ZonePickerPopup.IsOpen = false;
        if (ViewModel.SelectedDocument is { } document && ZoneToolEngaged) RunZoneTool(() => EngageZoneTool(document, zone: zone));
    }
    /// <summary>[ and ]: the previous or next zone the map uses, wrapping around.</summary>
    private void StepZone(int step)
    {
        if (zoneTool.Session is not { } session) return;
        var zones = UsedZones(session).Select(z => z.Zone).OrderBy(z => z == 255 ? 256 : z).ToList();
        if (zones.Count == 0) return;
        int at = zones.IndexOf(zoneTool.Zone);
        int next = at < 0 ? (step > 0 ? 0 : zones.Count - 1) : ((at + step) % zones.Count + zones.Count) % zones.Count;
        ChooseZone(zones[next]);
    }
    private void ZonePickerClick(object sender, RoutedEventArgs e)
    {
        if (zoneTool.Session is not { } session) return;
        if (zonePicker == null)
        {
            zonePicker = new(ChooseZone, RenameZoneAsync, () => ZonePickerPopup.IsOpen = false);
            ZonePickerPopup.Child = zonePicker;
            ZonePickerPopup.Closed += (_, _) => zonePicker.Commit();
        }
        zonePicker.Open(UsedZones(session), ZoneLabels(session), zoneTool.Zone);
        ZonePickerPopup.IsOpen = true;
    }
    private async Task RenameZoneAsync(byte zone, string label)
    {
        if (zoneTool.Session is not { Owner: { } document } session) return;
        if (label == (ZoneLabels(session).GetValueOrDefault(zone) ?? "")) return;
        try { await NameZoneAsync(document, zone, label, CancellationToken.None); }
        catch (Exception ex) when (ex is StudioCommandException or InvalidDataException) { Report(ex); }
        if (ZonePickerPopup.IsOpen && zoneTool.Session is { } current) zonePicker?.Open(UsedZones(current), ZoneLabels(current), zoneTool.Zone);
        RefreshZoneToolChrome();
    }
    /// <summary>Shift+click: a polygon's only zone is chosen; with several, a menu at the pointer offers them.</summary>
    private void PickZone(ZonePaintHover? hover)
    {
        if (hover == null) { ZoneFlash("Point at a polygon to pick its zone", false); return; }
        if (hover.Zones.Count == 0) { ZoneFlash("This polygon has no zones", false); return; }
        if (hover.Zones.Count == 1) { ChooseZone(hover.Zones[0]); ZoneFlash("Picked " + ZoneName(hover.Zones[0]), true); return; }
        var labels = zoneTool.Session is { } session ? ZoneLabels(session) : new Dictionary<byte, string>();
        ContextMenu menu = new() { Placement = PlacementMode.MousePoint };
        menu.Items.Add(new MenuItem { Header = "Pick a zone of this polygon", IsEnabled = false });
        foreach (byte zone in hover.Zones)
        {
            MenuItem item = new() { Header = ZoneName(zone) + (labels.TryGetValue(zone, out var label) ? " · " + label : ""), Icon = new Border { Width = 10, Height = 10, CornerRadius = new(2), Background = ZonePicker.Swatch(zone) } };
            item.Click += (_, _) => { ChooseZone(zone); ZoneFlash("Picked " + ZoneName(zone), true); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // ---------------------------------------------------------------- the chrome: command bar, banner, notices, readout

    private void RefreshZoneToolChrome()
    {
        synchronizingZoneTool = true;
        try
        {
            bool engaged = ZoneToolEngaged;
            PolygonZoneToolButton.IsChecked = engaged;
            PolygonZoneToolButton.ToolTip = engaged ? "Polygon zones — click or press Esc to stop painting" : "Polygon zones — paint or erase a zone on the polygons you drag over (P)";
            ZoneToolOptions.Visibility = engaged ? Visibility.Visible : Visibility.Collapsed;
            if (engaged)
            {
                SourcePaintGroup.BorderBrush = new SolidColorBrush(Color.FromArgb(115, 0xFF, 0x9A, 0x4D));
                SourcePaintGroup.Background = new SolidColorBrush(Color.FromArgb(18, 0xFF, 0x9A, 0x4D));
            }
            else { SourcePaintGroup.ClearValue(Border.BorderBrushProperty); SourcePaintGroup.ClearValue(Border.BackgroundProperty); }
            ZonePaintMode.IsChecked = !zoneTool.Erase; ZoneEraseMode.IsChecked = zoneTool.Erase;
            ZonePickerSwatch.Background = ZonePicker.Swatch(zoneTool.Zone);
            ZonePickerName.Text = ZoneName(zoneTool.Zone);
            string? label = zoneTool.Session is { } session && zoneTool.Zone != 255 ? ZoneLabels(session).GetValueOrDefault(zoneTool.Zone) : null;
            ZonePickerLabel.Text = label ?? ""; ZonePickerLabel.Visibility = label == null ? Visibility.Collapsed : Visibility.Visible;
            polygonZonesTip ??= HighlightPolygonZones.ToolTip as string ?? "Polygon zones";
            HighlightPolygonZones.ToolTip = engaged ? polygonZonesTip + " — while painting, other zones fade and the chosen zone stays" : polygonZonesTip;
        }
        finally { synchronizingZoneTool = false; }
        RefreshZoneBanner(); RefreshViewportChips(); UpdateDocumentCommands();
    }
    private static TextBlock BannerText(string text, string? brush = null, bool bold = false)
    {
        TextBlock block = new() { Text = text, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0), FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal };
        if (brush != null) block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return block;
    }
    private static Border Keycap(string key)
    {
        Border cap = new() { BorderThickness = new(1, 1, 1, 2), CornerRadius = new(4), MinWidth = 26, Height = 20, Padding = new(5, 0, 5, 0), Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = key, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
        cap.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorSecondaryBrush");
        return cap;
    }
    private void RefreshZoneBanner()
    {
        bool shown = ZoneToolEngaged && sourceChromeActive && scene != null && SceneHost.Visibility == Visibility.Visible && flyCamera?.IsActive != true;
        ZoneToolBanner.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ZoneToolNotices.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown) return;
        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool erase = zoneTool.Erase ^ (ctrl && zoneTool.Stroke == null);
        ZoneBannerIcon.Kind = erase ? "Eraser" : "Brush";
        var content = ZoneBannerContent.Children; content.Clear();
        content.Add(BannerText(zoneTool.Stroke != null ? (erase ? "Erasing" : "Painting") : erase ? "Erase" : "Paint", bold: true));
        content.Add(BannerText("·", "TextFillColorTertiaryBrush"));
        content.Add(new Border { Width = 10, Height = 10, CornerRadius = new(2), Background = ZonePicker.Swatch(zoneTool.Zone), Margin = new(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        content.Add(BannerText(ZoneName(zoneTool.Zone)));
        if (zoneTool.Session is { } session && zoneTool.Zone != 255 && ZoneLabels(session).GetValueOrDefault(zoneTool.Zone) is { } label)
            content.Add(new TextBlock { Text = label, MaxWidth = 140, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0), Foreground = (Brush)FindResource("TextFillColorTertiaryBrush") });
        bool narrow = PreviewHost.ActualWidth > 0 && PreviewHost.ActualWidth < 820;
        if (zoneTool.Stroke is { } stroke)
        {
            content.Add(BannerText("·", "TextFillColorTertiaryBrush"));
            content.Add(BannerText(stroke.Changed == 1 ? "1 polygon" : $"{stroke.Changed:N0} polygons", bold: true));
            if (stroke.Full > 0) content.Add(new TextBlock { Text = $"· {stroke.Full:N0} full", Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0xE1, 0x00)), VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 6, 0) });
            if (!narrow) { content.Add(BannerText("· Release to apply ·", "TextFillColorTertiaryBrush")); content.Add(Keycap("Esc")); content.Add(BannerText("cancels", "TextFillColorTertiaryBrush")); }
        }
        else if (zoneTool.Applying) content.Add(BannerText("· Applying…", "TextFillColorTertiaryBrush"));
        else if (zoneTool.Flash is { } flash)
            content.Add(new TextBlock { Text = (flash.Success ? "✓ " : "") + flash.Text, VerticalAlignment = VerticalAlignment.Center, Margin = new(4, 0, 0, 0),
                Foreground = flash.Success ? new SolidColorBrush(Color.FromRgb(0x7B, 0xD8, 0x8F)) : (Brush)FindResource("TextFillColorTertiaryBrush") });
        else if (!narrow)
        {
            if (ctrl) content.Add(BannerText($"· Ctrl held: {(erase ? "erase" : "paint")} for this stroke", "TextFillColorTertiaryBrush"));
            else
            {
                content.Add(BannerText("· Drag over polygons ·", "TextFillColorTertiaryBrush")); content.Add(Keycap("Ctrl"));
                content.Add(BannerText((zoneTool.Erase ? "paint" : "erase") + " ·", "TextFillColorTertiaryBrush")); content.Add(Keycap("Esc")); content.Add(BannerText("exit", "TextFillColorTertiaryBrush"));
            }
        }
        ZoneBannerExit.Visibility = zoneTool.Stroke == null ? Visibility.Visible : Visibility.Collapsed;
        ZoneToolBanner.Padding = new(12, 0, zoneTool.Stroke == null ? 4 : 12, 0);
    }
    private void ZoneFlash(string text, bool success)
    {
        zoneTool.Flash = (text, success);
        zoneFlashTimer.Stop(); zoneFlashTimer.Tick -= ZoneFlashEnded; zoneFlashTimer.Tick += ZoneFlashEnded; zoneFlashTimer.Start();
        RefreshZoneBanner();
    }
    private void ZoneFlashEnded(object? sender, EventArgs e) { zoneFlashTimer.Stop(); zoneTool.Flash = null; RefreshZoneBanner(); }

    private void ClearZoneNotices()
    {
        ZoneToolNotices.Children.Clear();
        if (zoneTool.SkippedShown) { zoneTool.SkippedShown = false; scene?.ShowZoneSkipped([]); }
    }
    /// <summary>After a stroke: the full polygons it skipped (with Show), editable terrain it crossed, and its limit.</summary>
    private void ShowZoneNotices(ZonePaintStroke stroke)
    {
        ClearZoneNotices();
        if (stroke.Full > 0)
        {
            Button show = new() { Content = "Show", Height = 26, Padding = new(10, 0, 10, 0), Margin = new(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            System.Windows.Automation.AutomationProperties.SetName(show, "Show the polygons that were left as they are");
            show.Click += (_, _) =>
            {
                zoneTool.SkippedShown = !zoneTool.SkippedShown;
                scene?.ShowZoneSkipped(zoneTool.SkippedShown ? stroke.FullTargets : []);
                show.Content = zoneTool.SkippedShown ? "Hide" : "Show";
            };
            AddZoneNotice("Warning", "#FCE100", stroke.Full == 1 ? "1 polygon already has three zones and was left as it is." : $"{stroke.Full:N0} polygons already have three zones and were left as they are.", show);
        }
        if (stroke.CrossedBlocked)
            AddZoneNotice("Info", null, "Editable terrain keeps its zones in its recipe and was not changed. Change them with its terrain regions (select a terrain piece; its Properties edit the recipe).", null);
        if (stroke.Limited)
            AddZoneNotice("Info", null, $"A stroke takes at most {SceneViewport.MaximumZonePaintTargets:N0} polygons; the rest of this one was left out. Paint them with another stroke.", null);
    }
    private void AddZoneNotice(string icon, string? color, string text, Button? action)
    {
        ViewportChip chip = new() { Padding = new(12, 8, 6, 8), Margin = new(0, 0, 0, 6), HorizontalAlignment = HorizontalAlignment.Center };
        DockPanel row = new();
        PreviewIcon glyph = new() { Kind = icon, Width = 15, Height = 15, Margin = new(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        if (color != null) glyph.SetValue(TextElement.ForegroundProperty, new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)));
        else glyph.SetResourceReference(TextElement.ForegroundProperty, "TextFillColorSecondaryBrush");
        DockPanel.SetDock(glyph, Dock.Left); row.Children.Add(glyph);
        Button dismiss = new() { Width = 26, Height = 26, Padding = new(0), Margin = new(4, 0, 0, 0), Background = Brushes.Transparent, BorderThickness = new(0), Content = new PreviewIcon { Kind = "Close", Width = 11, Height = 11 }, ToolTip = "Dismiss", VerticalAlignment = VerticalAlignment.Center };
        System.Windows.Automation.AutomationProperties.SetName(dismiss, "Dismiss");
        dismiss.Click += (_, _) =>
        {
            ZoneToolNotices.Children.Remove(chip);
            if (action != null && zoneTool.SkippedShown) { zoneTool.SkippedShown = false; scene?.ShowZoneSkipped([]); }
        };
        DockPanel.SetDock(dismiss, Dock.Right); row.Children.Add(dismiss);
        if (action != null) { DockPanel.SetDock(action, Dock.Right); row.Children.Add(action); }
        row.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12.5, LineHeight = 18, VerticalAlignment = VerticalAlignment.Center });
        chip.Content = row;
        ZoneToolNotices.Children.Add(chip);
    }
    /// <summary>The surface readout names the paintable polygon under the pointer: its file, zones and placements.</summary>
    private void RefreshZoneReadout()
    {
        if (CurrentInspectionCard is not { } card) return;
        if (!ZoneToolEngaged || scene?.ZoneHover is not { } hover || ViewModel.SelectedDocument?.SourceBuild is not { } build || !build.Provenance.TryGetValue(hover.Node, out var origin))
        { card.SetSurfaceDetail(null); return; }
        int placements = ZoneSiteNodes(build, hover.Node).Length;
        string zones = hover.Zones.Count == 0 ? "none" : string.Join(", ", hover.Zones.Select(z => z == 255 ? "Any" : z.ToString(CultureInfo.InvariantCulture)));
        card.SetSurfaceDetail($"{Path.GetFileName(origin.ModelFile)}{(placements > 1 ? $" · {placements} placements" : "")} · Zones {zones}");
    }

    // ---------------------------------------------------------------- keys

    /// <summary>
    /// Escape (cancel a stroke, then leave the tool), P and E (engage in a mode), [ and ] (step through used zones), and
    /// Ctrl and Shift (the cursor and banner follow). Nothing is taken from a text field.
    /// </summary>
    private bool ZoneToolKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift) { if (ZoneToolEngaged) { scene?.RefreshZoneTool(); RefreshZoneBanner(); } return false; }
        if (ZonePickerPopup.IsOpen) return false;
        bool typing = false;
        for (var source = e.OriginalSource as DependencyObject; source != null && !typing; source = source is Visual ? VisualTreeHelper.GetParent(source) : LogicalTreeHelper.GetParent(source))
            typing = source is System.Windows.Controls.Primitives.TextBoxBase or PasswordBox or ComboBox { IsEditable: true } or MenuItem;
        if (key == Key.Escape && ZoneToolEngaged)
        {
            if (scene?.CancelZoneGesture() == true) { ZoneFlash("Stroke cancelled", false); e.Handled = true; return true; }
            if (typing) return false;
            LeaveZoneTool(); e.Handled = true; return true;
        }
        if (typing || System.Windows.Input.Keyboard.Modifiers != ModifierKeys.None || !sourceChromeActive || e.OriginalSource is DependencyObject origin && Window.GetWindow(origin) is { } window && window != this) return false;
        if (key is Key.P or Key.E && ViewModel.SelectedDocument is { SourceWorld: not null } document && shownAsset?.Kind == AssetKind.World && scene?.IsZonePainting != true)
        { e.Handled = true; RunZoneTool(() => EngageZoneTool(document, erase: key == Key.E)); return true; }
        if (key is Key.OemOpenBrackets or Key.OemCloseBrackets && ZoneToolEngaged && scene?.IsZonePainting != true)
        { e.Handled = true; StepZone(key == Key.OemCloseBrackets ? 1 : -1); return true; }
        return false;
    }
    private void ZoneToolKeyUp(object sender, KeyEventArgs e)
    {
        if (ZoneToolEngaged && e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift) { scene?.RefreshZoneTool(); RefreshZoneBanner(); }
    }

    // ---------------------------------------------------------------- strokes

    /// <summary>A released stroke applies as one undoable change; the view waits until it is published.</summary>
    private async void ZoneStrokeFinished(SceneViewport viewport, ZonePaintStroke stroke)
    {
        if (!ReferenceEquals(viewport, scene) || zoneTool.Session is not { Owner: { SourceBuild: { } build } document })
        { viewport.ResolveZoneStroke(stroke.Id); return; }
        zoneTool.LastStroke = stroke; zoneTool.Stroke = null;
        ShowZoneNotices(stroke);
        if (stroke.Changed == 0) { viewport.ResolveZoneStroke(stroke.Id); ZoneFlash("Nothing changed", false); return; }
        viewport.ZonePaintWaiting = true; zoneTool.Applying = true; RefreshZoneBanner(); UpdateDocumentCommands();
        try
        {
            SourceZoneSelection[] selections;
            try { selections = [.. stroke.Targets.Select(t => SourceZoneEdits.FromCompiled(build, t.Node, t.Polygon))]; }
            catch (InvalidDataException ex) { throw new StudioCommandException("invalid_argument", ex.Message); }
            var (_, plan) = await ApplyPolygonZonesAsync(document, selections, stroke.Zone, stroke.Erase, CancellationToken.None);
            string polygons = plan.Changed == 1 ? "1 polygon" : $"{plan.Changed:N0} polygons";
            ZoneFlash(plan.Changed == 0 ? "Nothing changed" : stroke.Erase ? $"Erased {ZoneName(stroke.Zone)} from {polygons}" : $"Painted {ZoneName(stroke.Zone)} on {polygons}", plan.Changed > 0);
        }
        catch (Exception ex) when (ex is StudioCommandException or InvalidDataException or OperationCanceledException)
        { if (ex is not OperationCanceledException) Report(ex); }
        finally
        {
            zoneTool.Applying = false; viewport.ZonePaintWaiting = false; viewport.ResolveZoneStroke(stroke.Id);
            UpdateDocumentCommands(); RefreshZoneToolChrome(); RefreshZoneReadout();
        }
    }
    /// <summary>
    /// Paints or erases <paramref name="zone"/> on exact source polygons as one undoable change (GUI strokes and MCP alike).
    /// A plan that changes nothing accepts no edit and returns the same document.
    /// </summary>
    private async Task<(DocumentModel Document, SourcePolygonZoneStroke Plan)> ApplyPolygonZonesAsync(DocumentModel document, IReadOnlyList<SourceZoneSelection> selections, byte zone, bool erase, CancellationToken token)
    {
        var build = document.SourceBuild ?? throw new StudioCommandException("unsupported", "This document is not a source world.");
        if (selections.Count is 0 or > SourceZoneEdits.MaximumSelections) throw new StudioCommandException("invalid_argument", "Name between one and 4,096 polygons.");
        SourcePolygonZoneStroke? planned = null;
        try
        {
            var next = await PrepareSourceWorldEditAsync(document, erase ? "Erasing polygon zones" : "Painting polygon zones", (workspace, ct) =>
            {
                planned = SourceZoneEdits.PlanPolygonZones(workspace, build, selections, zone, erase, ct);
                if (planned.Changes.Count == 0) throw new StudioCommandException("unchanged", "Nothing changed.");
                return workspace.Apply(planned.Label, planned.Changes.Select(c => (c.Relative, (byte[]?)c.Content)), ct);
            }, token, quiet: true);
            return (next, planned!);
        }
        catch (StudioCommandException ex) when (ex.Code == "unchanged" && planned != null)
        {
            ViewModel.Status = "Nothing changed: " + (erase ? "no polygon had " : "every polygon already had ") + ZoneName(zone).ToLowerInvariant() + (planned.Full.Count > 0 ? " or was full." : ".");
            return (document, planned);
        }
    }
    private async Task<DocumentModel> NameZoneAsync(DocumentModel document, byte id, string label, CancellationToken token)
    {
        RequireNoDrafts(document, committing: true);
        string mission = SourceWorldOf(document).Mission;
        return await PrepareSourceWorldEditAsync(document, "Naming map zone", (workspace, ct) =>
        {
            var plan = SourceZoneEdits.PlanLabel(workspace, mission, id, label, ct);
            return workspace.Apply(plan.Label, plan.Changes.Select(change => (change.Relative, (byte[]?)change.Content)), ct);
        }, token, quiet: true);
    }

    // ---------------------------------------------------------------- MCP

    private JsonObject ZoneToolJson(DocumentModel? document)
    {
        var session = document?.SourceWorld;
        var stroke = zoneTool.LastStroke;
        JsonArray zones = [];
        if (session != null)
        {
            var labels = ZoneLabels(session);
            foreach (var entry in UsedZones(session)) zones.Add(new JsonObject { ["zone"] = entry.Zone, ["label"] = entry.Zone == 255 ? "Any" : labels.GetValueOrDefault(entry.Zone), ["polygons"] = entry.Polygons });
        }
        return new()
        {
            ["engaged"] = session != null && zoneTool.Session == session, ["mode"] = zoneTool.Erase ? "erase" : "paint", ["zone"] = zoneTool.Zone,
            ["label"] = session != null && zoneTool.Zone != 255 ? ZoneLabels(session).GetValueOrDefault(zoneTool.Zone) : zoneTool.Zone == 255 ? "Any" : null,
            ["stroking"] = scene?.IsZonePainting == true, ["applying"] = zoneTool.Applying, ["skippedShown"] = zoneTool.SkippedShown,
            ["lastStroke"] = stroke == null ? null : new JsonObject
            {
                ["zone"] = stroke.Zone, ["mode"] = stroke.Erase ? "erase" : "paint", ["changed"] = stroke.Changed, ["unchanged"] = stroke.Unchanged, ["full"] = stroke.Full,
                ["crossedTerrain"] = stroke.CrossedBlocked, ["limited"] = stroke.Limited,
                ["fullTargets"] = new JsonArray([.. stroke.FullTargets.Take(64).Select(t => (JsonNode)new JsonObject { ["node"] = t.Node, ["builtPolygon"] = t.Polygon })]),
            },
            ["zones"] = zones,
        };
    }
    private void RegisterSourceZoneCommands(StudioCommands registry)
    {
        Register(registry, "source_zones", "Inspect the map zone catalog. Zone assignments live in their owning glTF nodes, polygons or reference placements. Catalog and inspection report the master and owning source paths. Optional exact targets return a bounded assignment/scope page.", false,
            [DocumentParameter, ZoneTargetsParameter(), new("offset", "integer", "Target page offset.", Minimum: 0, Maximum: int.MaxValue)], async (args, token) =>
            {
                var document = TargetDocument(args); var session = SourceWorldOf(document); long revision = session.Workspace.ContentRevision;
                var targets = ParseZoneTargets(args); var build = document.SourceBuild!;
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, document.Lifetime.Token, ViewModel.WorkspaceToken, shutdownToken);
                var values = await Task.Run(() =>
                {
                    // Exact targets use immutable provenance from this build. Never combine its
                    // inherited IDs or source identities with newer live source assignments.
                    if (targets.Length != 0) RequireCurrentZoneSources(document, linked.Token);
                    var current = (SourceZoneEdits.Catalog(session.Workspace, session.Mission, linked.Token),
                        targets.Length == 0 ? null : SourceZoneEdits.Inspect(session.Workspace, build, targets, Int(args, "offset"), linked.Token));
                    if (targets.Length != 0) RequireCurrentZoneSources(document, linked.Token);
                    return current;
                }, linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                if (document.IsDisposed || session.Owner != document || revision != session.Workspace.ContentRevision) throw new StudioCommandException("context_changed", "The source world changed during zone inspection.");
                if (targets.Length != 0) RequireCurrentZoneSources(document, verifyContent: false);
                return Result(new { document = document.SessionId, revision = document.Revision, catalog = values.Item1, inspection = values.Item2 });
            });
        RegisterJob(registry, "source_polygon_zones", "Paint or erase one zone on exact source polygons of the map's own models as one undoable change (the polygon zone tool's stroke). Paint adds the zone after a polygon's others and skips (reports) a polygon that already has three; painting Any (255) replaces a polygon's zones, and painting a numbered zone replaces Any. Erase removes the zone, keeping the others' order; erasing the last leaves no zones, which differs from Any. A placement of a model takes its own zones; a file's own geometry changes wherever the file is loaded. Polygons of models outside the map's own folder (data/common, mission subfolders), placed units and pickups and editable terrain are refused. Nothing to change accepts no edit (changed 0).",
            [DocumentParameter, RevisionParameter, P("action", "string", "Paint or erase the zone.", true, "paint", "erase"),
             new("zone", "integer", "The zone: 0–254, or 255 for Any.", true, Minimum: 0, Maximum: 255), ZoneTargetsParameter(polygons: true)], true,
            async (args, token) =>
            {
                var document = TargetDocument(args);
                if (args["revision"]?.GetValue<long>() != document.Revision) throw new StudioCommandException("revision_conflict", "Read the current document revision.");
                var targets = ParseZoneTargets(args);
                if (targets.Any(t => t.Polygon == null)) throw new StudioCommandException("invalid_argument", "Each target names a source polygon.");
                if (scene?.IsZonePainting == true || zoneTool.Applying) throw new StudioCommandException("busy", "A polygon zone stroke is in progress in the view.");
                var (next, plan) = await ApplyPolygonZonesAsync(document, targets, checked((byte)Int(args, "zone")), Text(args, "action") == "erase", token);
                return Result(new
                {
                    document = DocumentState(next), plan.Label, plan.Changed, plan.Unchanged, full = plan.Full.Count,
                    fullTargets = plan.Full.Take(64).Select(t => new { node = t.SceneNode, polygon = t.Polygon }).ToArray(),
                });
            });
        Register(registry, "source_zone_tool", "Read or set the polygon zone tool of the shown source map (GUI state; it synthesizes no pointer input): engage it in a mode with a zone (the Paint group's toggle, Paint | Erase and zone picker, Tools > Paint/Erase polygon zones, P/E, [ ], Shift+click), exit it, or show/hide the polygons the last stroke skipped. State lists the zones the map's polygons carry as placed in the world (with labels and polygon counts) and what the last GUI stroke did.", true,
            [DocumentParameter, P("action", "string", "state reads; engage engages (or changes mode/zone); exit leaves; showSkipped/hideSkipped outline the last stroke's full polygons.", true, "state", "engage", "exit", "showSkipped", "hideSkipped"),
             P("mode", "string", "engage: paint or erase (default: as before).", false, "paint", "erase"), new("zone", "integer", "engage: the zone, 0–254 or 255 for Any (default: as before).", Minimum: 0, Maximum: 255)],
            args =>
            {
                var document = TargetDocument(args);
                switch (Text(args, "action"))
                {
                    case "engage":
                        if (scene?.IsZonePainting == true) throw new StudioCommandException("busy", "A stroke is in progress in the view.");
                        EngageZoneTool(document, args["mode"] == null ? null : Text(args, "mode") == "erase", args["zone"] == null ? null : checked((byte)Int(args, "zone")));
                        break;
                    case "exit":
                        if (scene?.IsZonePainting == true) throw new StudioCommandException("busy", "A stroke is in progress in the view.");
                        LeaveZoneTool(); break;
                    case "showSkipped" or "hideSkipped":
                        if (!ZoneToolEngaged || zoneTool.LastStroke is not { } stroke) throw new StudioCommandException("not_ready", "No stroke of the engaged tool skipped polygons.");
                        zoneTool.SkippedShown = Text(args, "action") == "showSkipped";
                        scene?.ShowZoneSkipped(zoneTool.SkippedShown ? stroke.FullTargets : []);
                        break;
                }
                return Result(ZoneToolJson(document));
            });
        RegisterJob(registry, "source_zone_label", "Name a map zone (the zone picker's rename): the label is stored in the map's master model and shown with the zone; an empty label removes it (the zone's assignments stay). One undoable change.",
            [DocumentParameter, RevisionParameter, new("zoneId", "integer", "The zone, 0–254 (Any is always Any).", true, Minimum: 0, Maximum: 254),
             new("label", "string", "The label, at most 256 characters; empty removes it.", true)], true,
            async (args, token) =>
            {
                var document = TargetDocument(args);
                if (args["revision"]?.GetValue<long>() != document.Revision) throw new StudioCommandException("revision_conflict", "Read the current document revision.");
                var next = await NameZoneAsync(document, checked((byte)Int(args, "zoneId")), Text(args, "label").Trim(), token);
                return Result(new { document = DocumentState(next) });
            });
    }
    private static StudioParameter ZoneTargetsParameter(bool polygons = false) => new("targets", "array", polygons ? "Exact source scene nodes and their source polygon indices (as inspection returns them)." : "Exact source scene nodes and, for faces, source polygon indices (as inspection returns them).",
        polygons, Items: new("", "object", "Source zone target.", Properties: [new("node", "integer", "Scene node index.", true, Minimum: 0, Maximum: int.MaxValue),
            new("polygon", "integer", "Polygon index in the node's source mesh, the Polygon an inspection target returns; not the built model's polygon index, which differs where the build split or discarded polygons. An index no built face comes from is refused." + (polygons ? "" : " Omit for object."), polygons, Minimum: 0, Maximum: int.MaxValue)]), MaxItems: SourceZoneEdits.MaximumSelections);
    private static SourceZoneSelection[] ParseZoneTargets(JsonObject args) => args["targets"] is JsonArray targets ?
        targets.Select(value => value is JsonObject target ? new SourceZoneSelection(Int(target, "node"), target["polygon"] == null ? null : Int(target, "polygon")) :
            throw new StudioCommandException("invalid_argument", "Each zone target must be an object.")).ToArray() : [];
}
