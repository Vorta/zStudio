using System.IO;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using zStudio.Automation;
using Xunit;

namespace zStudio.Desktop.Tests;

internal static class McpParityChecks
{
    internal static void Run(StudioCommands commands)
    {
        var discovery = zStudio.Mcp.McpCommandCatalog.Create((_, _, _) => throw new NotSupportedException());
        Assert.Equal(zStudio.Mcp.McpCommandCatalog.Serialize(commands), zStudio.Mcp.McpCommandCatalog.Serialize(discovery));
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "zStudio.slnx"))) root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("Repository root");
        var capabilityMap = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "docs", "mcp-capabilities.json")))!;
        var inventory = capabilityMap["handlers"]!.AsArray();
        var interactions = capabilityMap["interactions"]!.AsArray();
        Assert.Equal(new[] { "MainWindow.Navigation:CameraNavigationClick", "MainWindow.Navigation:CameraKeyboard", "SceneViewport.Navigation:ConfigureNavigation", "SceneViewport.NavigationCube:NavigateCubeAt", "MainWindow.Resources:ArchiveMembers", "MainWindow.Resources:ZrdNodes", "MainWindow.Content:Scripts", "SceneViewport.Ai:HandleAiPointerDown", "SceneViewport.Inspection:Pointer", "SceneInspectionCard:Resize", "SceneInspectionCard:Actions", "DocumentSceneTree:Hierarchy", "MotionEditor:Controls", "SceneViewport:Valves", "SourceExportSelectionDialog:Controls", "MainWindow.Sources:SourceProfileMenu", "SourceObjectCardEditor:Fields", "SourceSectionCard:Fields", "SourceSectionCard:Inspector", "MainWindow.SourceAnimations:Controls", "MainWindow.AnimationPaths:PathsChip", "AnimationViewOverlay:RangeTests", "AnimationTimingView:DropWindows", "MainWindow.Parachutes:MoveAlongPathBanner", "AnimationTimingView:Controls", "AnimationThresholdEditor:ValueProposed", "AnimationPanel:Controls", "AnimationGraphView:Controls", "AnimationScenarioPanel:Controls", "MainWindow.AnimationMode:Chrome", "MainWindow.AnimationMode:AnimationModeKey", "MainWindow.AnimationScenario:PickInView", "TerrainPropertiesEditor:Fields", "SceneViewport.TerrainBrush:Strokes", "ZonePicker:Controls", "SceneViewport.Zones:Strokes", "MainWindow.SourceZones:Notices", "MainWindow.SourceZones:ZoneToolKey", "SourceModelDialog:Controls", "SourcePickupDialog:Controls", "BlenderUpdateDialog:Controls", "SourceExportDialog:Controls", "SourceInitializeDialog:Controls", "WorldCompareWindow:Controls", "TerrainCreateDialog:Controls", "MainWindow.AiPath:AiPathKeyboard", "SceneViewport.AiEditing:HandleAiPathDown", "MainWindow.Parachutes:ParachuteKeyboard", "SceneViewport.Parachutes:HandleParachuteDown", "MainWindow.Presentation:PresentationKey", "MainWindow.MapWorkspace:MapPicker",
            "PlayerTankPanel:Radios", "SceneInspectionCard:PlayerTankRow", "MainWindow.SourceEditor:StatusPlayerTank" },
            interactions.Select(row => row!["gui"]!.GetValue<string>()));
        var mapped = inventory.Select(x => x!["gui"]!.GetValue<string>()).Order().ToArray();
        string[] events = ["Click", "Checked", "Unchecked", "SelectionChanged", "ValueChanged", "TextChanged", "LostFocus", "LostKeyboardFocus", "KeyDown", "SelectedItemChanged", "MouseDoubleClick", "DragCompleted", "PreviewMouseUp"];
        var actual = Directory.EnumerateFiles(Path.Combine(root, "src", "zStudio.Desktop"), "*.xaml")
            .SelectMany(path => XDocument.Load(path).Descendants().Attributes().Where(a => events.Contains(a.Name.LocalName)).Select(a => Path.GetFileName(path) + ":" + a.Value)).Distinct().Order().ToArray();
        Assert.Equal(actual, mapped);
        var available = commands.All.Select(c => c.Name).ToHashSet();
        foreach (var row in inventory.Concat(interactions))
        {
            Assert.False(string.IsNullOrWhiteSpace(row!["note"]!.GetValue<string>()));
            var tools = row["tools"]!.AsArray();
            if (tools.Count == 0) Assert.Equal("MainWindow.xaml:McpIntegrationClick", row["gui"]!.GetValue<string>());
            foreach (var tool in tools) Assert.Contains(tool!.GetValue<string>(), available);
        }
    }
}
