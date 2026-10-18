using System.Text;
using System.Text.Json.Nodes;
using zStudio.Core.Gltf;
using zStudio.Core;
using zStudio.Core.Formats;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;
using Xunit;

namespace zStudio.Tests;

public sealed class SourceMapZonesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    [Fact]
    public void ReconstructionReferencesOneOriginalFileWithIndependentEmbeddedPlacementProfiles()
    {
        GameZWorld world = new();
        WorldNode root = new("loaded", WorldNodeClass.Object3D), map = new("world", WorldNodeClass.World);
        world.Nodes.Add(map); world.Nodes.Add(root);
        WorldMaterial material = new() { Color = new(1, 2, 3), Flags = 0xFF };
        foreach (uint zone in new uint[] { 0xFFFF0001, 0xFFFF0101 })
        {
            ModelBuilder builder = new();
            builder.Add(new([new(0, 0, 0), new(1, 0, 0), new(0, 1, 0)], [], [], [], material, Zone: zone)); builder.Finish();
            WorldNode reference = new("lturret.flt", WorldNodeClass.Object3D);
            WorldNode body = new("turret", WorldNodeClass.Object3D) { Model = builder.Model, Zone = zone == 0xFFFF0001 ? 0u : 1u };
            root.Children.Add(reference); reference.Parents.Add(root); reference.Children.Add(body); body.Parents.Add(reference);
            world.Nodes.Add(reference); world.Nodes.Add(body); world.Models.Add(builder.Model);
        }
        world.Materials.Add(material);
        byte[] bytes = GameZWriter.Write(world, Token);
        var shipped = GameZWorldReader.FromDocument(FormatRegistry.Default.OpenBytes("gamez.zbd", bytes, token: Token), Token);
        var script = GameGenScriptText.Tokenize("NewWorld world\nSetModelDirectory ../data/m3/models\nLoadGameGen assembly.flt loaded\nGameZWriteZBDFile gamez.zbd\n");
        var outputs = WorldSources.Reconstruct([new(3, shipped)], _ => script, (_, _) => null, new HashSet<string>(), _ => 0, [], Token);
        Assert.DoesNotContain(outputs, o => o.Path.EndsWith("/meta/zones.json", StringComparison.Ordinal));
        var files = outputs.ToDictionary(o => o.Path, o => o.Bytes, StringComparer.OrdinalIgnoreCase);
        string physical = Assert.Single(outputs, o => Path.GetFileName(o.Path) == "lturret.gltf").Path;
        string assembly = Assert.Single(outputs, o => Path.GetFileName(o.Path) == "assembly.gltf").Path;
        Dictionary<string, GltfDocument> parsed = new(StringComparer.OrdinalIgnoreCase);
        GltfDocument Read(string path) => parsed.TryGetValue(path, out var document) ? document
            : parsed[path] = GltfDocument.Read(files[path], uri => files[WorldAssembler.Relative(path, uri)], Token);
        var shared = Read(physical);
        var defaults = WorldGltf.CaptureZoneProfile(shared, token: Token);
        var master = Read(assembly);
        var references = master.AllNodes().Where(n => n.Extras?[WorldGltf.Key]?["ref"] != null).ToArray();
        Assert.Equal(2, references.Length);
        Assert.All(references, n => Assert.Equal(physical, WorldAssembler.Relative(assembly, n.Extras![WorldGltf.Key]!["ref"]!.GetValue<string>())));
        var profiles = references.Select(n => n.Extras![WorldGltf.Key]!["zoneOverrides"] is JsonObject value
            ? WorldGltf.ReadZoneOverride(value, Token) : defaults).ToArray();
        Assert.Equal(new uint[] { 0xFFFF0001, 0xFFFF0101 }, profiles.SelectMany(p => p.MeshPolygons.SelectMany(words => words)).Order());
        // The first placement inherits its parent; the other explicitly overrides its ID.
        // Resolve the actual hierarchy, since a standalone inherited profile has no parent ID.
        Assert.True(Assert.Single(profiles[0].Nodes).Inherit);
        Assert.False(Assert.Single(profiles[1].Nodes).Inherit);
        WorldGltf.ImportContext context = new()
        {
            World = new(), Token = Token, TextureName = (uri, name, _) => name ?? uri,
            Reference = (uri, from) => { string path = WorldAssembler.Relative(from, uri); return (Read(path), path); },
        };
        var imported = WorldGltf.Import(master, assembly, WorldGltf.EmbeddedLoadRoot(master)!.Word & 255u, context);
        Assert.Equal(new uint[] { 0, 1 }, WorldAssembler.Subtree(imported).Where(n => n.Name == "turret").Select(n => n.Zone).Order());
        Assert.Equal(2, outputs.Count(o => o.Path.EndsWith(".gltf", StringComparison.Ordinal)));
    }
}
