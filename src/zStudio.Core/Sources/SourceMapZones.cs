using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

public sealed record SourceMapZoneReference(int Node, string Asset, string Spelling);
public sealed record SourceMapZoneAsset(string LogicalPath, string GeometryPath, WorldZoneProfile Profile, IReadOnlyList<SourceMapZoneReference> References);
public sealed record SourceMapZoneLabel(byte Id, string Label);

/// <summary>Authoritative per-map zone assignments and logical model identities, independent of shared geometry.</summary>
public sealed class SourceMapZones
{
    public const int Version = 1, MaximumBytes = 32 << 20, MaximumAssets = 16_384;
    public const int MaximumTargets = 2_000_000, MaximumPathCharacters = 4096, MaximumLabelCharacters = 256;
    public IReadOnlyList<SourceMapZoneAsset> Assets { get; }
    public IReadOnlyList<SourceMapZoneLabel> Labels { get; }
    private readonly Dictionary<string, SourceMapZoneAsset> assets;

    public SourceMapZones(IReadOnlyList<SourceMapZoneAsset> assets, IReadOnlyList<SourceMapZoneLabel>? labels = null)
    {
        Assets = assets; Labels = labels ?? [new(255, "Any")];
        Validate(default);
        this.assets = new(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
            if (!this.assets.TryAdd(asset.LogicalPath, asset)) throw Bad("A logical model path is assigned more than once.");
    }

    public bool TryGetAsset(string path, out SourceMapZoneAsset asset) => assets.TryGetValue(SourceWorkspace.Normalize(path), out asset!);
    public string Label(byte id) => id == 255 ? "Any" : Labels.FirstOrDefault(z => z.Id == id)?.Label ?? $"Zone {id}";
    public static string PathForMission(string mission)
    {
        if (mission.Length is < 2 or > 4 || mission[0] != 'm' || !mission.Skip(1).All(char.IsAsciiDigit))
            throw Bad("The map name must be m followed by its number.");
        return $"data/{mission}/meta/zones.json";
    }

    public static SourceMapZones Parse(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > MaximumBytes) throw Bad("The map zone file exceeds 32 MiB.");
        try
        {
            // Bound scalar decoding and token count before JsonDocument creates its token table or strings.
            Utf8JsonReader scan = new(bytes.Span, new JsonReaderOptions { MaxDepth = 16 });
            int tokens = 0;
            while (scan.Read())
            {
                if ((++tokens & 1023) == 0) token.ThrowIfCancellationRequested();
                if (tokens > 8 * MaximumTargets || scan.TokenType is JsonTokenType.String or JsonTokenType.PropertyName && scan.ValueSpan.Length > 6 * MaximumPathCharacters)
                    throw Bad("The map zone file contains excessive metadata.");
            }
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            var root = Object(document.RootElement, "version", "labels", "assets");
            if (root.GetProperty("version").GetInt32() != Version) throw Bad("The map zone version is unsupported.");
            List<SourceMapZoneLabel> labels = [];
            foreach (var entry in Array(root.GetProperty("labels"), 256).EnumerateArray())
            {
                Object(entry, "id", "label");
                labels.Add(new(checked((byte)entry.GetProperty("id").GetInt32()), Text(entry.GetProperty("label"), MaximumLabelCharacters)));
            }
            List<SourceMapZoneAsset> assets = []; int targets = 0;
            void Charge(int count) { token.ThrowIfCancellationRequested(); if (count > MaximumTargets - targets) throw Bad("The map has too many zone targets."); targets += count; }
            foreach (var entry in Array(root.GetProperty("assets"), MaximumAssets).EnumerateArray())
            {
                Object(entry, "path", "geometry", "profile", "references");
                var profile = Object(entry.GetProperty("profile"), "fingerprint", "nodes", "meshes", "loadRoot");
                var nodeValues = Array(profile.GetProperty("nodes"), MaximumTargets); Charge(nodeValues.GetArrayLength());
                WorldNodeZone[] nodes = new WorldNodeZone[nodeValues.GetArrayLength()]; int nodeIndex = 0;
                foreach (var node in nodeValues.EnumerateArray()) { if ((nodeIndex & 1023) == 0) token.ThrowIfCancellationRequested(); nodes[nodeIndex++] = Node(node); }
                List<IReadOnlyList<uint>> meshes = [];
                foreach (var mesh in Array(profile.GetProperty("meshes"), MaximumTargets).EnumerateArray())
                {
                    var words = Array(mesh, MaximumTargets); Charge(words.GetArrayLength() + 1);
                    uint[] polygonWords = new uint[words.GetArrayLength()]; int polygon = 0;
                    foreach (var word in words.EnumerateArray()) { if ((polygon & 1023) == 0) token.ThrowIfCancellationRequested(); polygonWords[polygon++] = Word(word); }
                    meshes.Add(polygonWords);
                }
                List<SourceMapZoneReference> references = [];
                foreach (var reference in Array(entry.GetProperty("references"), MaximumTargets).EnumerateArray())
                {
                    Charge(1); Object(reference, "node", "asset", "spelling");
                    references.Add(new(reference.GetProperty("node").GetInt32(), Text(reference.GetProperty("asset"), MaximumPathCharacters), Text(reference.GetProperty("spelling"), MaximumPathCharacters)));
                }
                WorldNodeZone? loadRoot = profile.TryGetProperty("loadRoot", out var load) ? Node(load) : null;
                assets.Add(new(Text(entry.GetProperty("path"), MaximumPathCharacters), Text(entry.GetProperty("geometry"), MaximumPathCharacters),
                    new(Text(profile.GetProperty("fingerprint"), 64), nodes, meshes, loadRoot), references));
            }
            token.ThrowIfCancellationRequested();
            return new(assets, labels);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        { throw Bad("The map zone file is malformed: " + JsonData.ShownText(ex.Message, 256)); }
    }

    public byte[] Write(CancellationToken token = default)
    {
        Validate(token); // Admit counts and escaped strings before creating any JSON representation.
        Buffer buffer = new(token); int written = 0;
        void Poll() { if ((written++ & 1023) == 0) token.ThrowIfCancellationRequested(); }
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject(); writer.WriteNumber("version", Version);
            writer.WriteStartArray("labels");
            foreach (var label in Labels.OrderBy(z => z.Id))
            { writer.WriteStartObject(); writer.WriteNumber("id", label.Id); writer.WriteString("label", label.Label); writer.WriteEndObject(); }
            writer.WriteEndArray(); writer.WriteStartArray("assets");
            foreach (var asset in Assets.OrderBy(a => a.LogicalPath, StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested(); writer.WriteStartObject(); writer.WriteString("path", asset.LogicalPath); writer.WriteString("geometry", asset.GeometryPath);
                writer.WriteStartObject("profile"); writer.WriteString("fingerprint", asset.Profile.Fingerprint);
                writer.WriteStartArray("nodes"); foreach (var node in asset.Profile.Nodes) { Poll(); WriteNode(writer, node); } writer.WriteEndArray();
                writer.WriteStartArray("meshes");
                foreach (var mesh in asset.Profile.MeshPolygons)
                { token.ThrowIfCancellationRequested(); writer.WriteStartArray(); foreach (uint word in mesh) { Poll(); writer.WriteStringValue(Hex(word)); } writer.WriteEndArray(); }
                writer.WriteEndArray();
                if (asset.Profile.LoadRoot is { } root) { writer.WritePropertyName("loadRoot"); WriteNode(writer, root); }
                writer.WriteEndObject(); writer.WriteStartArray("references");
                foreach (var reference in asset.References.OrderBy(r => r.Node))
                { writer.WriteStartObject(); writer.WriteNumber("node", reference.Node); writer.WriteString("asset", reference.Asset); writer.WriteString("spelling", reference.Spelling); writer.WriteEndObject(); }
                writer.WriteEndArray(); writer.WriteEndObject();
            }
            writer.WriteEndArray(); writer.WriteEndObject();
        }
        return buffer.Bytes();
    }

    private void Validate(CancellationToken token)
    {
        if (Assets.Count > MaximumAssets || Labels.Count > 256) throw Bad("The map has too many logical assets or zone labels.");
        long size = 128; int targets = 0; HashSet<byte> labels = [];
        void Reserve(long bytes, int count = 0)
        { token.ThrowIfCancellationRequested(); if (bytes > MaximumBytes - size || count > MaximumTargets - targets) throw Bad("The map zone assignments exceed their serialization allowance."); size += bytes; targets += count; }
        foreach (var label in Labels)
        {
            if (label.Label.Length is < 1 or > MaximumLabelCharacters || !labels.Add(label.Id) || label.Id == 255 && label.Label != "Any") throw Bad("Zone labels must be unique, nonempty and bounded; zone 255 is Any.");
            Reserve(48 + 6L * label.Label.Length);
        }
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in Assets)
        {
            ValidatePath(asset.LogicalPath); ValidatePath(asset.GeometryPath);
            if (!paths.Add(asset.LogicalPath)) throw Bad("A logical model path is assigned more than once.");
            var profile = asset.Profile;
            if (profile.LoadRoot is { Inherit: true }) throw Bad("A load-root zone is absolute; it cannot inherit from a model parent.");
            if (profile.Fingerprint.Length != 64 || profile.Fingerprint.Any(c => !Uri.IsHexDigit(c))) throw Bad("A zone profile requires its 64-digit structural fingerprint.");
            Reserve(256 + 6L * (asset.LogicalPath.Length + asset.GeometryPath.Length));
            Reserve(60L * profile.Nodes.Count, profile.Nodes.Count);
            foreach (var mesh in profile.MeshPolygons) Reserve(4 + 14L * mesh.Count, checked(mesh.Count + 1));
            HashSet<int> referenced = [];
            foreach (var reference in asset.References)
            {
                if (reference.Node < 0 || reference.Node >= profile.Nodes.Count || !referenced.Add(reference.Node)) throw Bad("A reference target must name one unique source node.");
                ValidatePath(reference.Asset);
                if (reference.Spelling.Length is < 1 or > MaximumPathCharacters || reference.Spelling.Contains('\\') || reference.Spelling.Contains(':') || reference.Spelling.StartsWith('/')) throw Bad("A reference spelling must be a bounded relative model URI.");
                if (!WorldAssembler.Relative(asset.LogicalPath, reference.Spelling).Equals(reference.Asset, StringComparison.OrdinalIgnoreCase)) throw Bad("A reference spelling does not resolve to its logical asset.");
                Reserve(96 + 6L * (reference.Asset.Length + reference.Spelling.Length), 1);
            }
        }
    }

