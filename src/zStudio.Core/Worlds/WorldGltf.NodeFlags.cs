using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;

namespace Recoil.Zbd.Core.Worlds;

public static partial class WorldGltf
{
    internal const uint UnusedNodeFlags = 0x70000000;
    internal const uint DeclientMask = 0x00030000;
    private static readonly (string Name, uint Bit)[] BooleanNodeFlags =
    [
        ("altitudeSurface", 0x08), ("intersectSurface", 0x10), ("intersectBounds", 0x20),
        ("proximity", 0x40), ("landmark", 0x80), ("overwrite", 0x800000), ("altitudeZoneCheck", ZoneGate),
    ];
    internal static readonly string[] NodeFlagFields = [.. BooleanNodeFlags.Select(f => f.Name), "declient"];

    /// <summary>Source-authored bits only. Missing Boolean fields are false; a missing/null declient clears both modes.</summary>
    internal static uint StoredNodeFlags(JsonObject? values)
    {
        if (values == null) return 0;
        RejectPackedNodeFlags(values, "the source node");
        uint result = 0;
        foreach (var (name, bit) in BooleanNodeFlags)
            if (values.TryGetPropertyValue(name, out var value))
            {
                if (value is not JsonValue scalar || !scalar.TryGetValue<bool>(out bool enabled))
                    throw new InvalidDataException($"The source node attribute {name} must be a Boolean.");
                if (enabled) result |= bit;
            }
        if (values["declient"] is { } mode)
        {
            NumericOperand(mode, "declient", "the source node");
            if (mode is not JsonValue scalar || !scalar.TryGetValue<string>(out string? text))
                throw new InvalidDataException("The source node declient must be canModify, clipTo or null.");
            result |= text switch
            {
                "canModify" => 0x10000u,
                "clipTo" => 0x20000u,
                _ => throw new InvalidDataException("The source node declient must be canModify, clipTo or null; omit it to clear both modes."),
            };
        }
        return result;
    }

    /// <summary>Writes canonical true-only attributes, preserving unrelated node metadata.</summary>
    internal static void WriteNodeFlags(JsonObject values, uint flags)
    {
        ValidateDeclient(flags, "The source node");
        RejectPackedNodeFlags(values, "the source node");
        foreach (var (name, bit) in BooleanNodeFlags)
            if ((flags & bit) != 0) values[name] = true;
            else values.Remove(name);
        switch (flags & DeclientMask)
        {
            case 0x10000: values["declient"] = "canModify"; break;
            case 0x20000: values["declient"] = "clipTo"; break;
            default: values.Remove("declient"); break;
        }
    }

    internal static uint SetNodeFlag(uint flags, uint bit, bool enabled)
    {
        if (enabled && bit is 0x10000 or 0x20000) flags &= ~DeclientMask;
        return (enabled ? flags | bit : flags & ~bit) & ~UnusedNodeFlags;
    }

    internal static void ValidateDeclient(uint flags, string context)
    {
        if ((flags & DeclientMask) == DeclientMask)
            throw new InvalidDataException($"{context}: CanModify and ClipTo cannot both be enabled. Choose one declient mode in the model and its build scripts.");
    }

    private static void RejectPackedNodeFlags(JsonObject values, string path)
    {
        if (values.ContainsKey("flags") || values.ContainsKey("rootFlags") || values.ContainsKey("gate"))
            throw new InvalidDataException($"{path}: obsolete packed node flags or gate attributes are not supported. Reconstruct the source project again from the original ZBD files.");
    }

    private static JsonObject? SourceSceneValues(GltfDocument document, string path)
    {
        var scene = EngineValues(document.SceneExtras, path);
        if (scene == null) return null;
        if (scene.ContainsKey("rootFlags"))
            throw new InvalidDataException($"{path}: obsolete rootFlags are not supported. Reconstruct the source project again from the original ZBD files.");
        NumericOperand(scene["sourceFormat"], "sourceFormat", path);
        if (scene.TryGetPropertyValue("sourceFormat", out var version)
            && (!GltfInteger.TryInt64(version, out long number) || number != 4))
            throw new InvalidDataException($"{path}: this source format is not supported. Reconstruct the source project again from the original ZBD files.");
        if (scene.TryGetPropertyValue("loadRoot", out var root) && root is not JsonObject)
            throw new InvalidDataException($"{path}: loadRoot must be an object.");
        return scene;
    }
}
