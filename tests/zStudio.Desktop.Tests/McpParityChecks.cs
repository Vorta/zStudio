using System.IO;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Recoil.Zbd.Automation;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

internal static class McpParityChecks
{
    internal static void Run(StudioCommands commands)
    {
        var discovery = Recoil.Zbd.Mcp.McpCommandCatalog.Create((_, _, _) => throw new NotSupportedException());
        Assert.Equal(Recoil.Zbd.Mcp.McpCommandCatalog.Serialize(commands), Recoil.Zbd.Mcp.McpCommandCatalog.Serialize(discovery));
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "zStudio.slnx"))) root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("Repository root");
        var inventory = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "docs", "mcp-capabilities.json")))!["handlers"]!.AsArray();
        var mapped = inventory.Select(x => x!["gui"]!.GetValue<string>()).Order().ToArray();
        string[] events = ["Click", "Checked", "Unchecked", "SelectionChanged", "ValueChanged", "TextChanged", "LostFocus", "LostKeyboardFocus", "KeyDown", "SelectedItemChanged", "MouseDoubleClick", "DragCompleted"];
        var actual = Directory.EnumerateFiles(Path.Combine(root, "src", "zStudio.Desktop"), "*.xaml")
            .SelectMany(path => XDocument.Load(path).Descendants().Attributes().Where(a => events.Contains(a.Name.LocalName)).Select(a => Path.GetFileName(path) + ":" + a.Value)).Distinct().Order().ToArray();
        Assert.Equal(actual, mapped);
        var available = commands.All.Select(c => c.Name).ToHashSet();
        foreach (var row in inventory)
        {
            Assert.False(string.IsNullOrWhiteSpace(row!["note"]!.GetValue<string>()));
            var tools = row["tools"]!.AsArray();
            if (tools.Count == 0) Assert.Equal("MainWindow.xaml:McpIntegrationClick", row["gui"]!.GetValue<string>());
            foreach (var tool in tools) Assert.Contains(tool!.GetValue<string>(), available);
        }
    }
}
