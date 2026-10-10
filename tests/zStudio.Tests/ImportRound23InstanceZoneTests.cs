using System.Collections;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class ImportRound23InstanceZoneTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void DeletingFirstCopiesPreservesAllZonesWithOneLookupPerInstanceAndBoundedNotes()
    {
        const int count = 2000;
        JsonArray nodes = [], first = [], second = [];
        nodes.Add(new JsonObject { ["children"] = first, ["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["zone"] = 1 } } });
        nodes.Add(new JsonObject { ["children"] = second, ["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["zone"] = 2 } } });
        for (int i = 0; i < count; i++)
        {
            first.Add(nodes.Count); nodes.Add(Instance(i + 1));
            second.Add(nodes.Count); nodes.Add(Instance(i + 1));
        }
        JsonObject root = new() { ["nodes"] = nodes, ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0, 1) }) };
        var before = new CountedZones(GltfNodeEdits.InstanceZones(root, Token).ToDictionary(z => z.Mark, z => z.Zone));
        GltfNodeEdits.Remove(root, new[] { 0 }, Token);
        List<string> notes = [];
        SourceObjectEdits.KeepInstanceZones(root, new string('x', 10000) + ".gltf", before, new Dictionary<long, long>(), null, notes, Token);
        Assert.Equal(count, before.Reads);
        Assert.All(GltfNodeEdits.InstanceZones(root, Token), z => Assert.Equal(1u, z.Zone));
        Assert.Equal(33, notes.Count);
        Assert.All(notes, n => Assert.True(n.Length < 600));
        Assert.Contains((count - 32).ToString("N0"), notes[^1]);
    }

    [Fact]
    public void RepairingOuterZoneChangesInheritanceBeforeTheInnerInstanceIsVisited()
    {
        JsonArray nodes = [Instance(10, 1), Instance(20), Instance(10, 3), Instance(20)];
        ((JsonObject)nodes[0]!)["extras"]![WorldGltf.Key]!["zone"] = 2;
        ((JsonObject)nodes[2]!)["extras"]![WorldGltf.Key]!["zone"] = 2;
        JsonObject root = new() { ["nodes"] = nodes, ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0, 2) }) };
        List<string> notes = [];
        SourceObjectEdits.KeepInstanceZones(root, "nested.gltf", new Dictionary<long, uint?> { [10] = 1, [20] = 1 }, new Dictionary<long, long>(), null, notes, Token);
        Assert.Single(notes);
        Assert.All(GltfNodeEdits.InstanceZones(root, Token), z => Assert.Equal(1u, z.Zone));
        Assert.Equal(1, nodes[2]!["extras"]![WorldGltf.Key]!["zone"]!.GetValue<int>());
        Assert.Null(nodes[1]!["extras"]![WorldGltf.Key]!["zone"]);
        using CancellationTokenSource canceled = new(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => GltfNodeEdits.InstanceZones(root, canceled.Token));
    }

    private static JsonObject Instance(int mark, int? child = null)
    {
        JsonObject node = new() { ["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["instance"] = mark } } };
        if (child is int index) node["children"] = new JsonArray(index);
        return node;
    }

    private sealed class CountedZones(IReadOnlyDictionary<long, uint?> values) : IReadOnlyDictionary<long, uint?>
    {
        public int Reads;
        public uint? this[long key] => values[key];
        public IEnumerable<long> Keys => values.Keys;
        public IEnumerable<uint?> Values => values.Values;
        public int Count => values.Count;
        public bool ContainsKey(long key) => values.ContainsKey(key);
        public bool TryGetValue(long key, out uint? value) { Reads++; return values.TryGetValue(key, out value); }
        public IEnumerator<KeyValuePair<long, uint?>> GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