    private static void ValidatePath(string path)
    {
        if (path.Length is < 1 or > MaximumPathCharacters || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.Split('/').Any(p => p.Length == 0 || p is "." or "..") || !path.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
            throw Bad("A logical asset and its geometry must have normalized project-relative paths below data/.");
        SourceProject.RequireSource(path);
        if (!path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".glb", StringComparison.OrdinalIgnoreCase))
            throw Bad("A logical asset and its geometry must be glTF or GLB models.");
    }
    private static JsonElement Object(JsonElement value, params string[] properties)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Bad("Expected a zone object.");
        HashSet<string> seen = [];
        foreach (var property in value.EnumerateObject())
            if (!properties.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw Bad("Unknown or duplicate zone property.");
        return value;
    }
    private static JsonElement Array(JsonElement value, int maximum)
    { if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > maximum) throw Bad("Invalid or excessive zone array."); return value; }
    private static string Text(JsonElement value, int maximum)
    { if (value.ValueKind != JsonValueKind.String || value.GetRawText().Length > 6L * maximum + 2) throw Bad("Invalid zone text."); var text = value.GetString()!; if (text.Length > maximum) throw Bad("Zone text is too long."); return text; }
    private static uint Word(JsonElement value)
    { var word = Text(value, 10); if (word.Length != 10 || !word.StartsWith("0x", StringComparison.Ordinal) || !uint.TryParse(word.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint result)) throw Bad("Zone words must contain exactly eight hexadecimal digits after 0x."); return result; }
    private static WorldNodeZone Node(JsonElement value) { Object(value, "word", "gate", "inherit"); return new(Word(value.GetProperty("word")), value.GetProperty("gate").GetBoolean(), value.TryGetProperty("inherit", out var inherited) && inherited.GetBoolean()); }
    private static string Hex(uint word) => "0x" + word.ToString("X8", CultureInfo.InvariantCulture);
    private static void WriteNode(Utf8JsonWriter writer, WorldNodeZone node)
    { writer.WriteStartObject(); writer.WriteString("word", Hex(node.Word)); writer.WriteBoolean("gate", node.Gate); if (node.Inherit) writer.WriteBoolean("inherit", true); writer.WriteEndObject(); }
    private static InvalidDataException Bad(string message) => new("Map zones: " + message);

    private sealed class Buffer(CancellationToken token) : IBufferWriter<byte>
    {
        private byte[] bytes = []; private int count;
        public void Advance(int value) { token.ThrowIfCancellationRequested(); if (value < 0 || value > bytes.Length - count) throw new InvalidOperationException(); count += value; }
        public Memory<byte> GetMemory(int sizeHint = 0) { Reserve(sizeHint); return bytes.AsMemory(count); }
        public Span<byte> GetSpan(int sizeHint = 0) { Reserve(sizeHint); return bytes.AsSpan(count); }
        private void Reserve(int hint)
        {
            token.ThrowIfCancellationRequested(); hint = Math.Max(1, hint);
            if (hint > MaximumBytes - count) throw Bad("Serialized zone assignments exceed 32 MiB.");
            if (hint > bytes.Length - count) System.Array.Resize(ref bytes, (int)Math.Min(MaximumBytes, Math.Max((long)count + hint, Math.Max(4096L, 2L * bytes.Length))));
        }
        public byte[] Bytes() => bytes.AsSpan(0, count).ToArray();
    }
}
