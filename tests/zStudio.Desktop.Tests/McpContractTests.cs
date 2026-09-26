using System.Text.Json.Nodes;
using Recoil.Zbd.Automation;
using Recoil.Zbd.Mcp;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class McpContractTests
{
    [Fact]
    public void PickupIdentitySchemaDescribesRequiredCaseSensitiveFieldsAndAcceptsReturnedShape()
    {
        var command = McpCommandCatalog.Create((_, _, _) => throw new Exception("Must not execute"))
            .All.Single(c => c.Name == "zstudio_pickup_move");
        var source = command.InputSchema["properties"]!["source"]!;
        Assert.False(source["additionalProperties"]!.GetValue<bool>());
        Assert.Equal(new[] { "ArchivePath", "AssetIndex", "ResourceName", "RecordIndex" },
            source["required"]!.AsArray().Select(p => p!.GetValue<string>()));
        var properties = source["properties"]!;
        Assert.Equal("string", properties["ArchivePath"]!["type"]!.GetValue<string>());
        Assert.Equal("string", properties["ResourceName"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", properties["AssetIndex"]!["type"]!.GetValue<string>());
        Assert.Equal("integer", properties["RecordIndex"]!["type"]!.GetValue<string>());
        command.Validate(new()
        {
            ["document"] = Guid.NewGuid().ToString(), ["revision"] = 0L,
            ["source"] = new JsonObject { ["ArchivePath"] = @"C:\working\zrdr.zbd", ["AssetIndex"] = 0, ["ResourceName"] = "PUPPIES", ["RecordIndex"] = 0 },
            ["x"] = 0, ["y"] = 1, ["z"] = -2
        });
    }

    [Fact]
    public async Task TypedMapsValidateEachValueBeforeExecutingAndDescribeOpenKeys()
    {
        int calls = 0;
        var command = new StudioCommand("test", "test", false,
            [new("paths", "object", "destination map", true, AdditionalProperties: new("", "string", "new path"))],
            (_, _) => { calls++; return Task.FromResult(new StudioResult(new JsonObject())); });
        var registry = new StudioCommands(); registry.Add(command);
        foreach (string value in new[] { "1", "null", "true", "{}", "[]" })
        {
            var error = await Assert.ThrowsAsync<StudioCommandException>(() => registry.ExecuteAsync("test",
                new() { ["paths"] = new JsonObject { ["source.zbd"] = JsonNode.Parse(value) } }, TestContext.Current.CancellationToken));
            Assert.Equal("invalid_argument", error.Code);
            Assert.Contains("paths.source.zbd", error.Message);
        }
        Assert.Equal(0, calls);
        await registry.ExecuteAsync("test", new() { ["paths"] = new JsonObject { ["source.zbd"] = "copy.zbd" } }, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal("string", command.InputSchema["properties"]!["paths"]!["additionalProperties"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void IntegerBoundsRejectOverflowBeforeExecutionButRetainLongOffsetsAndRevisions()
    {
        var commands = McpCommandCatalog.Create((_, _, _) => throw new Exception("Must not execute"));
        var files = commands.All.Single(c => c.Name == "zstudio_files");
        foreach (long value in new[] { (long)int.MinValue - 1, (long)int.MaxValue + 1 })
            Assert.Equal("invalid_argument", Assert.Throws<StudioCommandException>(() => files.Validate(new() { ["offset"] = value })).Code);
        Assert.Equal(int.MinValue, files.InputSchema["properties"]!["offset"]!["minimum"]!.GetValue<long>());
        Assert.Equal(int.MaxValue, files.InputSchema["properties"]!["offset"]!["maximum"]!.GetValue<long>());
        commands.All.Single(c => c.Name == "zstudio_source_bytes").Validate(new()
            { ["document"] = Guid.NewGuid().ToString(), ["offset"] = (long)int.MaxValue + 1, ["length"] = 1 });
        commands.All.Single(c => c.Name == "zstudio_undo_redo").Validate(new()
            { ["document"] = Guid.NewGuid().ToString(), ["revision"] = (long)int.MaxValue + 1, ["action"] = "undo" });
    }

    [Fact]
    public async Task ArraySchemasValidateItemsAndLengthsRecursivelyBeforeExecuting()
    {
        int calls = 0;
        var command = new StudioCommand("test", "test", false,
            [new("vectors", "array", "vectors", true, Items: new("", "object", "vector", Properties:
                [new("xyz", "array", "coordinates", true, Items: new("", "number", "component"), MinItems: 3, MaxItems: 3)]))],
            (_, _) => { calls++; return Task.FromResult(new StudioResult(new JsonObject())); });
        var registry = new StudioCommands(); registry.Add(command);
        foreach (string value in new[] { "[{}]", "[{\"xyz\":[0,\"bad\",0]}]", "[{\"xyz\":[0,null,0]}]", "[{\"xyz\":[0,0]}]", "[{\"xyz\":[0,0,0,0]}]", "[{\"xyz\":[0,0,0],\"extra\":0}]" })
        {
            var error = await Assert.ThrowsAsync<StudioCommandException>(() => registry.ExecuteAsync("test", new() { ["vectors"] = JsonNode.Parse(value) }, TestContext.Current.CancellationToken));
            Assert.Equal("invalid_argument", error.Code);
            Assert.Contains("vectors[0]", error.Message);
        }
        Assert.Equal(0, calls);
        await registry.ExecuteAsync("test", JsonNode.Parse("{\"vectors\":[{\"xyz\":[0,-2.5,1e-7]}]}")!.AsObject(), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        var item = command.InputSchema["properties"]!["vectors"]!["items"]!;
        Assert.False(item["additionalProperties"]!.GetValue<bool>());
        Assert.Equal("xyz", item["required"]![0]!.GetValue<string>());
        var vector = item["properties"]!["xyz"]!;
        Assert.Equal(3, vector["minItems"]!.GetValue<int>());
        Assert.Equal(3, vector["maxItems"]!.GetValue<int>());
        Assert.Equal("number", vector["items"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void UnknownMissingAndWronglyTypedArgumentsCannotReachHandlers()
    {
        var command=new StudioCommand("test","test",false,[new("value","integer","value",true)],(_,_)=>throw new Exception("Must not execute"));
        Assert.Throws<StudioCommandException>(()=>command.Validate(new()));
        Assert.Throws<StudioCommandException>(()=>command.Validate(new() { ["value"]="1" }));
        Assert.Throws<StudioCommandException>(()=>command.Validate(new() { ["value"]=1,["extra"]=0 }));
        Assert.Throws<StudioCommandException>(()=>command.Validate(new() { ["value"]=1.5 }));
        command.Validate(JsonNode.Parse("{\"value\":1}")!.AsObject());
        command.Validate(new() { ["value"] = 1 });
        Assert.False(command.InputSchema["properties"]!["value"]!.AsObject().ContainsKey("enum"));
    }
    [Fact]
    public void NestedOptionsRejectUnknownAndIncorrectTypesBeforeExecution()
    {
        var command = new StudioCommand("test", "test", true,
            [new("changes", "object", "options", true, Properties: [new("mute", "boolean", "mute"), new("height", "number", "offset")])], (_,_)=>throw new Exception());
        command.Validate(JsonNode.Parse("{\"changes\":{\"mute\":true,\"height\":-50}}")!.AsObject());
        Assert.Throws<StudioCommandException>(()=>command.Validate(JsonNode.Parse("{\"changes\":{\"mute\":\"true\"}}")!.AsObject()));
        Assert.Throws<StudioCommandException>(()=>command.Validate(JsonNode.Parse("{\"changes\":{\"unknown\":1}}")!.AsObject()));
        Assert.False(command.InputSchema["properties"]!["changes"]!["additionalProperties"]!.GetValue<bool>());
    }
    [Fact]
    public void EnumConstraintsAreMachineReadableAndEnforced()
    {
        var command=new StudioCommand("test","test",false,[new("action","string","action",true,["play","pause"])],(_,_)=>throw new Exception());
        command.Validate(new() { ["action"]="play" });
        Assert.Throws<StudioCommandException>(()=>command.Validate(new() { ["action"]="delete" }));
        Assert.Equal(2,command.InputSchema["properties"]!["action"]!["enum"]!.AsArray().Count);
    }
}
