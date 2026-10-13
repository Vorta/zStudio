using System.Globalization;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;

namespace Recoil.Zbd.Core.Worlds;

public static partial class WorldGltf
{
    /// <summary>The editable node ID is separate from the three uninterpreted bytes stored after it.</summary>
    internal static void WriteNodeZoneValue(JsonObject values, uint word, bool inherit = false)
    {
        values.Remove("zoneWord"); values.Remove("word"); values.Remove("zoneInherited"); values.Remove("inherit");
        values["zone"] = inherit ? JsonValue.Create("inherit") : (word & 0xFF) == 255 ? JsonValue.Create("any") : JsonValue.Create((int)(word & 0xFF));
        if ((word >> 8) != 0) values["zonePadding"] = ZoneBytes(word, 1, 3);
        else values.Remove("zonePadding");
    }

    internal static uint ReadNodeZoneValue(JsonNode? values, uint parent, string path)
    {
        bool inherited = NodeZoneIsInherited(values, path);
        if (inherited && (values?["zoneWord"] != null || values?["word"] != null))
            throw new InvalidDataException($"{path}: an inherited zone conflicts with a legacy packed zone field; remove the packed field.");
        uint? legacy = values?["zoneWord"] is { } old ? Hex(old, "zoneWord", path)
            : values?["word"] is { } oldWord ? Hex(oldWord, "word", path) : null;
        uint zone = inherited ? parent & 0xFF : values?["zone"] is { } id
            ? id is JsonValue stated && stated.TryGetValue<string>(out var text) && text == "any" ? 255u : (uint)Integer(id, "zone", path, 0, 254)
            : legacy.GetValueOrDefault() & 0xFF;
        uint padding = legacy.GetValueOrDefault() & 0xFFFFFF00;
        if (values?["zonePadding"] is { } stored)
        {
            if (values?["zone"] == null) throw new InvalidDataException($"{path}: zonePadding requires a numeric zone ID, \"any\" or \"inherit\".");
            padding = ReadZoneBytes(stored, 1, 3, "zonePadding", path);
        }
        return padding | zone;
    }

    internal static bool NodeZoneIsInherited(JsonNode? values, string path)
    {
        if (values is JsonObject fields && (fields.ContainsKey("zoneInherited") || fields.ContainsKey("inherit")))
            throw new InvalidDataException($"{path}: zoneInherited and inherit are obsolete; remove them and set zone to a numeric ID, \"any\" or \"inherit\".");
        if (values?["zone"] is not { } zone) return values?["zoneWord"] == null && values?["word"] == null;
        NumericOperand(zone, "zone", path);
        if (zone is JsonValue value && value.TryGetValue<string>(out var text))
        {
            if (text == "inherit") return true;
            if (text == "any") return false;
            throw new InvalidDataException($"{path}: zone must be a numeric ID from 0 to 254, \"any\" or \"inherit\".");
        }
        if (Integer(zone, "zone", path, 0, 255) == 255)
            throw new InvalidDataException($"{path}: numeric zone 255 is obsolete; use zone: \"any\" instead.");
        return false;
    }

    /// <summary>Material defaults use the same count-free IDs as individual polygons.</summary>
    internal static void WritePolygonZoneValue(JsonObject values, uint word)
    {
        values.Remove("zone"); values.Remove("zonePadding"); values.Remove("zoneRaw");
        int count = (int)(word & 0xFF);
        if (count > 3)
        {
            values.Remove("zones"); values["zoneRaw"] = ZoneBytes(word, 0, 4);
            return;
        }
        values["zones"] = ZoneBytes(word, 1, count);
        if (HasPolygonZonePadding(word)) values["zonePadding"] = ZoneBytes(word, count + 1, 3 - count);
    }

    internal static uint ReadPolygonZoneValue(JsonNode? values, string path)
    {
        if (values?["zoneRaw"] is { } raw)
        {
            if (values["zones"] != null || values["zone"] != null || values["zonePadding"] != null)
                throw new InvalidDataException($"{path}: zoneRaw cannot also contain editable zones or padding.");
            return ReadRawPolygonZone(raw, "zoneRaw", path);
        }
        if (values?["zones"] is { } zones)
        {
            if (values["zone"] != null) throw new InvalidDataException($"{path}: zones conflicts with the legacy zone word.");
            return ReadPolygonZone(zones, values["zonePadding"], path);
        }
        if (values?["zonePadding"] != null) throw new InvalidDataException($"{path}: zonePadding requires a zones array.");
        return values?["zone"] is { } old ? Hex(old, "zone", path) : DefaultPolygonZone;
    }

