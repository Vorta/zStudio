using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Recoil.Zbd.Automation;

public sealed class StudioCommandException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

public sealed record StudioResult(JsonNode Data, byte[]? Image = null);
public sealed record StudioParameter(string Name, string Type, string Description, bool Required = false, string[]? Choices = null,
    StudioParameter[]? Properties = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudioParameter? Items = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MinItems = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? MaxItems = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StudioParameter? AdditionalProperties = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Minimum = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? Maximum = null);
public sealed record StudioCommand(string Name, string Description, bool Mutates, IReadOnlyList<StudioParameter> Parameters,
    Func<JsonObject, CancellationToken, Task<StudioResult>> Execute)
{
    public JsonObject InputSchema => new()
    {
        ["type"] = "object", ["additionalProperties"] = false,
        ["properties"] = new JsonObject(Parameters.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Name, p.ToSchema()))),
        ["required"] = new JsonArray(Parameters.Where(p => p.Required).Select(p => (JsonNode?)JsonValue.Create(p.Name)).ToArray())
    };
    public void Validate(JsonObject args) => ValidateObject(args, Parameters, "");

    private static void ValidateObject(JsonObject args, IReadOnlyList<StudioParameter> parameters, string path, StudioParameter? additionalProperties = null)
    {
        foreach (var (name, value) in args)
        {
            var p = parameters.FirstOrDefault(p => p.Name == name) ?? additionalProperties ?? throw new StudioCommandException("invalid_argument", "Unknown argument: " + path + name);
            ValidateValue(value, p, path + name);
        }
        foreach (var p in parameters.Where(p => p.Required))
            if (!args.ContainsKey(p.Name)) throw new StudioCommandException("invalid_argument", "Required argument: " + path + p.Name);
    }

    private static void ValidateValue(JsonNode? value, StudioParameter p, string path)
    {
        var scalar = JsonSerializer.SerializeToElement(value);
        bool valid = p.Type switch
        {
            "string" => value is JsonValue s && s.TryGetValue<string>(out _),
            "boolean" => value is JsonValue b && b.TryGetValue<bool>(out _),
            "integer" => scalar.ValueKind == JsonValueKind.Number && scalar.TryGetInt64(out _),
            "number" => scalar.ValueKind == JsonValueKind.Number && scalar.TryGetDouble(out var d) && double.IsFinite(d),
            "object" => value is JsonObject, "array" => value is JsonArray, _ => false
        };
        if (!valid || p.Choices != null && !p.Choices.Contains(value!.GetValue<string>()))
            throw new StudioCommandException("invalid_argument", "Invalid " + p.Type + " argument: " + path);
        if (p.Type == "integer" && (scalar.GetInt64() < p.Minimum || scalar.GetInt64() > p.Maximum))
            throw new StudioCommandException("invalid_argument", "Integer argument is out of range: " + path);
        if (value is JsonObject nested && (p.Properties != null || p.AdditionalProperties != null))
            ValidateObject(nested, p.Properties ?? [], path + ".", p.AdditionalProperties);
        if (value is JsonArray array)
        {
            if (array.Count < p.MinItems || array.Count > p.MaxItems)
                throw new StudioCommandException("invalid_argument", "Invalid array length: " + path);
            if (p.Items != null)
                for (int i = 0; i < array.Count; i++) ValidateValue(array[i], p.Items, $"{path}[{i}]");
        }
    }
}

internal static class StudioSchema
{
    internal static JsonObject ToSchema(this StudioParameter parameter)
    {
        var schema = new JsonObject { ["type"] = parameter.Type, ["description"] = parameter.Description }
            .WithChoices(parameter.Choices).WithProperties(parameter.Properties);
        if (parameter.Items != null) schema["items"] = parameter.Items.ToSchema();
        if (parameter.MinItems != null) schema["minItems"] = parameter.MinItems.Value;
        if (parameter.MaxItems != null) schema["maxItems"] = parameter.MaxItems.Value;
        if (parameter.AdditionalProperties != null) schema["additionalProperties"] = parameter.AdditionalProperties.ToSchema();
        if (parameter.Minimum != null) schema["minimum"] = parameter.Minimum.Value;
        if (parameter.Maximum != null) schema["maximum"] = parameter.Maximum.Value;
        return schema;
    }
    internal static JsonObject WithProperties(this JsonObject schema, StudioParameter[]? properties)
    {
        if (properties == null) return schema;
        var nested = new StudioCommand("", "", false, properties, (_, _) => throw new NotSupportedException()).InputSchema;
        foreach (var (name, value) in nested) schema[name] = value?.DeepClone();
        return schema;
    }
    internal static JsonObject WithChoices(this JsonObject schema, string[]? choices)
    {
        if (choices != null) schema["enum"] = new JsonArray(choices.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
        return schema;
    }
}

public sealed class StudioCommands
{
    private readonly Dictionary<string, StudioCommand> commands = new(StringComparer.Ordinal);
    public IReadOnlyCollection<StudioCommand> All => commands.Values;
    public void Add(StudioCommand command) => commands.Add(command.Name, command);
    public async Task<StudioResult> ExecuteAsync(string name, JsonObject args, CancellationToken token = default)
    {
        if (!commands.TryGetValue(name, out var command)) throw new StudioCommandException("unknown_command", "Unknown capability: " + name);
        string json = args.ToJsonString();
        if (json.Length > 1024 * 1024) throw new StudioCommandException("request_too_large", "Arguments are limited to 1 MiB. Use smaller batches.");
        args = JsonNode.Parse(json)!.AsObject();
        command.Validate(args); token.ThrowIfCancellationRequested();
        return await command.Execute(args, token);
    }
    public JsonNode Describe() => new JsonObject { ["tools"] = JsonSerializer.SerializeToNode(All.Select(c => new { c.Name, c.Description, c.Mutates, schema = c.InputSchema })) };
}
