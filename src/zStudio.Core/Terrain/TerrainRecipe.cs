using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Recoil.Zbd.Core.Terrain;

/// <summary>How a surface treats craters: CanModify (craters form here), ClipTo ("no clip": no crater may overlap it) or neither.</summary>
public enum TerrainCraters { Ignored, Allowed, Blocked }

/// <summary>
/// A polygon's zones: one to three zone numbers (0–254), or <see cref="Any"/>, which the engine treats as "no
/// information" (count 0): it passes every filter and the camera keeps its previous zones.
/// </summary>
public sealed record TerrainZones
{
    public static TerrainZones Any { get; } = new([]);
    public IReadOnlyList<byte> Ids { get; }
    public TerrainZones(IReadOnlyList<byte> ids)
    {
        if (ids.Count > 3) throw new InvalidDataException("A polygon holds at most three zones.");
        if (ids.Any(i => i == 0xFF)) throw new InvalidDataException("Zone 255 is reserved; use \"any\".");
        Ids = ids.Distinct().Order().ToArray();
    }
    public bool IsAny => Ids.Count == 0;
    /// <summary>The polygon zone word: count byte, then up to three ids padded with 0xFF.</summary>
    public uint Word
    {
        get
        {
            uint word = 0xFFFFFF00 | (uint)Ids.Count;
            for (int i = 0; i < Ids.Count; i++) word = word & ~(0xFFu << (8 * (i + 1))) | (uint)Ids[i] << (8 * (i + 1));
            return word;
        }
    }
    public bool Equals(TerrainZones? other) => other != null && Ids.SequenceEqual(other.Ids);
    public override int GetHashCode() => Ids.Aggregate(17, (h, i) => h * 31 + i);
    public override string ToString() => IsAny ? "any" : string.Join(" ", Ids);
}

/// <summary>
/// Gameplay attributes a layer of a terrain recipe sets; null leaves the value of the layers before it. Node attributes
/// (<see cref="NodeZone"/>, <see cref="NodeGate"/>, <see cref="Collision"/>, <see cref="Standable"/>, <see cref="Craters"/>,
/// <see cref="Flags"/>) separate pieces into different nodes; polygon attributes (<see cref="Zones"/>, <see cref="Soil"/>,
/// <see cref="Priority"/>) only cut polygons.
/// </summary>
public sealed record TerrainAttributes
{
    public static TerrainAttributes None { get; } = new();
    public const int AutoZone = -1, AnyZone = 0xFF;
    public TerrainZones? Zones { get; init; }
    /// <summary>The node's zone: 0–254, <see cref="AnyZone"/>, or <see cref="AutoZone"/> (the one zone its polygons share, else any).</summary>
    public int? NodeZone { get; init; }
    /// <summary>The zone gate (node flag 0x01000000), which makes altitude probes check the node's zone.</summary>
    public bool? NodeGate { get; init; }
    public bool? Collision { get; init; }
    public bool? Standable { get; init; }
    public TerrainCraters? Craters { get; init; }
    /// <summary>Material soil: 0 default, 1 water, 2 seafloor, 3 quicksand, 4 lava, 5 fire, 6–99 named by LoadSoils.</summary>
    public uint? Soil { get; init; }
    /// <summary>Polygon draw priority (Object3DSetPriority values).</summary>
    public int? Priority { get; init; }
    /// <summary>The node's carried flag bits exactly (<see cref="Worlds.WorldGltf.CarriedFlags"/>), for combinations the presets cannot express.</summary>
    public uint? Flags { get; init; }
    public bool IsEmpty => this == None;
    internal static readonly string[] SoilNames = ["default", "water", "seafloor", "quicksand", "lava", "fire"];
    private static readonly string[] Keys = ["zones", "nodeZone", "nodeGate", "collision", "standable", "craters", "soil", "priority", "flags"];

