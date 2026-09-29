using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;
using Recoil.Zbd.Rendering;

namespace Recoil.Zbd.Desktop;

public partial class MainWindow
{
    private bool aiVisible, aiThroughGeometry = true, synchronizingAi;
    private string? aiNetworkFilter, aiSnapshot, aiPropertiesArchive;
    private long aiOptionsGeneration;
    private FileStamp? aiPropertiesStamp;
    private sealed record AiNetworkChoice(string? Id, string Label) { public override string ToString() => Label; }
    private bool IsAiWorld => animation == null && shownAsset?.Kind == AssetKind.World && scene != null && SceneHost.Visibility == Visibility.Visible;
    private void ConfigureAiScene(SceneViewport viewport)
    {
        AiEnabled.Tag = "Show authored AI node markers and directed connections.\n" + AiNetworkColors.Legend;
        AttachInspection(viewport);
        viewport.AiNodeSelected += id =>
        {
            if (scene != viewport || id == null) return;
            selectedNode = null; inspectedSceneSource = null;
            if (viewport.AiNetworks.Find(id) is not { } target) return;
            SetProperties(DescribeAiNode(viewport.AiNetworks, target.Network, target.Node));
            ViewModel.Status = $"AI {target.Network.Member} · node_{target.Node.Index:00} · {target.Node.Position}";
        };
        viewport.AiPropertiesRequested += async () =>
        {
            if (scene != viewport || viewport.SelectedAiNode is not { } id) return;
            try { await OpenAiPropertiesAsync(id, false); }
            catch (StudioCommandException ex) { ViewModel.Status = ex.Message; }
        };
        viewport.AiLabelChanged += text =>
        {
            if (scene != viewport) return;
            AiLabel.Visibility = Visibility.Collapsed;
        };
    }
    private void AiOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (!ready || synchronizingAi) return;
        if (!ResolveInspectionDrafts()) { ApplyAiOptions(); return; }
        SetAiOptions(AiEnabled.IsChecked == true, AiThroughGeometry.IsChecked == true, (AiNetworkCombo.SelectedItem as AiNetworkChoice)?.Id);
    }
    private void SetAiOptions(bool visible, bool throughGeometry, string? filter)
    {
        ++aiOptionsGeneration; aiVisible = visible; aiThroughGeometry = throughGeometry; aiNetworkFilter = filter;
        ApplyAiOptions();
    }
    private void ApplyAiOptions()
    {
        bool world = IsAiWorld; AiTools.Visibility = world ? Visibility.Visible : Visibility.Collapsed;
        if (!world) { AiLabel.Visibility = Visibility.Collapsed; return; }
        var graph = scene!.AiNetworks;
        AiValves.Visibility = graph.ValveSources.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AiValveOverlay.Visibility = AiValves.Visibility;
        if (aiSnapshot != graph.Id)
        {
            aiSnapshot = graph.Id; aiNetworkFilter = null;
            if (propertiesWindow is { } pinnedWindow && pinnedWindow.Document == shownDocument && pinnedWindow.CurrentJson?["ai_snapshot"]?.GetValue<string>() is { } pinned && pinned != graph.Id)
                pinnedWindow.MarkAiSnapshotStale();
            synchronizingAi = true;
            try { AiNetworkCombo.ItemsSource = new[] { new AiNetworkChoice(null, "All networks") }.Concat(graph.Networks.Select(n =>
                new AiNetworkChoice(n.Id, $"{n.Member} · {ShortAiText(n.Name)} · {n.Nodes.Count} nodes · {AiSourceLabel(n.Archive)} #{n.MemberIndex}"))).ToArray(); }
            finally { synchronizingAi = false; }
        }
        synchronizingAi = true;
        try
        {
            AiEnabled.IsChecked = aiVisible; AiThroughGeometry.IsChecked = aiThroughGeometry;
            AiValveOverlay.IsChecked = scene.ValveOverlayVisible;
            AiNetworkCombo.SelectedItem = AiNetworkCombo.Items.Cast<AiNetworkChoice>().FirstOrDefault(n => n.Id == aiNetworkFilter);
            AiNetworkCombo.ToolTip = ((AiNetworkChoice?)AiNetworkCombo.SelectedItem)?.Label + "\nFilter authored AI networks by source record.";
            AiNetworkCombo.Visibility = AiThroughGeometry.Visibility = aiVisible ? Visibility.Visible : Visibility.Collapsed;
            AiNetworkCombo.IsEnabled = graph.Networks.Count > 0;
        }
        finally { synchronizingAi = false; }
        scene.SetAiOptions(aiVisible, aiThroughGeometry, aiNetworkFilter);
        if (aiVisible && !graph.Networks.Any(n => n.Nodes.Count > 0))
        { AiLabelText.Text = "No supported AI nodes in this mission."; AiLabel.Visibility = Visibility.Visible; }
    }
    private static string ShortAiText(string text) => text.Length <= 70 ? text : text[..70] + "…";
    private string AiSourceLabel(string path) => string.IsNullOrEmpty(ViewModel.RootPath) ? Path.GetFullPath(path) : Path.GetRelativePath(ViewModel.RootPath, path);
    private object? AiPreviewState() => !IsAiWorld ? null : new
    {
        snapshot = scene!.AiNetworks.Id, visible = scene.AiVisible, throughGeometry = scene.AiThroughGeometry,
        network = scene.AiNetworkFilter ?? "all", selectedNode = scene.SelectedAiNode,
        valveOverlay = scene.ValveOverlayVisible, valveFilter = scene.ValveFilter,
        networks = scene.AiNetworks.Networks.Count, nodes = scene.AiNetworks.Networks.Sum(n => n.Nodes.Count),
        links = scene.AiNetworks.Networks.Sum(n => n.Nodes.Sum(p => p.PreviewLinks.Count(l => l.Target != null))),
        linkSlots = scene.AiNetworks.Networks.Sum(n => n.Nodes.Sum(p => (long)p.LinkCount)), linksTruncated = scene.AiNetworks.Networks.Any(n => n.Nodes.Any(p => p.LinksTruncated))
    };
    private async Task<PropertiesWindow?> OpenAiPropertiesAsync(string id, bool automation)
    {
        if (!IsAiWorld || shownDocument is not { } doc || scene!.AiNetworks.Find(id) is not { } target)
            throw new StudioCommandException("stale_record", "AI node is unavailable. Read the current AI graph.");
        var snapshot = scene.AiNetworks;
        if (automation) RequireNoDrafts();
        else if (!await ResolvePropertiesDraftsAsync()) return null;
        if (!IsAiWorld || shownDocument != doc || scene!.AiNetworks.Id != snapshot.Id || doc.IsDisposed)
            throw new StudioCommandException("context_changed", "AI preview changed while opening Properties.");
        ++propertyRequest;
        var data = DescribeAiNode(snapshot, target.Network, target.Node); data["ai_snapshot"] = snapshot.Id;
        data["snapshot_status"] = "Pinned preview snapshot. Reopen after editing or reloading the source resource.";
        var window = GetPropertiesWindow();
        bool accepted = window.SetReadOnly(doc, $"{target.Network.Member} #{target.Network.MemberIndex} · node_{target.Node.Index:00} · {AiSourceLabel(target.Network.Archive)}", data);
        if (accepted) { aiPropertiesArchive = target.Network.Archive; aiPropertiesStamp = ReadAiStamp(target.Network.Archive); }
        PresentProperties(window, accepted); return accepted ? window : null;
    }
    private void CheckAiPropertiesSnapshot()
    {
        if (propertiesWindow?.CurrentJson?["ai_snapshot"] != null && aiPropertiesArchive != null && aiPropertiesStamp != ReadAiStamp(aiPropertiesArchive)) propertiesWindow.MarkAiSnapshotStale();
    }
    private static JsonObject DescribeAiStrategy(AiNetwork network)
    {
        var result = network.AttackStrategy.Describe();
        result["color"] = AiNetworkColors.Hex(network.AttackStrategy);
        return result;
    }
    private static JsonObject DescribeAiNode(AiNetworkSnapshot graph, AiNetwork network, AiNode node)
    {
        var result = graph.Describe(network, node); result["attack_strategy"] = DescribeAiStrategy(network); return result;
    }
    private static string AiStrategyText(AiAttackStrategy strategy) => strategy.State switch
    {
        AiAttackStrategyState.Missing => "Not stored",
        AiAttackStrategyState.Invalid => "Unavailable (invalid data)",
        _ => strategy.Value == "" ? "\"\" (empty)" : strategy.BoundedValue(2048)!
    };
    private static FileStamp? ReadAiStamp(string path)
    {
        try { return FileStamp.Read(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
