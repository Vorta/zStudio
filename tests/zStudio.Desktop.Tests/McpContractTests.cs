using System.Text.Json.Nodes;
using Recoil.Zbd.Automation;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

public sealed class McpContractTests
{
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