    /// <summary>
    /// Attributes from JSON (<c>{ "zones": [1], "craters": "blocked", … }</c>); problems start with <paramref name="what"/>.
    /// With <paramref name="patch"/>, the result starts from <paramref name="patch"/>'s values: keys present replace them and
    /// a null value removes the override, so the layers before show through again.
    /// </summary>
    public static TerrainAttributes FromJson(JsonNode? node, string what, TerrainAttributes? patch = null)
    {
        if (node is null) return patch ?? None;
        var o = node as JsonObject ?? throw Error("must be an object");
        foreach (var key in o.Select(p => p.Key))
            if (!Keys.Contains(key)) throw Error($"has an unknown attribute {JsonData.ShownText(key)} (known: {string.Join(", ", Keys)})");
        var a = patch ?? None;
        bool Has(string key, out JsonNode? value) { bool has = o.TryGetPropertyValue(key, out value); return has; }
        if (Has("zones", out var zones)) a = a with
        {
            Zones = zones switch
            {
                null => null,
                JsonValue v when v.TryGetValue(out string? any) && any == "any" => TerrainZones.Any,
                JsonArray ids when ids.Count is >= 1 and <= 3 => new(ids.Select(i => i is JsonValue z && z.TryGetValue(out int id) && id is >= 0 and <= 254 ? (byte)id : throw Error("zones are numbers 0–254")).ToArray()),
                _ => throw Error("zones is \"any\" or a list of one to three zone numbers"),
            }
        };
        if (Has("nodeZone", out var nodeZone)) a = a with
        {
            NodeZone = nodeZone switch
            {
                null => null,
                JsonValue v when v.TryGetValue(out string? word) && word is "auto" or "any" => word == "auto" ? AutoZone : AnyZone,
                JsonValue v when v.TryGetValue(out int zone) && zone is >= 0 and <= 254 => zone,
                _ => throw Error("nodeZone is a zone number 0–254, \"any\" or \"auto\""),
            }
        };
        if (Has("nodeGate", out var gate)) a = a with { NodeGate = Bool(gate, "nodeGate") };
        if (Has("collision", out var collision)) a = a with { Collision = Bool(collision, "collision") };
        if (Has("standable", out var standable)) a = a with { Standable = Bool(standable, "standable") };
        if (Has("craters", out var craters)) a = a with
        {
            Craters = craters switch
            {
                null => null,
                JsonValue v when v.TryGetValue(out string? mode) && Enum.TryParse<TerrainCraters>(mode, true, out var parsed) && mode == mode.ToLowerInvariant() => parsed,
                _ => throw Error("craters is \"allowed\", \"blocked\" or \"ignored\""),
            }
        };
        if (Has("soil", out var soil)) a = a with
        {
            Soil = soil switch
            {
                null => null,
                JsonValue v when v.TryGetValue(out string? name) && Array.IndexOf(SoilNames, name) is int index and >= 0 => (uint)index,
                JsonValue v when v.TryGetValue(out int number) && number is >= 0 and <= 99 => (uint)number,
                _ => throw Error("soil is default, water, seafloor, quicksand, lava, fire or a number 6–99"),
            }
        };
        if (Has("priority", out var priority)) a = a with
        {
            Priority = priority switch { null => null, JsonValue v when v.TryGetValue(out int p) && p is >= 0 and <= 255 => p, _ => throw Error("priority is 0–255") }
        };
        if (Has("flags", out var flags)) a = a with
        {
            Flags = flags switch
            {
                null => null,
                JsonValue v when v.TryGetValue(out string? hex) && hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) && uint.TryParse(hex.AsSpan(2), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint word)
                    && (word & ~Worlds.WorldGltf.CarriedFlags) == 0 => word,
                _ => throw Error("flags is a hexadecimal word (\"0x…\") of the carried node flags"),
            }
        };
        return a;
        InvalidDataException Error(string message) => new($"{what} {message}.");
        bool? Bool(JsonNode? value, string key) => value switch { null => null, JsonValue v when v.TryGetValue(out bool b) => b, _ => throw Error($"{key} is true or false") };
    }

    /// <summary>The attributes as JSON, with only the values this layer sets.</summary>
    public JsonObject ToJson()
    {
        JsonObject o = new();
        if (Zones is { } z) o["zones"] = z.IsAny ? "any" : new JsonArray(z.Ids.Select(i => (JsonNode?)JsonValue.Create((int)i)).ToArray());
        if (NodeZone is { } nz) o["nodeZone"] = nz == AutoZone ? "auto" : nz == AnyZone ? "any" : JsonValue.Create(nz);
        if (NodeGate is { } g) o["nodeGate"] = g;
        if (Collision is { } c) o["collision"] = c;
        if (Standable is { } s) o["standable"] = s;
        if (Craters is { } cr) o["craters"] = cr.ToString().ToLowerInvariant();
        if (Soil is { } soil) o["soil"] = soil < SoilNames.Length ? SoilNames[soil] : JsonValue.Create((int)soil);
        if (Priority is { } p) o["priority"] = p;
        if (Flags is { } f) o["flags"] = $"0x{f:X8}";
        return o;
    }
}

