using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Recoil.Zbd.Automation;

namespace Recoil.Zbd.Mcp;

public static class StudioMcpServer
{
    public static McpServer Create(Stream stream, StudioCommands commands, string version, Action<string>? activity = null)
        => Create(stream, stream, commands, version, activity);

    public static McpServer Create(Stream input, Stream output, StudioCommands commands, string version, Action<string>? activity = null)
    {
        return McpServer.Create(new StreamServerTransport(new BoundedMcpStream(input), output), new McpServerOptions
        {
            ServerInfo = new() { Name = "zStudio", Version = version },
            ServerInstructions = "Control the user's visible zStudio workspace. Read zstudio_state and zstudio_capabilities first. Use document/record identities, never names as identity. Read revisions before changes. Pending GUI drafts and unsaved changes require explicit resolution. Game-dependent preview approximations are reported in diagnostics.",
            Handlers = new()
            {
                ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                {
                    Tools = commands.All.Select(c => new Tool
                    {
                        Name = c.Name, Description = c.Description, InputSchema = JsonSerializer.SerializeToElement(c.InputSchema),
                        Annotations = new() { ReadOnlyHint = !c.Mutates, DestructiveHint = c.Mutates, OpenWorldHint = false }
                    }).ToList()
                }),
                CallToolHandler = async (request, token) =>
                {
                    string name = request.Params?.Name ?? "";
                    try
                    {
                        var args = JsonSerializer.SerializeToNode(request.Params?.Arguments) as JsonObject ?? new();
                        var result = await commands.ExecuteAsync(name, args, token);
                        string json = result.Data.ToJsonString();
                        if (json.Length > 4 * 1024 * 1024) throw new StudioCommandException("response_too_large", "Result exceeds 4 MiB. Use paged inspection, property_fields, source_bytes or JSON export.");
                        activity?.Invoke(name + " · completed");
                        List<ContentBlock> content = [new TextContentBlock { Text = json }];
                        if (result.Image != null) content.Add(ImageContentBlock.FromBytes(result.Image, "image/png"));
                        return new CallToolResult { IsError = false, Content = content, StructuredContent = JsonSerializer.SerializeToElement(result.Data) };
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException and not StackOverflowException)
                    {
                        string code = ex is StudioCommandException error ? error.Code : ex is OperationCanceledException ? "canceled" : "operation_failed";
                        var errorData = new { code, message = ex.Message };
                        activity?.Invoke(name + " · " + code);
                        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = JsonSerializer.Serialize(errorData) }], StructuredContent = JsonSerializer.SerializeToElement(errorData) };
                    }
                },
                ListResourcesHandler = (_, _) => ValueTask.FromResult(new ListResourcesResult { Resources =
                [new Resource { Uri = "zstudio://state", Name = "Visible workspace", MimeType = "application/json" },
                 new Resource { Uri = "zstudio://capabilities", Name = "Capabilities", MimeType = "application/json" }] }),
                ReadResourceHandler = async (request, token) =>
                {
                    string uri = request.Params?.Uri ?? "";
                    string command = uri switch { "zstudio://state" => "zstudio_state", "zstudio://capabilities" => "zstudio_capabilities", _ => throw new StudioCommandException("unknown_resource", uri) };
                    var result = await commands.ExecuteAsync(command, new(), token);
                    return new ReadResourceResult { Contents = [new TextResourceContents { Uri = uri, MimeType = "application/json", Text = result.Data.ToJsonString() }] };
                }
            }
        });
    }
}