    internal static void WritePolygonZoneValues(JsonObject values, IReadOnlyList<uint> words, CancellationToken token)
    {
        if (words.Count > GltfDocument.MaximumDecodedElements)
            throw new InvalidDataException("The source zone attributes exceed their polygon allowance.");
        long bytes = 64;
        foreach (uint word in words)
        {
            token.ThrowIfCancellationRequested(); bytes += PolygonZoneMetadataBytes(word);
            if (bytes > GltfDocument.MaximumMetadataBytes)
                throw new InvalidDataException("The source zone attributes exceed their metadata allowance.");
        }
        JsonArray zones = []; JsonObject padding = [], raw = [];
        for (int i = 0; i < words.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            uint word = words[i]; int count = (int)(word & 0xFF);
            string index = i.ToString(CultureInfo.InvariantCulture);
            if (count > 3) { zones.Add((JsonNode?)null); raw[index] = ZoneBytes(word, 0, 4); }
            else
            {
                zones.Add(ZoneBytes(word, 1, count));
                if (HasPolygonZonePadding(word)) padding[index] = ZoneBytes(word, count + 1, 3 - count);
            }
        }
        values["polygonZones"] = zones;
        values.Remove("polygonZonePadding"); values.Remove("polygonZoneRaw");
        if (padding.Count != 0) values["polygonZonePadding"] = padding;
        if (raw.Count != 0) values["polygonZoneRaw"] = raw;
    }

    internal static uint[] ReadPolygonZoneValues(JsonObject values, int expectedCount, string path, CancellationToken token)
    {
        if (values["polygonZones"] is not JsonArray zones || zones.Count != expectedCount || zones.Count > GltfDocument.MaximumDecodedElements)
            throw new InvalidDataException($"{path}: polygon zone assignments do not match the polygon boundaries.");
        var padding = ZoneIndexMap(values["polygonZonePadding"], zones.Count, "polygonZonePadding", path, token);
        var raw = ZoneIndexMap(values["polygonZoneRaw"], zones.Count, "polygonZoneRaw", path, token);
        uint[] words = new uint[zones.Count];
        for (int i = 0; i < words.Length; i++)
        {
            token.ThrowIfCancellationRequested(); string index = i.ToString(CultureInfo.InvariantCulture);
            if (raw?[index] is { } storedRaw)
            {
                if (zones[i] != null || padding?.ContainsKey(index) == true)
                    throw new InvalidDataException($"{path}: a raw polygon zone cannot also have editable IDs or padding.");
                words[i] = ReadRawPolygonZone(storedRaw, "polygonZoneRaw", path);
            }
            else if (zones[i] is JsonArray ids) words[i] = ReadPolygonZone(ids, padding?[index], path);
            else
            {
                if (padding?.ContainsKey(index) == true) throw new InvalidDataException($"{path}: polygon zone padding requires an ID array.");
                words[i] = Hex(zones[i], "polygonZones", path);
            }
        }
        return words;
    }

    private static JsonObject? ZoneIndexMap(JsonNode? value, int count, string name, string path, CancellationToken token)
    {
        if (value == null) return null;
        if (value is not JsonObject map || map.Count > count) throw new InvalidDataException($"{path}: {name} must be a bounded polygon-index object.");
        foreach (var item in map)
        {
            token.ThrowIfCancellationRequested();
            if (!int.TryParse(item.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
                || index < 0 || index >= count || item.Key != index.ToString(CultureInfo.InvariantCulture) || item.Value is not JsonArray)
                throw new InvalidDataException($"{path}: {name} contains an invalid polygon index or byte array.");
        }
        return map;
    }

    private static uint ReadPolygonZone(JsonNode value, JsonNode? padding, string path)
    {
        if (value is not JsonArray zones || zones.Count > 3)
            throw new InvalidDataException($"{path}: polygon zones must contain zero to three numeric IDs.");
        uint word = 0xFFFFFF00 | (uint)zones.Count;
        for (int i = 0; i < zones.Count; i++)
        {
            int shift = (i + 1) * 8;
            word = word & ~(0xFFu << shift) | (uint)Integer(zones[i], "zones", path, 0, 255) << shift;
        }
        if (padding != null)
        {
            uint mask = zones.Count == 3 ? 0 : uint.MaxValue << ((zones.Count + 1) * 8);
            word = word & ~mask | ReadZoneBytes(padding, zones.Count + 1, 3 - zones.Count, "zonePadding", path);
        }
        return word;
    }

    private static uint ReadRawPolygonZone(JsonNode value, string name, string path)
    {
        uint word = ReadZoneBytes(value, 0, 4, name, path);
        if ((word & 0xFF) <= 3) throw new InvalidDataException($"{path}: valid polygon zones must use an editable ID array.");
        return word;
    }

    private static uint ReadZoneBytes(JsonNode value, int start, int count, string name, string path)
    {
        if (value is not JsonArray bytes || bytes.Count != count)
            throw new InvalidDataException($"{path}: {name} must contain exactly {count} numeric bytes.");
        uint result = 0;
        for (int i = 0; i < bytes.Count; i++) result |= (uint)Integer(bytes[i], name, path, 0, 255) << ((start + i) * 8);
        return result;
    }

    private static JsonArray ZoneBytes(uint word, int start, int count)
    {
        JsonArray result = [];
        for (int i = 0; i < count; i++) result.Add((int)(word >> ((start + i) * 8) & 0xFF));
        return result;
    }

    private static bool HasPolygonZonePadding(uint word)
    {
        int count = (int)(word & 0xFF);
        if (count >= 3) return false;
        uint mask = uint.MaxValue << ((count + 1) * 8);
        return (word & mask) != mask;
    }

    private static long PolygonZoneMetadataBytes(uint word) => (word & 0xFF) > 3 || HasPolygonZonePadding(word) ? 64 : 16;
}