/// <summary>A glTF node whose mesh is terrain, and the attributes it starts with.</summary>
public sealed record TerrainSurface(string Id, string Model, string Node, TerrainAttributes Defaults);

/// <summary>A polygon of a region's shape in plan view (x, z), with holes.</summary>
public sealed record TerrainOutline(IReadOnlyList<Vector2> Outer, IReadOnlyList<IReadOnlyList<Vector2>> Holes);

/// <summary>
/// Where a region applies: polygons in plan view (x, z), optionally limited to a height range. A polygon of the terrain
/// is cut along the outlines and each part is inside when its centre is; the height range selects parts by their centre.
/// </summary>
public sealed record TerrainShape(IReadOnlyList<TerrainOutline> Polygons, float? MinY = null, float? MaxY = null);

/// <summary>A named region: the attributes it sets on the surfaces it lists (all when empty), inside its shape (everywhere when null).</summary>
public sealed record TerrainRegion(string Name, IReadOnlyList<string> Surfaces, TerrainShape? Shape, TerrainAttributes Set);

/// <summary>
/// A terrain recipe (<c>*.terrain.json</c>): which glTF surfaces are terrain and the gameplay attributes painted on them,
/// applied in order — the recipe's defaults, each surface's defaults, then the regions. <see cref="Compiler"/> is the
/// version of the splitting rules. A recipe holds no editor state.
/// </summary>
public sealed record TerrainRecipe(int Compiler, IReadOnlyList<TerrainSurface> Surfaces, TerrainAttributes Defaults, IReadOnlyList<TerrainRegion> Regions)
{
    public const string Format = "recoil-terrain", Extension = ".terrain.json";
    public const int Version = 1, CurrentCompiler = 1;
    public const int MaximumSurfaces = 256, MaximumRegions = 4096, MaximumPolygons = 1024, MaximumRingPoints = 200_000, MaximumPoints = 1_000_000;
    public const float MaximumCoordinate = 1_000_000;

