using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Mcp;

/// <summary>Generated discovery metadata; execution always belongs to the visible workspace.</summary>
public static class McpCommandCatalog
{
    public sealed record Definition(string Name, string Description, bool Mutates, StudioParameter[] Parameters);

    public static string Serialize(StudioCommands commands) => JsonSerializer.Serialize(
        commands.All.Select(c => new Definition(c.Name, c.Description, c.Mutates, c.Parameters.ToArray())),
        new JsonSerializerOptions { WriteIndented = true });

    public static StudioCommands Create(Func<string, JsonObject, CancellationToken, Task<StudioResult>> execute)
    {
        using var stream = typeof(McpCommandCatalog).Assembly.GetManifestResourceStream("Recoil.Zbd.Mcp.CommandCatalog.json")
            ?? throw new InvalidOperationException("MCP discovery catalog is missing.");
        var definitions = JsonSerializer.Deserialize<Definition[]>(stream) ?? throw new InvalidDataException("Invalid MCP catalog.");
        StudioCommands commands = new();
        foreach (var d in definitions)
            commands.Add(new(d.Name, d.Description, d.Mutates, d.Parameters, (a, t) => execute(d.Name, a, t)));
        return commands;
    }
}
