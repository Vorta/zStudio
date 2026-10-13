using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Sources;

/// <summary>Windowless, one-request file adapter. Does not access a live workspace or enable automation.</summary>
public static class BlenderBridge
{
    public static async Task<int> RunAsync(Stream input, Stream output, CancellationToken token = default)
    {
        JsonObject result; int exit = 0;
        try
        {
            using MemoryStream bytes = new(); byte[] buffer = new byte[4096]; int count;
            while ((count = await input.ReadAsync(buffer, token)) != 0)
            {
                if (count > 65536 - bytes.Length) throw new InvalidDataException("The Blender helper request exceeds 64 KiB.");
                bytes.Write(buffer, 0, count);
            }
            var request = JsonNode.Parse(bytes.ToArray()) as JsonObject ?? throw new InvalidDataException("A Blender helper request must be an object.");
            if (request["protocol"]?.GetValue<int>() != SourceBlender.GraphProtocol) throw new InvalidDataException("Incompatible Blender helper protocol; update the extension and zStudio together.");
            string Text(string name) => request[name]?.GetValue<string>() is { Length: > 0 and <= 32767 } text ? text : throw new InvalidDataException($"Missing or invalid {name}.");
            result = Text("operation") switch
            {
                "info" => new() { ["protocol"] = SourceBlender.GraphProtocol, ["sourceFormat"] = 4, ["blenderMin"] = "5.2.0", ["blenderMax"] = "5.3.0" },
                "import" => SourceBlender.PrepareBlenderImport(Text("root"), Text("model"), Text("destination"), token),
                "export" => SourceBlender.PrepareBlenderExport(Text("edits"), Text("destination"), token),
                _ => throw new InvalidDataException("Unknown Blender helper operation."),
            };
            result["ok"] = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException or OperationCanceledException or OverflowException or FormatException or KeyNotFoundException or NullReferenceException)
        {
            exit = ex is OperationCanceledException ? 2 : 1;
            result = new() { ["ok"] = false, ["error"] = ex.Message.Length > 2048 ? ex.Message[..2048] : ex.Message };
        }
        await JsonSerializer.SerializeAsync(output, result, cancellationToken: CancellationToken.None);
        await output.FlushAsync(CancellationToken.None); return exit;
    }
}