    /// <summary>Reads and validates a recipe; problems name <paramref name="source"/>.</summary>
    public static TerrainRecipe Parse(ReadOnlySpan<byte> json, string source)
    {
        if (json.Length > 64 * 1024 * 1024) throw new InvalidDataException($"{source} is larger than 64 MB.");
        JsonObject root;
        try { root = JsonNode.Parse(json.ToArray(), documentOptions: new() { MaxDepth = 32 }) as JsonObject ?? throw Error("is not a JSON object"); }
        catch (JsonException ex) { throw new InvalidDataException($"{source} is not valid JSON: {ex.Message}", ex); }
        if (Text(root["format"], "format") != Format) throw Error($"format must be \"{Format}\"");
        if (Int(root["version"], "version") != Version) throw Error($"only version {Version} is known");
        int compiler = Int(root["compiler"], "compiler");
        if (compiler != CurrentCompiler) throw Error($"was written for splitting rules {compiler}; this zStudio has rules {CurrentCompiler}");
        if (root["surfaces"] is not JsonArray surfaceList || surfaceList.Count is 0 or > MaximumSurfaces) throw Error($"lists 1–{MaximumSurfaces} surfaces");
        List<TerrainSurface> surfaces = [];
        foreach (var entry in surfaceList)
        {
            var s = entry as JsonObject ?? throw Error("has a surface that is not an object");
            string id = Name(s["id"], "surface id");
            if (surfaces.Any(x => x.Id == id)) throw Error($"lists surface {id} twice");
            string model = Path(s["model"], $"surface {id} model"), node = Text(s["node"], $"surface {id} node");
            if (node.Length is 0 or > 128) throw Error($"surface {id} has an invalid node name");
            if (surfaces.Any(x => x.Model.Equals(model, StringComparison.OrdinalIgnoreCase) && x.Node == node)) throw Error($"uses node {node} of {model} for two surfaces");
            surfaces.Add(new(id, model, node, Attributes(s["defaults"], $"surface {id} defaults")));
        }
        var defaults = Attributes(root["defaults"], "defaults");
        List<TerrainRegion> regions = []; long points = 0;
        if (root["regions"] is { } regionNode)
        {
            if (regionNode is not JsonArray regionList || regionList.Count > MaximumRegions) throw Error($"lists at most {MaximumRegions} regions");
            foreach (var entry in regionList)
            {
                var r = entry as JsonObject ?? throw Error("has a region that is not an object");
                string name = Text(r["name"], "region name");
                if (name.Length is 0 or > 128) throw Error("has a region without a name, or with one over 128 characters");
                List<string> on = [];
                if (r["surfaces"] is { } list)
                    foreach (var id in list as JsonArray ?? throw Error($"region {name} surfaces must be a list"))
                    {
                        string surface = Text(id, $"region {name} surface");
                        if (!surfaces.Any(s => s.Id == surface)) throw Error($"region {name} names unknown surface {JsonData.ShownText(surface)}");
                        if (!on.Contains(surface)) on.Add(surface);
                    }
                if (regions.Any(x => x.Name == name)) throw Error($"has two regions named {name}");
                TerrainShape? shape = r["shape"] is { } shapeNode ? Shape(shapeNode, name, ref points) : null;
                var set = Attributes(r["set"], $"region {name} set");
                regions.Add(new(name, on, shape, set));
            }
        }
        return new(compiler, surfaces, defaults, regions);

        InvalidDataException Error(string message) => new($"{source} {message}.");
        string Text(JsonNode? node, string what) => node is JsonValue v && v.TryGetValue(out string? text) ? text : throw Error($"needs {what} as text");
        int Int(JsonNode? node, string what) => node is JsonValue v && v.TryGetValue(out int i) ? i : throw Error($"needs {what} as a whole number");
        float Real(JsonNode? node, string what)
        {
            if (node is not JsonValue v || !v.TryGetValue(out double d) || !double.IsFinite(d) || Math.Abs(d) > MaximumCoordinate) throw Error($"needs {what} as a number within ±{MaximumCoordinate:N0}");
            return (float)d;
        }
        string Name(JsonNode? node, string what)
        {
            string text = Text(node, what);
            if (text.Length is 0 or > 32 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) throw Error($"{what} \"{JsonData.ShownText(text)}\" must be 1–32 letters, digits, _ or -");
            return text;
        }
        string Path(JsonNode? node, string what)
        {
            string text = Text(node, what).Replace('\\', '/');
            // Relative to the recipe: leading ".." steps, then plain names (the project boundary is checked where it is read).
            var parts = text.Split('/').SkipWhile(p => p == "..").ToArray();
            if (text.Length is 0 or > 260 || text.Contains(':') || parts.Length == 0 || parts.Any(p => p is "" or "." or ".."))
                throw Error($"{what} must be a relative file path");
            return text;
        }
        TerrainAttributes Attributes(JsonNode? node, string what) => TerrainAttributes.FromJson(node, $"{source} {what}");
        TerrainShape Shape(JsonNode node, string region, ref long total)
        {
            var o = node as JsonObject ?? throw Error($"region {region} shape must be an object");
            if (o["plane"] is { } plane && (plane is not JsonValue pv || !pv.TryGetValue(out string? p) || p != "xz")) throw Error($"region {region} shape plane must be \"xz\" (plan view)");
            float? min = o["minY"] is null ? null : Real(o["minY"], $"region {region} minY"), max = o["maxY"] is null ? null : Real(o["maxY"], $"region {region} maxY");
            if (min > max) throw Error($"region {region} minY is above maxY");
            // An empty list covers nothing (everything was erased).
            if (o["polygons"] is not JsonArray polygons || polygons.Count > MaximumPolygons) throw Error($"region {region} shape lists at most {MaximumPolygons} polygons");
            List<TerrainOutline> outlines = [];
            foreach (var entry in polygons)
            {
                var polygon = entry as JsonObject ?? throw Error($"region {region} has a polygon that is not an object");
                var outer = Ring(polygon["outer"], region, ref total);
                List<IReadOnlyList<Vector2>> holes = [];
                if (polygon["holes"] is { } h) foreach (var hole in h as JsonArray ?? throw Error($"region {region} holes must be a list")) holes.Add(Ring(hole, region, ref total));
                outlines.Add(new(outer, holes));
            }
            return new(outlines, min, max);
        }
        IReadOnlyList<Vector2> Ring(JsonNode? node, string region, ref long total)
        {
            if (node is not JsonArray ring || ring.Count is < 3 or > MaximumRingPoints) throw Error($"region {region} has a ring without 3–{MaximumRingPoints} points");
            if ((total += ring.Count) > MaximumPoints) throw Error($"has more than {MaximumPoints:N0} shape points");
            return ring.Select(p => p is JsonArray { Count: 2 } xz ? new Vector2(Real(xz[0], $"region {region} point"), Real(xz[1], $"region {region} point")) : throw Error($"region {region} points are [x, z]")).ToArray();
        }
    }

