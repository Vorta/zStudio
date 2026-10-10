using System.Buffers.Binary;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class GltfStrideExtentTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ASingleElementNeedsOnlyItsOccupiedBytes(bool statedStride)
    {
        // Mesh 0 is unused, but the reader validates every mesh. Its one position needs 12 bytes even with
        // a stride of 16: there is no next element. The selected mesh remains an ordinary, useful triangle.
        byte[] buffer = new byte[51];
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(24), 1);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(40), 1);
        JsonObject root = JsonNode.Parse("""
            {
              "asset":{"version":"2.0"},
              "buffers":[{"uri":"mesh.bin","byteLength":51}],
              "bufferViews":[
                {"buffer":0,"byteOffset":0,"byteLength":12,"target":34962},
                {"buffer":0,"byteOffset":12,"byteLength":36,"target":34962},
                {"buffer":0,"byteOffset":48,"byteLength":3,"target":34963}
              ],
              "accessors":[
                {"bufferView":0,"componentType":5126,"count":1,"type":"VEC3","min":[0,0,0],"max":[0,0,0]},
                {"bufferView":1,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]},
                {"bufferView":2,"componentType":5121,"count":3,"type":"SCALAR"}
              ],
              "materials":[{"pbrMetallicRoughness":{"metallicFactor":0}}],
              "meshes":[
                {"primitives":[{"attributes":{"POSITION":0},"indices":2,"material":0}]},
                {"primitives":[{"attributes":{"POSITION":1},"material":0}]}
              ],
              "nodes":[{"name":"triangle","mesh":1}],
              "scenes":[{"nodes":[0]}],"scene":0
            }
            """)!.AsObject();
        if (statedStride) root["bufferViews"]![0]!["byteStride"] = 16;

        var document = Read(root, buffer);
        var selected = Assert.Single(document.Roots);
        var primitive = Assert.Single(selected.Mesh!.Primitives);
        Assert.Equal([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], primitive.Positions);
        Assert.Equal([0, 1, 2], primitive.Indices);
        GameZWorld world = new();
        var imported = Assert.Single(WorldGltf.Import(document, "mesh.gltf", 0xFF, new()
        {
            World = world, Token = Token,
            Reference = (_, _) => throw new InvalidOperationException(),
            TextureName = (uri, name, _) => name ?? uri,
        }));
        Assert.Equal("triangle", imported.Name);
        var polygon = Assert.Single(imported.Model!.Polygons);
        Assert.Equal([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], polygon.Vertices.Select(i => imported.Model.Vertices[i]));
    }

    [Theory]
    [InlineData(44, true)]
    [InlineData(43, false)]
    public void MultipleElementsMustFitTheirOccupiedSpan(int viewLength, bool fits)
    {
        // Three 12-byte positions at stride 16 occupy 44 bytes, not 48. The backing buffer always has all
        // 44 bytes; shortening only the view must still refuse access to the last component.
        byte[] buffer = new byte[44];
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(16), 1);
        BinaryPrimitives.WriteSingleLittleEndian(buffer.AsSpan(36), 1);
        JsonObject root = JsonNode.Parse("""
            {
              "asset":{"version":"2.0"},
              "buffers":[{"uri":"mesh.bin","byteLength":44}],
              "bufferViews":[{"buffer":0,"byteLength":44,"byteStride":16,"target":34962}],
              "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3","min":[0,0,0],"max":[1,1,0]}],
              "meshes":[{"primitives":[{"attributes":{"POSITION":0}}]}],
              "nodes":[{"mesh":0}],"scenes":[{"nodes":[0]}],"scene":0
            }
            """)!.AsObject();
        root["bufferViews"]![0]!["byteLength"] = viewLength;
        if (fits)
            Assert.Equal([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], Read(root, buffer).Roots[0].Mesh!.Primitives[0].Positions);
        else
        {
            var error = Assert.Throws<InvalidDataException>(() => Read(root, buffer));
            Assert.Contains("buffer view", error.Message);
            Assert.Contains("44", error.Message);
            Assert.Contains("43", error.Message);
        }
    }

    private static GltfDocument Read(JsonObject root, byte[] buffer) =>
        GltfDocument.Read(Encoding.UTF8.GetBytes(root.ToJsonString()), uri => uri == "mesh.bin" ? buffer : throw new FileNotFoundException(uri), Token);
}
