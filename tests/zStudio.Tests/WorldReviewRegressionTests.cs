using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core;
using Recoil.Zbd.Core.Animation;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldReviewRegressionTests
{
    [Fact]
    public void SiFrameIndexKeepsReversedAndRepeatedFramePositions()
    {
        SiScriptWriter.FrameIndex index = new(new[] { 0, 2, 4, 2, 0, 5 });
        Assert.Equal((0, 6), index.Range([0, 2, 0, 5], "a"));
        Assert.Equal((1, 3), index.Range([2, 2], "b"));
        Assert.Equal((2, 3), index.Range([4, 0], "c"));
        Assert.Throws<InvalidDataException>(() => index.Range([5, 0], "missing"));
        CountedFrames frames = new(Enumerable.Range(0, 1000).ToArray());
        SiScriptWriter.FrameIndex sparse = new(frames);
        for (int i = 0; i < 500; i++) Assert.Equal((2 * i, 2), sparse.Range([2 * i, 2 * i + 1], "sparse"));
        Assert.Equal(1000, frames.Reads);
    }
    private sealed class CountedFrames(int[] frames) : IReadOnlyList<int>
    {
        public int Reads;
        public int Count => frames.Length;
        public int this[int index] { get { Reads++; return frames[index]; } }
        public IEnumerator<int> GetEnumerator() { for (int i = 0; i < Count; i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    [Fact]
    public void WritingSparseSiObjectsDoesNotVisitEveryObjectAtEveryFrame()
    {
        var objects = new CountedSiObjects(Enumerable.Range(0, 500).Select(i =>
            ("object" + i, i * 2, new long[]?[] { [1_000_000, 1_000_000, 1_000_000], [0, 0, 0], [i, 0, 0] })).ToArray());
        string text = SiScriptWriter.Text(objects, Enumerable.Range(0, 1000).ToList(), new(null), TestContext.Current.CancellationToken);
        Assert.Equal(500, text.Split("Object: ").Length - 1);
        Assert.Contains("Frame: 999\r\nObject: object499\r\n", text);
        Assert.InRange(objects.Reads, 500, 5000);
    }
    private sealed class CountedSiObjects((string Object, int Start, long[]?[] Values)[] items) : IReadOnlyList<(string Object, int Start, long[]?[] Values)>
    {
        public int Reads;
        public int Count => items.Length;
        public (string Object, int Start, long[]?[] Values) this[int index] { get { Reads++; return items[index]; } }
        public IEnumerator<(string Object, int Start, long[]?[] Values)> GetEnumerator()
        { for (int i = 0; i < items.Length; i++) yield return this[i]; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    [Fact]
    public void DeletingAScriptObjectDoesNotReparseTheWholeScriptForEachInstruction()
    {
        using SourceWorldFixture fixture = new();
        const string path = "gamegen/m1.gs";
        string text = "NewObject3D part\r\n" + string.Concat(Enumerable.Repeat("SetIntersectSurface on # keep this text\r\n", 300));
        fixture.Write(path, text);
        var syntax = GameGenScriptSyntax.Parse(text, TestContext.Current.CancellationToken);
        SourceInstruction Instruction(GameGenScriptLine l) => new(path, l.Number, l.Tokens[0], l.Tokens, l.Tokens.Skip(1).ToArray());
        WorldNode root = new("world", WorldNodeClass.World), node = new("part", WorldNodeClass.Object3D);
        root.Children.Add(node); node.Parents.Add(root);
        GameZWorld world = new(); world.Nodes.AddRange([root, node]);
        WorldNodeProvenance origin = new() { Created = Instruction(syntax.Lines[0]) };
        origin.Applied.AddRange(syntax.Lines.Skip(1).Select(Instruction));
        SourceWorkspace workspace = new(fixture.Project);
        SourceObjectTarget target = new(workspace, "m1", world, node, new Dictionary<WorldNode, WorldNodeProvenance> { [node] = origin }, new Dictionary<(string, int), int>());
        long before = GC.GetAllocatedBytesForCurrentThread();
        var plan = SourceObjectEdits.PlanDelete(target, TestContext.Current.CancellationToken);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(string.Concat(syntax.Lines.Select(l => "# " + text.Substring(l.Start, l.Length) + "\r\n")), Encoding.Latin1.GetString(Assert.Single(plan.Changes).Content));
        Assert.InRange(allocated, 0, 8 << 20);
        Assert.Equal(text, File.ReadAllText(fixture.Path(path)));
    }
    [Fact]
    public void ReconstructedWorldFilesShareOneOutputBudget()
    {
        GameZWorld world = new();
        world.Nodes.Add(new("world", WorldNodeClass.World));
        world.Nodes.Add(new("model", WorldNodeClass.Object3D));
        var script = GameGenScriptText.Tokenize("NewWorld world\nLoadGameGen model.flt model\nGameZWriteZBDFile ..\\m1\\gamez.zbd");
        List<WorldSources.Output> Reconstruct(long maximum) => WorldSources.Reconstruct([new(1, world)],
            n => n.Equals("m1.gs", StringComparison.OrdinalIgnoreCase) ? script : null, (_, _) => null, new HashSet<string>(), _ => 0, [], TestContext.Current.CancellationToken, maximumOutputBytes: maximum);
        var complete = Reconstruct(1 << 20);
        long size = complete.Sum(o => o.Bytes.LongLength);
        Assert.True(size > 0);
        Assert.Contains("memory limit", Assert.Throws<IOException>(() => Reconstruct(size - 1)).Message);
        Assert.Equal(size, Reconstruct(size).Sum(o => o.Bytes.LongLength));
    }
    [Fact]
    public void InferenceReplayBudgetIncludesOriginalRecordsWithoutCaches()
    {
        WorldNode[] records = Enumerable.Range(0, 10).Select(i => new WorldNode("node" + i, WorldNodeClass.Object3D)).ToArray();
        OriginalLoader.MirrorBudget budget = new(2, _ => "cache limit");
        int allocations = 0;
        void Load() => OriginalLoader.Load(new("root", WorldNodeClass.Object3D), records, records, new()
        {
            Allocate = _ => allocations++, Free = _ => { }, Content = _ => [], File = n => n.Name,
            Mirrored = budget.Load(), Token = TestContext.Current.CancellationToken,
        });
        Load();
        Assert.Equal(11, allocations);
        Assert.Contains("work limit", Assert.Throws<InvalidDataException>(Load).Message);
        Assert.Equal(16, allocations);
    }
    [Fact]
    public void TerrainCutsChargeIntermediateGeometryBeforeRetainingIt()
    {
        TerrainSurface surface = new("ground", "ground.gltf", "ground", TerrainAttributes.None);
        TerrainRecipe recipe = new(TerrainRecipe.CurrentCompiler, [surface], TerrainAttributes.None, []);
        TerrainCorner Corner(float x, float z) => new(new(x, 0, z), Vector3.UnitY, Vector2.Zero);
        TerrainSurfaceGeometry geometry = new(surface, [new(0, [Corner(0, 0), Corner(0, -10), Corner(10, -10), Corner(10, 0)])]);
        TerrainGrid grid = new(0, 0, 10, -10, 1, -1, 10, 10);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var error = Assert.Throws<InvalidDataException>(() => TerrainCompiler.Compile("test", recipe, [geometry], [new(0)], grid, TestContext.Current.CancellationToken, 40));
        Assert.Contains("intermediate geometry limit", error.Message);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024 * 1024);
        var complete = TerrainCompiler.Compile("test", recipe, [geometry], [new(0)], grid, TestContext.Current.CancellationToken);
        Assert.Equal(100, complete.Pieces.Count);
        // Preserving the authored fan splits cells along its diagonal, so polygon count need not equal cell count.
        // Every cell must still contain exactly its original square unit of geometry.
        Assert.All(complete.Pieces, piece => Assert.Equal(1.0, piece.Polygons.Sum(p =>
        {
            double twiceArea = 0;
            for (int i = 0; i < p.Corners.Length; i++)
            {
                var a = p.Corners[i].Position; var b = p.Corners[(i + 1) % p.Corners.Length].Position;
                twiceArea += (double)a.X * b.Z - (double)b.X * a.Z;
            }
            return Math.Abs(twiceArea) / 2;
        }), 6));
    }
    [Fact]
    public void InferenceBoundaryRunsMatchTheSlotDefinitionWithoutRepeatedPrefixSets()
    {
        var random = new Random(761);
        int[] slots = Enumerable.Range(0, 2000).OrderBy(_ => random.Next()).ToArray();
        int[] objects = Enumerable.Range(2000, 200).ToArray();
        var expected = Enumerable.Range(1, slots.Length - 2).Select(d =>
        {
            int high = slots.Skip(d).SkipLast(1).Max() + 1;
            var run = objects.Concat(slots.Take(d)).Where(s => s < high).ToHashSet();
            return (d, high, run.Count > 0 && run.Contains(0) && run.Min() == slots[d] - run.Count && run.Max() == slots[d] - 1);
        }).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var actual = DatabaseRecords.BoundaryCandidates(slots, objects, 0, TestContext.Current.CancellationToken).ToArray();
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024 * 1024);
        Assert.Equal(expected, actual);
    }
    [Theory]
    [InlineData("100")]
    [InlineData("allowed, blocked")]
    public void TerrainCraterModesAcceptOnlyTheDocumentedWords(string mode) =>
        Assert.Throws<InvalidDataException>(() => TerrainAttributes.FromJson(new JsonObject { ["craters"] = mode }, "terrain"));
    [Fact]
    public void InferredDeletedGroupsCannotCreateAnUnboundedRecursiveTree()
    {
        WorldNode leaf = new("leaf", WorldNodeClass.Object3D);
        var parts = typeof(DatabaseRecords).GetNestedType("Parts", System.Reflection.BindingFlags.NonPublic)!;
        var ctor = parts.GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Single();
        const int depth = 512;
        var database = new LoadedModel("database", "database", new("m1.gs", "LoadGameGen", [], [], null, []), true, null, [leaf], false);
        object?[] args = [new Dictionary<WorldNode, int> { [leaf] = depth + 1 }, (Func<WorldNode, bool>)(_ => false), database,
            Enumerable.Range(0, depth + 2).ToArray(), Enumerable.Range(0, depth + 1).ToList(), "m1", OriginalLoader.MirrorBudget.PerLoad(), new DatabaseRecords.RetainedMatchBudget(), TestContext.Current.CancellationToken, false, true, null];
        var thrown = Assert.Throws<System.Reflection.TargetInvocationException>(() => ctor.Invoke(args));
        Assert.Contains("nested", Assert.IsType<InvalidDataException>(thrown.InnerException).Message);
    }
    [Fact]
    public void AltitudeComparisonReportsItsWorkLimitWithoutClaimingEqualTerrain()
    {
        WorldModel model = new(); model.Vertices.AddRange([new(0, 0, 0), new(0, 0, 1000), new(1000, 0, 1000), new(1000, 0, 0)]);
        model.Polygons.Add(new() { Vertices = [0, 1, 2, 3] });
        var nodes = Enumerable.Range(0, 20).Select(i => new WorldNode("ground" + i, WorldNodeClass.Object3D) { Model = model }).ToArray();
        long before = GC.GetAllocatedBytesForCurrentThread();
        var limitedIndex = TerrainProbe.Compare(nodes, nodes, 1, TestContext.Current.CancellationToken, null, 100, 1000);
        Assert.False(limitedIndex.Complete); Assert.NotNull(limitedIndex.Limitation); Assert.Equal(0, limitedIndex.Samples);
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 1024 * 1024);
        var limitedWork = TerrainProbe.Compare(nodes, nodes, 100, TestContext.Current.CancellationToken, null, 1000, 20);
        Assert.False(limitedWork.Complete);
        var complete = TerrainProbe.Compare(nodes, nodes, 100, TestContext.Current.CancellationToken);
        Assert.True(complete.Complete); Assert.True(complete.Samples > 0); Assert.Equal(0, complete.Mismatches);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FinitePolygonInputsCannotOverflowTheirStoredUvOrMorphValues(bool morph)
    {
        ModelBuilder builder = new();
        Vector3[] points = [new(-3e38f, 0, 0), Vector3.UnitX, Vector3.UnitY];
        var polygon = new PolygonInput(points, [Vector2.Zero, new(1e30f, 0), Vector2.One], [],
            morph ? [new(3e38f, 0, 0), Vector3.UnitX, Vector3.UnitY] : [], new() { Texture = morph ? null : new("texture") });
        Assert.Throws<InvalidDataException>(() => builder.Add(polygon));
        Assert.Empty(builder.Model.Vertices); Assert.Empty(builder.Model.Polygons); Assert.Empty(builder.Model.Morphs);
        Assert.True(builder.Add(new([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [], [], [], new())));
        Assert.Equal(3, builder.Model.Vertices.Count);
    }
    [Fact]
    public void ReconstructionDoesNotRescanEveryInstructionForEachDetachedNode()
    {
        GameZWorld world = new(); world.Nodes.Add(new("world", WorldNodeClass.World));
        for (int i = 0; i < 200; i++) world.Nodes.Add(new("detached" + i, WorldNodeClass.Object3D));
        CountedTrace trace = new(Enumerable.Repeat(new TracedInstruction("m1.gs", "ignored", ["absent"], [], null, []), 2000).ToArray());
        Assert.Empty(WorldDecomposer.DecomposeAll(world, trace, [], TestContext.Current.CancellationToken).Loads);
        Assert.InRange(trace.Reads, 0, 100_000);
    }
    [Fact]
    public void TraceReadCounterDetectsRepeatedEnumerationAsWellAsIndexing()
    {
        CountedTrace trace = new(Enumerable.Repeat(new TracedInstruction("m1.gs", "ignored", ["absent"], [], null, []), 2000).ToArray());
        // The rejected algorithm scans the whole trace once for each detached node.
        int visited = 0;
        for (int node = 0; node < 200; node++)
            foreach (var instruction in trace)
                if (instruction.Command == "ignored") visited++;
        Assert.Equal(400_000, visited);
        Assert.Equal(400_000, trace.Reads);
        Assert.True(trace.Reads > 100_000);
        _ = trace[0];
        Assert.Equal(400_001, trace.Reads);
        System.Collections.IEnumerator untyped = ((System.Collections.IEnumerable)trace).GetEnumerator();
        Assert.True(untyped.MoveNext());
        Assert.Equal(400_002, trace.Reads);
        (untyped as IDisposable)?.Dispose();
    }
    private sealed class CountedTrace(TracedInstruction[] items) : IReadOnlyList<TracedInstruction>
    {
        internal int Reads;
        public TracedInstruction this[int index] { get { Reads++; return items[index]; } }
        public int Count => items.Length;
        public IEnumerator<TracedInstruction> GetEnumerator()
        {
            for (int i = 0; i < items.Length; i++) yield return this[i];
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    [Fact]
    public void WorldWriterRejectsAggregateGeometryBeforeAllocatingOutput()
    {
        GameZWorld world = new();
        WorldModel model = new(); model.Vertices.AddRange(Enumerable.Repeat(Vector3.Zero, 1024));
        // A modest in-memory fixture represents 12.6 MiB of dense vectors, beyond the reader's aggregate budget.
        for (int i = 0; i < 1025; i++) world.Models.Add(model);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => GameZWriter.Write(world, TestContext.Current.CancellationToken));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 4 * 1024 * 1024);
    }
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private sealed class Project : IProjectFiles, IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "zstudio-review-" + Guid.NewGuid().ToString("N"));
        public Project() { Directory.CreateDirectory(Path.Combine(Root, "data")); Directory.CreateDirectory(Path.Combine(Root, "gamegen")); }
        public void Put(string path, string text) => File.WriteAllText(Path.Combine(Root, path), text);
        public bool Exists(string path) => File.Exists(Path.Combine(Root, path));
        public byte[] Read(string path, CancellationToken token) => Read(path, token, ProjectReadLimits.Document);
        public byte[] Read(string path, CancellationToken token, ProjectReadLimits limits) { token.ThrowIfCancellationRequested(); return Recoil.Zbd.Core.Sources.SourceRead.All(Path.Combine(Root, path), limits, token); }
        public void Dispose() => Directory.Delete(Root, true);
    }

    [Fact]
    public void SharedDescendantsCountEveryExportOccurrenceBeforeAllocatingNodes()
    {
        List<WorldNode> levels = [new("leaf", WorldNodeClass.Object3D)];
        for (int i = 0; i < 18; i++)
        {
            WorldNode a = new("a" + i, WorldNodeClass.Object3D), b = new("b" + i, WorldNodeClass.Object3D);
            foreach (var node in levels) { a.Children.Add(node); b.Children.Add(node); node.Parents.Add(a); node.Parents.Add(b); }
            levels = [a, b];
        }
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => WorldGltf.Export(levels, 255, new() { Texture = _ => throw new InvalidOperationException() }));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 64L * 1024 * 1024);
    }

    [Fact]
    public void CoreCommandPrefixesSelectTheSameNodeInBuildAndTrace()
    {
        using Project p = new();
        p.Put("gamegen/m1.gs", "NewObject3D wanted\nNewObject3D other\nFindNodes wanted\nObject3DTranslateSuffix 10 20 30\nGameZWriteZBDFile world.zbd\n");
        WorldAssembler assembler = new(p, Token);
        var world = assembler.Assemble("m1.gs");
        Assert.Equal(new Vector3(10, 20, 30), ObjectTransform.Of(world.Nodes.Single(n => n.Name == "wanted")).Position);
        Assert.Equal(Vector3.Zero, ObjectTransform.Of(world.Nodes.Single(n => n.Name == "other")).Position);
        Assert.Equal("Object3DTranslate", assembler.Provenance[world.Nodes.Single(n => n.Name == "wanted")].Writers["Object3DTranslate"].Command);
        Assert.Equal("CameraSetHorizonXZ", ScriptCommands.Core("CameraSetHorizonXZextra"));
        Assert.Equal("findNodes", ScriptCommands.Core("findNodes"));
        Assert.Equal("WorldSetFogRangeExtra", ScriptCommands.Core("WorldSetFogRangeExtra"));
    }

    [Fact]
    public void SingularScriptParentRefusesReparentWithoutChangingSources()
    {
        using Project p = new();
        string script = "NewWorld world\nNewObject3D old\nFindNode world\nAddChild old\nNewObject3D parent\nObject3DScale 0 1 1\nFindNode world\nAddChild parent\nNewObject3D thing\nObject3DTranslate 4 5 6\nFindNode old\nAddChild thing\nGameZWriteZBDFile world.zbd\n";
        p.Put("gamegen/m1.gs", script);
        SourceWorkspace workspace = new(p.Root); WorldAssembler assembler = new(p, Token); var world = assembler.Assemble("m1.gs");
        var target = new SourceObjectTarget(workspace, "m1", world, world.Nodes.Single(n => n.Name == "thing"), assembler.Provenance, assembler.Executions) { Write = assembler.WriteInstruction };
        Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanReparent(target, world.Nodes.Single(n => n.Name == "parent"), Token));
        Assert.Empty(workspace.Overlay());
        Assert.Equal(script, Encoding.UTF8.GetString(p.Read("gamegen/m1.gs", Token)));
    }

    [Fact]
    public void TransitiveReferencesAndPrefixCommandsAreSeenInOtherMissions()
    {
        using Project p = new();
        p.Put("data/leaf.gltf", """{"asset":{"version":"2.0"},"nodes":[{"name":"piece"}],"scenes":[{"nodes":[0]}]}""");
        p.Put("data/middle.gltf", """{"asset":{"version":"2.0"},"nodes":[{"name":"middle","extras":{"recoil":{"ref":"leaf.gltf"}}}],"scenes":[{"nodes":[0]}]}""");
        p.Put("data/outer.gltf", """{"asset":{"version":"2.0"},"nodes":[{"name":"outer","extras":{"recoil":{"ref":"middle.gltf"}}}],"scenes":[{"nodes":[0]}]}""");
        p.Put("gamegen/m1.gs", "SetModelDirectory ..\\data\nLoadGameGen leaf.gltf root1\nGameZWriteZBDFile world.zbd\n");
        p.Put("gamegen/m2.gs", "SetModelDirectory ..\\data\nLoadGameGen outer.gltf root2\nFindNodes piece\nObject3DRotateSuffix 0 45 0\nGameZWriteZBDFile world.zbd\n");
        SourceWorkspace workspace = new(p.Root); WorldAssembler assembler = new(p, Token); var world = assembler.Assemble("m1.gs");
        var node = world.Nodes.Single(n => n.Name == "piece");
        var dependent = SourceObjectEdits.TransformElsewhere(workspace, "m1", assembler.Provenance[node], node.Name, Token);
        Assert.NotNull(dependent); Assert.Equal("m2", dependent.Value.Mission);
        var transform = ObjectTransform.Of(node);
        Assert.Throws<InvalidDataException>(() => SourceObjectEdits.PlanTransform(workspace, node.Name, assembler.Provenance[node], assembler.Executions, transform with { Position = Vector3.UnitX }, mission: "m1", current: transform, world: world, write: assembler.WriteInstruction, token: Token));
        Assert.Empty(workspace.Overlay());
    }

    [Fact]
    public void FailedZoneReparentLeavesJsonIntact()
    {
        var root = JsonNode.Parse("""{"asset":{"version":"2.0"},"nodes":[{"name":"moving"},{"name":"parent","extras":{"recoil":{"zone":2}}}],"scenes":[{"nodes":[0,1]}]}""")!.AsObject();
        string before = root.ToJsonString();
        Assert.Throws<InvalidDataException>(() => GltfNodeEdits.Reparent(root, 0, 1, token: Token));
        Assert.Equal(before, root.ToJsonString());
        GltfNodeEdits.Reparent(root, 0, 1, 255, Token);
        Assert.Equal(255, root["nodes"]![0]!["extras"]!["recoil"]!["zone"]!.GetValue<int>());
    }

    [Fact]
    public void JunctionRepairStillFitsEveryTerrainPieceWithoutDroppingTriangles()
    {
        TerrainSurface a = new("a", "surface.gltf", "a", TerrainAttributes.None), b = new("b", "surface.gltf", "b", TerrainAttributes.None);
        static TerrainCorner Corner(float x, float z) => new(new(x, 0, z), Vector3.UnitY, Vector2.Zero);
        static TerrainFace Rect(float lo, float hi) => new(0, [Corner(lo, 0), Corner(lo, 1000), Corner(hi, 1000), Corner(hi, 0)]);
        var polygons = Enumerable.Range(0, 500).Select(i => new TerrainOutline([new(0, i * 2 + .25f), new(1, i * 2 + .25f), new(1, i * 2 + .75f), new(0, i * 2 + .75f)], [])).ToArray();
        TerrainRecipe recipe = new(1, [a, b], TerrainAttributes.None, [new("stripes", ["b"], new(polygons), new() { Soil = 1 })]);
        var compiled = TerrainCompiler.Compile("test", recipe, [new(a, [Rect(-1, 0)]), new(b, [Rect(0, 1)])], [new(WorldGltf.DefaultPolygonZone)], new(-5, 1500, 10, -2000, 10, -2000, 1, 1), Token);
        double area = 0;
        Assert.True(compiled.Pieces.Count(p => p.Surface == 0) > 1);
        foreach (var piece in compiled.Pieces.Where(p => p.Surface == 0))
        {
            ModelBuilder builder = new(); WorldMaterial material = new();
            foreach (var polygon in piece.Polygons)
            {
                var positions = polygon.Corners.Select(c => c.Position).ToArray();
                builder.Add(new(positions, [], [], [], material));
                for (int i = 1; i + 1 < positions.Length; i++) area += Vector3.Cross(positions[i] - positions[0], positions[i + 1] - positions[0]).Length() / 2;
            }
            Assert.DoesNotContain(builder.Warnings, w => w.Contains("discarded"));
            Assert.InRange(builder.Model.Vertices.Count, 1, TerrainCompiler.VertexBudget);
        }
        Assert.Equal(1000, area, 3);
    }

    [Fact]
    public void ComparisonReportsPolygonOrderThatChangesFirstGroundHit()
    {
        static GameZWorld Make(float[] heights)
        {
            ModelBuilder builder = new(); WorldMaterial material = new();
            foreach (float h in heights) builder.Add(new([new(0,h,0),new(0,h,10),new(10,h,10),new(10,h,0)], [], [], [], material));
            WorldNode root = new("world", WorldNodeClass.World), ground = new("ground", WorldNodeClass.Object3D) { Model = builder.Model, Flags = WorldGltf.DefaultCarried | 8 };
            ground.SetPayloadInt(0, 0x28); ground.Parents.Add(root); root.Children.Add(ground);
            GameZWorld world = new(); world.Nodes.AddRange([root, ground]); world.Models.Add(builder.Model); world.Materials.Add(material); return world;
        }
        var before = Make([0, 10]); var after = Make([10, 0]);
        Assert.Equal(0, Assert.Single(TerrainProbe.At([before.Nodes[1]], 5, 5, token: TestContext.Current.CancellationToken)).Height);
        Assert.Equal(10, Assert.Single(TerrainProbe.At([after.Nodes[1]], 5, 5, token: TestContext.Current.CancellationToken)).Height);
        Assert.True(WorldComparer.CompareTree(before, after, token: Token).DifferenceCount > 0);
    }

    [Fact]
    public void DuplicateConnectorAttributesKeepTheirFirstValues()
    {
        using Project p = new();
        p.Put("data/anim.zad", "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( beam ) SEQUENCE_DEFINITION ( NAME ( go ) OBJECT_CONNECTOR ( NAME ( beam ) FROM_POS ( 1.0 2.0 3.0 ) FROM_POS ( 9.0 8.0 7.0 ) RUN_TIME ( 1.0 ) RUN_TIME ( 5.0 ) ) ) ) ) ) )");
        var compiled = AnimationCompiler.Compile(p, "data/anim.zad", ["beam"], token: Token);
        var e = compiled.Package.Entries[1].Sequences[0].Events[0];
        Assert.Equal(new Vector3(1,2,3), e.Vector(24)); Assert.Equal(1, e.F32(80));
        p.Put("data/anim.zad", "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( beam ) SEQUENCE_DEFINITION ( NAME ( go ) CALL_OBJECT_CONNECTOR ( NAME ( child ) FROM_POS ( 1.0 2.0 3.0 ) FROM_POS ( 9.0 8.0 7.0 ) ) ) ) ) ) )");
        var call = AnimationCompiler.Compile(p, "data/anim.zad", ["beam"], token: Token).Package.Entries[1].Sequences[0].Events[0];
        Assert.Equal(new Vector3(1,2,3), call.Vector(56));
    }

    [Fact]
    public void AnimationSourceBudgetsIncludeRepeatedDefinitionsAndKeyframeScripts()
    {
        using Project p = new();
        const string definition = "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION ( NAME ( thing ) ) ) ) )";
        const string root = "( ANIMATION_DEFINITIONS ( ANIMATION_LIST ( ANIMATION_DEFINITION_FILE ( leaf.zad ) ANIMATION_DEFINITION_FILE ( leaf.zad ) ) ) )";
        p.Put("data/anim.zad", root); p.Put("data/leaf.zad", definition);
        // Fits root + one leaf, so the repeated include must consume another share before parsing it.
        long bytes = root.Length + definition.Length;
        Assert.Contains("together exceed", Assert.Throws<InvalidDataException>(() => AnimationDefinitionSet.Load(p, "data/anim.zad", bytes, 1000, Token)).Message);
        Assert.Contains("syntax nodes", Assert.Throws<InvalidDataException>(() => AnimationDefinitionSet.Load(p, "data/anim.zad", 10000, 15, Token)).Message);
        var set = AnimationDefinitionSet.Load(p, "data/anim.zad", bytes + definition.Length + 3, 1000, Token);
        p.Put("data/motion.zan", "FRAME 0\nFRAME 1\n");
        Assert.Contains("together exceed", Assert.Throws<InvalidDataException>(() => set.ReadScript("motion.zan", "data/anim.zad")).Message);
        Assert.Equal(2, AnimationDefinitionSet.Load(p, "data/anim.zad", token: Token).Definitions.Count);
    }

    [Fact]
    public void AssemblerChargesEveryDistinctScriptBeforeDecodingIt()
    {
        using Project p = new();
        const string main = "source child.gw\nsource child.gw\nNewWorld world\nGameGenSetWorld\nGameZWriteZBDFile out.zbd\n";
        const string child = "# retained source\nQuit\n";
        p.Put("gamegen/m1.gs", main); p.Put("gamegen/child.gw", child);
        long exact = main.Length + child.Length;
        Assert.Contains("script sources together", Assert.Throws<InvalidDataException>(() =>
            new WorldAssembler(p, Token) { ScriptSourceByteLimit = exact - 1 }.Assemble("m1.gs")).Message);
        // Repeated sourcing is charged once, while its instructions still execute each time.
        Assert.NotEmpty(new WorldAssembler(p, Token) { ScriptSourceByteLimit = exact }.Assemble("m1.gs").Nodes);
    }

    [Fact]
    public void ExportWorldCacheSharesOneBudgetAcrossMissionsAndKeepsEarlierResults()
    {
        using SourceWorldFixture fixture = new();
        fixture.Write("gamegen/m2.gs", File.ReadAllText(fixture.Path("gamegen/m1.gs")));
        var measure = new SourceBuilder.Snapshot(fixture.Project);
        measure.World("m1", Token);
        long one = measure.RetainedBytes;
        Assert.True(one > 0);
        var bounded = new SourceBuilder.Snapshot(fixture.Project, maximumRetainedBytes: one + 1);
        var first = bounded.World("m1", Token);
        Assert.Contains("fewer missions", Assert.Throws<InvalidDataException>(() => bounded.World("m2", Token)).Message);
        Assert.Same(first, bounded.World("m1", Token));
        Assert.Equal(one, bounded.RetainedBytes);
        // Animation packages use the same remaining allowance, rather than a second independent limit.
        Assert.Throws<InvalidDataException>(() => bounded.Retain(2));
        Assert.Equal(one, bounded.RetainedBytes);
    }

    [Fact]
    public void PreparedScriptSourcesShareBytesAndDecodedRecordBudgets()
    {
        using Project p = new();
        var source = Encoding.Latin1.GetBytes("FindNode a\nFindNode b\n");
        var files = new Dictionary<string, byte[]> { ["gamegen/a.gs"] = source, ["gamegen/b.gs"] = source };
        var snapshot = new SourceBuilder.Snapshot(p.Root, files);
        SourceOutputPlan plan = new("interp.zbd", "scripts", files.Keys.ToArray());
        Assert.Throws<InvalidDataException>(() => SourceBuilder.BuildScripts(p.Root, plan, snapshot, Token, source.Length * 2 - 1));
        Assert.Throws<InvalidDataException>(() => SourceBuilder.BuildScripts(p.Root, plan, snapshot, Token, 1000, 3));
        Assert.Throws<InvalidDataException>(() => SourceBuilder.BuildScripts(p.Root, plan, snapshot, Token, 1000, 10, 7));
        Assert.Equal(2, SourceBuilder.BuildScripts(p.Root, plan, snapshot, Token, source.Length * 2, 4, 8).Items);
    }

    [Fact]
    public void SnapshotAppliesTheCallersSmallerBoundBeforeReadingOrCopying()
    {
        using Project p = new();
        p.Put("data/oversized.json", new string(' ', 1024 * 1024));
        var snapshot = new SourceBuilder.Snapshot(p.Root);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<InvalidDataException>(() => snapshot.Read("data/oversized.json", Token, 65536));
        Assert.InRange(GC.GetAllocatedBytesForCurrentThread() - before, 0, 65536);
    }

    [Fact]
    public void SoundBankStopsReadingBeforeAggregatePayloadsExceedDocumentLimit()
    {
        using Project p = new();
        // Alias one modest allocation to many input names: the guard must count each output member's bytes.
        byte[] wav = new byte[8 * 1024 * 1024];
        using (var stream = new MemoryStream(wav)) using (BinaryWriter writer = new(stream))
        {
            writer.Write("RIFF"u8); writer.Write(wav.Length - 8); writer.Write("WAVEfmt "u8); writer.Write(16);
            writer.Write((ushort)1); writer.Write((ushort)1); writer.Write(8000); writer.Write(8000);
            writer.Write((ushort)1); writer.Write((ushort)8); writer.Write("data"u8); writer.Write(wav.Length - 44);
        }
        var files = Enumerable.Range(0, 80).ToDictionary(i => $"data/common/sounds/s{i:D3}.wav", _ => wav);
        const string last = "data/common/sounds/last.wav";
        files.Add(last, "invalid!"u8.ToArray());
        SourceBuilder.Snapshot snapshot = new(p.Root, files);
        Assert.Throws<InvalidDataException>(() => SourceBuilder.Build(p.Root, new("soundsh.zbd", "sounds", files.Keys.ToArray()), snapshot, Token));
        Assert.DoesNotContain(last, snapshot.Dependencies());
    }

    [Fact]
    public void SparseSiTracksRetainOnlyTheirAuthoredSpan()
    {
        static long Write(int count)
        {
            List<SiScriptWriter.Track> tracks = [];
            for (int i = 0; i < count; i++)
            {
                string text = $"FRAME {2*i} POSITION 0 0 0 ROTATION 1 0 0 0 SCALE 1 1 1\nFRAME {2*i+1} POSITION 1 0 0 ROTATION 1 0 0 0 SCALE 1 1 1\nFRAME {2*i+2}";
                var frames = AnimationScript.Compile(AnimationScript.Track(AnimationScript.Parse(Encoding.ASCII.GetBytes(text), "growth.zan", TestContext.Current.CancellationToken), "a")!, 1, "growth.zan", TestContext.Current.CancellationToken);
                tracks.Add(new("a" + i, frames, 1));
            }
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.NotNull(SiScriptWriter.Write(tracks, new(null), Token));
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Write(2);
        long small = Write(100), large = Write(1600);
        Assert.True(large < small * 19, $"Sparse tracks allocated {small} then {large} bytes.");
    }
}