    /// <summary>The recipe as canonical JSON: fixed key order, shortest round-trip numbers, two-space indentation.</summary>
    public byte[] Write()
    {
        JsonObject root = new()
        {
            ["format"] = Format, ["version"] = Version, ["compiler"] = Compiler,
            ["surfaces"] = new JsonArray(Surfaces.Select(s =>
            {
                JsonObject o = new() { ["id"] = s.Id, ["model"] = s.Model, ["node"] = s.Node };
                if (!s.Defaults.IsEmpty) o["defaults"] = Json(s.Defaults);
                return (JsonNode?)o;
            }).ToArray()),
        };
        if (!Defaults.IsEmpty) root["defaults"] = Json(Defaults);
        if (Regions.Count > 0)
            root["regions"] = new JsonArray(Regions.Select(r =>
            {
                JsonObject o = new() { ["name"] = r.Name };
                if (r.Surfaces.Count > 0) o["surfaces"] = new JsonArray(r.Surfaces.Select(s => (JsonNode?)JsonValue.Create(s)).ToArray());
                if (r.Shape is { } shape)
                {
                    JsonObject s = new() { ["plane"] = "xz" };
                    if (shape.MinY is { } min) s["minY"] = min;
                    if (shape.MaxY is { } max) s["maxY"] = max;
                    s["polygons"] = new JsonArray(shape.Polygons.Select(p =>
                    {
                        JsonObject polygon = new() { ["outer"] = Ring(p.Outer) };
                        if (p.Holes.Count > 0) polygon["holes"] = new JsonArray(p.Holes.Select(h => (JsonNode?)Ring(h)).ToArray());
                        return (JsonNode?)polygon;
                    }).ToArray());
                    o["shape"] = s;
                }
                o["set"] = Json(r.Set);
                return (JsonNode?)o;
            }).ToArray());
        string text = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n") + "\n");

        static JsonArray Ring(IReadOnlyList<Vector2> ring) => new(ring.Select(p => (JsonNode?)new JsonArray(p.X, p.Y)).ToArray());
        static JsonObject Json(TerrainAttributes a) => a.ToJson();
    }
}
