using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using zStudio.Core.Formats;
using zStudio.Core.Animation;
using zStudio.Core.Gltf;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>Native named model and sampled-motion sources, independent of compiled record identities.</summary>
internal static class Mw3LibrarySources
{
    internal const string MotionFolder = "data/common/mechs/motions";
    internal sealed record Library(byte[] Bytes, int Members, IReadOnlyDictionary<string, string> TextureFiles, IReadOnlyDictionary<string, int> TextureAddressing, IReadOnlyList<string> Warnings);

    internal static IReadOnlyList<WorldSources.Output> Models(AssetDocument document, Func<string, string, string?> texture,
        CancellationToken token)
    {
        var world = GameZWorldReader.FromDocument(document, token);
        List<WorldSources.Output> outputs = [];
        foreach (var asset in document.Assets.Where(a => a.Content is MechAssembly))
        {
            token.ThrowIfCancellationRequested();
            string folder = SourceExtractor.SourceDirectory(asset.Metadata.Text("source_path"))
                ?? throw new InvalidDataException($"{asset.Name}: the mech model has no recorded source folder.");
            string path = $"{folder}/{Path.GetFileNameWithoutExtension(asset.Name)}.gltf";
            SourceProject.RequireSource(path);
            var assembly = (MechAssembly)asset.Content!;
            var root = world.Nodes[assembly.RootNode];
            var doc = WorldGltf.Export(root.Children, WorldGltf.DefaultZone, new()
            {
                Game = GameVariant.MechWarrior3, Canonical = true, Token = token,
                Texture = t => (WorldSources.RelativeUri(folder, texture(folder, t.Name) ?? $"{folder}/../textures/{t.Name}.png"), 0)
            }, root);
            doc.SceneExtras![WorldGltf.Key]!["mechLibraryMember"] = asset.Name;
            var (json, binary) = doc.Write(Path.GetFileNameWithoutExtension(path) + ".bin", token);
            outputs.Add(new(path, json)); outputs.Add(new(path[..^5] + ".bin", binary));
        }
        return outputs;
    }

    internal static byte[] BuildModels(IReadOnlyList<string> inputs, SourceBuilder.Snapshot snapshot, CancellationToken token)
        => CompileModels(inputs, snapshot, token).Bytes;

    internal static Evaluation EvaluateModels(IReadOnlyList<string> inputs, SourceBuilder.Snapshot snapshot, CancellationToken token)
    {
        GameZWorld world = new() { SourceVersion = 27 };
        Dictionary<string, string> textureFiles = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> addressing = new(StringComparer.OrdinalIgnoreCase);
        BoundedDiagnostics warnings = new();
        List<(string Path, string Member, WorldNode Root)> roots = [];
        Dictionary<string, string> memberNames = new(StringComparer.OrdinalIgnoreCase) { ["version"] = "reserved metadata", ["format"] = "reserved metadata", ["materials"] = "reserved metadata" };
        foreach (string input in inputs.Where(Mw3Project.Model).Order(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            byte[] source = snapshot.Read(input, token, ProjectReadLimits.Model());
            var identity = WorldGltf.ReadSourceIdentity(source, token);
            if (identity.InvalidLibraryMember) throw new InvalidDataException($"{input}: mechLibraryMember must be a string.");
            if (identity.LibraryMember is not { } member) continue;
            if (member.Length == 0 || member.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException($"{input}: invalid mech library member name.");
            if (memberNames.TryGetValue(member, out string? previous)) throw new InvalidDataException($"{previous} and {input} both name mech library member {member}; give the source models distinct member names.");
            memberNames.Add(member, input);
            GltfDocument Load(string path) => WorldAssembler.ReadModel(snapshot.Read(path, token, ProjectReadLimits.Model()), path,
                (p, remaining) => snapshot.Read(p, token, ProjectReadLimits.Bytes(remaining)), token);
            var context = new WorldGltf.ImportContext
            {
                World = world, Token = token,
                TextureName = (uri, name, path) =>
                {
                    string engineName = (uri.Length > 0 ? Path.GetFileNameWithoutExtension(uri) : name ?? "").ToLowerInvariant();
                    string texturePath = uri.Length > 0 ? WorldAssembler.Relative(path, uri)
                        : Path.GetDirectoryName(path)!.Replace('\\', '/').Replace("/models", "/textures", StringComparison.OrdinalIgnoreCase) + "/" + engineName + ".png";
                    if (!snapshot.Exists(texturePath)) { warnings.Add($"{path}: texture {engineName} has no PNG source at {texturePath}; its name remains in the model, but the texture library cannot supply that image."); return engineName; }
                    if (textureFiles.TryGetValue(engineName, out string? prior) && !prior.Equals(texturePath, StringComparison.OrdinalIgnoreCase)
                        && SourceProject.Sha256(snapshot.Read(prior, token)) != SourceProject.Sha256(snapshot.Read(texturePath, token)))
                        throw new InvalidDataException($"{path}: {engineName} names different PNG files in one mech library; give them distinct texture names.");
                    textureFiles.TryAdd(engineName, texturePath); return engineName;
                },
                Reference = (uri, from) => { string path = WorldAssembler.Relative(from, uri); return (Load(path), path); }
            };
            var document = Load(input);
            var root = new WorldNode(member, WorldNodeClass.Object3D)
            { Flags = (0x0308001Cu & ~WorldGltf.CarriedFlags) | (WorldGltf.RootFlags(document) ?? WorldGltf.DefaultCarried), BoundsFlags = 4, Zone = WorldGltf.DefaultZone, Mw3Words = [0, 160, 0, 0] };
            root.SetPayloadInt(0, 9); foreach (int at in new[] { 0x24, 0x28, 0x2C, 0x30, 0x40, 0x50 }) root.SetPayloadFloat(at, 1);
            WorldGltf.ApplyMw3LoadRoot(root, document, input);
            if (WorldGltf.EmbeddedLoadRoot(document) is { } rootZone)
            { root.Zone = rootZone.Word; root.Flags = rootZone.Gate ? root.Flags | 0x01000000u : root.Flags & ~0x01000000u; }
            foreach (var node in WorldGltf.Import(document, input, root.Zone & 255, context)) { root.Children.Add(node); node.Parents.Add(root); }
            foreach (var pair in context.TextureAddressing)
                if (!addressing.TryAdd(pair.Key, pair.Value) && addressing[pair.Key] != pair.Value) throw new InvalidDataException($"{input}: one library texture uses conflicting edge modes.");
            roots.Add((input, member, root));
            Add(root);
            void Add(WorldNode node) { world.Nodes.Add(node); foreach (var child in node.Children) Add(child); }
        }
        WorldUpdate.RebuildBounds(world, token);
        if (world.Materials.Any(m => m.Mw3Cycle != null)) throw new InvalidDataException("Mech-library material cycles cannot be serialized by the native library format; remove the cycle or use it in a campaign world model.");
        world.NodeCapacity = Math.Max(1, world.Nodes.Count); world.ModelCapacity = Math.Max(1, world.Models.Count); world.MaterialCapacity = Math.Max(1, world.Materials.Count);
        foreach (var root in roots)
        {
            HashSet<WorldNode> visited = new(ReferenceEqualityComparer.Instance);
            void Validate(WorldNode node, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 256 || !visited.Add(node) || node.Class != WorldNodeClass.Object3D || node.Parents.Count > 1)
                    throw new InvalidDataException($"{root.Path}: mech libraries require a tree of Object3D nodes.");
                foreach (var child in node.Children) Validate(child, depth + 1);
            }
            Validate(root.Root, 0);
        }
        return new(world, roots, textureFiles, addressing, warnings.Snapshot());
    }

    internal sealed record Evaluation(GameZWorld World, IReadOnlyList<(string Path, string Member, WorldNode Root)> Roots,
        IReadOnlyDictionary<string, string> TextureFiles, IReadOnlyDictionary<string, int> TextureAddressing, IReadOnlyList<string> Warnings)
    {
        internal AssetDocument SceneDocument(string path, CancellationToken token)
        {
            var scene = WorldScene.Create(World, token);
            var doc = new AssetDocument(path, FileStamp.Unverified, new(FormatFamily.GameZ, 27, Recognition.Supported, "Evaluated source mech library"), ReadOnlyMemory<byte>.Empty) { Scene = scene };
            var slots = GameZWriter.NodeSlots(World, token);
            int member = 0;
            var modelIndices = World.Models.Select((model, index) => (model, index)).ToDictionary(p => p.model, p => p.index);
            foreach (var root in Roots)
            {
                token.ThrowIfCancellationRequested();
                var nodes = new List<WorldNode>();
                void Visit(WorldNode n) { token.ThrowIfCancellationRequested(); nodes.Add(n); foreach (var child in n.Children) Visit(child); }
                Visit(root.Root);
                int[] models = nodes.Where(n => n.Model != null).Select(n => modelIndices[n.Model!]).Distinct().Order().ToArray();
                doc.Add(AssetKind.Model, member, root.Member, 0, 0, metadata: new() { ["source_path"] = root.Path }, content: new MechAssembly(member, slots[root.Root], nodes.Count, models.FirstOrDefault(), models.Length));
                member++;
            }
            return doc;
        }
    }

    internal static Library CompileModels(IReadOnlyList<string> inputs, SourceBuilder.Snapshot snapshot, CancellationToken token)
    {
        var evaluated = EvaluateModels(inputs, snapshot, token);
        var world = evaluated.World; var roots = evaluated.Roots;
        var textureFiles = evaluated.TextureFiles; var addressing = evaluated.TextureAddressing;
        byte[] bytes = GameZWriter.Write(world, token);
        var decoded = FormatRegistry.Default.OpenBytes("mech-library-world", bytes, token: token);
        var layout = decoded.GameZLayout!; var slots = GameZWriter.NodeSlots(world, token);
        var modelIndices = world.Models.Select((m, i) => (m, i)).ToDictionary(p => p.m, p => p.i);
        var modelAssets = decoded.Assets.Where(a => a.Kind == AssetKind.Model).ToDictionary(a => a.Index);
        List<ArchiveSources.Entry> entries = [new("version", "", BitConverter.GetBytes(27u)), new("format", "", BitConverter.GetBytes(1u))];
        foreach (var (path, identity, root) in roots)
        {
            using MemoryStream member = new(); using BinaryWriter writer = new(member, Encoding.Latin1);
            HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance);
            WriteNode(root, 0);
            entries.Add(new(identity, SourceBuilder.SourceField(path), member.ToArray()));
            void WriteNode(WorldNode node, int depth)
            {
                token.ThrowIfCancellationRequested();
                if (depth > 256 || !seen.Add(node) || node.Class != WorldNodeClass.Object3D || node.Parents.Count > 1)
                    throw new InvalidDataException($"{path}: mech libraries require a tree of Object3D nodes.");
                byte[] header = bytes.AsSpan(layout.NodeOffset + slots[node] * 212, 208).ToArray();
                BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(60), node.Model == null ? 0 : 1);
                writer.Write(header); writer.Write(node.Payload);
                if (node.Model is { } model)
                {
                    int index = modelIndices[model]; writer.Write(bytes.AsSpan(layout.ModelOffset + 12 + index * 96, 92));
                    var asset = modelAssets[index]; writer.Write(bytes.AsSpan((int)asset.Offset, (int)asset.Length));
                }
                foreach (var child in node.Children) WriteNode(child, depth + 1);
            }
        }
        using MemoryStream table = new(); using (BinaryWriter writer = new(table, Encoding.Latin1, leaveOpen: true))
        {
            writer.Write(world.Materials.Count);
            for (int i = 0; i < world.Materials.Count; i++)
            {
                writer.Write(bytes.AsSpan(layout.MaterialOffset + 16 + i * 44, 40));
                if (world.Materials[i].Texture is { } texture) Sized(writer, texture.Name);
            }
        }
        entries.Add(new("materials", "", table.ToArray()));
        return new(ArchiveSources.Write(entries), roots.Count, textureFiles, addressing, evaluated.Warnings);
    }

    internal static byte[] MotionText(MotionClip clip, CancellationToken token)
    {
        StringBuilder text = new();
        text.AppendLine("# zStudio sampled MechWarrior 3 motion; quaternion components are W X Y Z.");
        text.AppendLine($"MOTION ( LOOP_SECONDS ( {F(clip.LoopTime)} ) FRAME_COUNT ( {clip.FrameCount} )");
        foreach (var part in clip.Parts)
        {
            token.ThrowIfCancellationRequested();
            text.Append("  PART ( NAME ( ").Append(Quote(part.Name)).AppendLine(" ) SAMPLES (");
            foreach (var frame in part.Frames)
            {
                token.ThrowIfCancellationRequested();
                text.Append("    ( ").Append(F(frame.Translation.X)).Append(' ').Append(F(frame.Translation.Y)).Append(' ').Append(F(frame.Translation.Z))
                    .Append(' ').Append(F(frame.Rotation.W)).Append(' ').Append(F(frame.Rotation.X)).Append(' ').Append(F(frame.Rotation.Y)).Append(' ').Append(F(frame.Rotation.Z)).AppendLine(" )");
                if (text.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException("The motion source exceeds the text limit.");
            }
            text.AppendLine("  ) )");
        }
        text.AppendLine(")"); return Encoding.Latin1.GetBytes(text.ToString());
    }

    internal static byte[] BuildMotion(byte[] source, CancellationToken token)
    {
        var tree = ZrdText.Parse(source, token); var motion = AnimationItem.Parse(tree, "motion").SingleOrDefault(i => i.Key == "MOTION") ?? throw new InvalidDataException("A sampled motion needs MOTION.");
        float duration = motion.Item("LOOP_SECONDS")?.Number() ?? 0; int frames = (int)(motion.Item("FRAME_COUNT")?.Number() ?? 0);
        var parts = motion.Items.Where(i => i.Key == "PART").ToArray();
        if (!float.IsFinite(duration) || duration <= 0 || frames is < 1 or > 100_000 || parts.Length is < 1 or > 4096 || (long)parts.Length * (frames + 1) > MotionClip.MaximumArchiveSamples)
            throw new InvalidDataException("Invalid sampled-motion duration, frame count or part count.");
        using MemoryStream stream = new(); using BinaryWriter writer = new(stream, Encoding.Latin1);
        writer.Write(4); writer.Write(duration); writer.Write(frames); writer.Write(parts.Length); writer.Write(-1f); writer.Write(1f);
        foreach (var part in parts)
        {
            token.ThrowIfCancellationRequested(); string name = part.TextOf("NAME") ?? throw new InvalidDataException("A motion part needs NAME.");
            Sized(writer, name); writer.Write(12);
            var samples = part.Item("SAMPLES")?.Values?.Children.Where(c => c.Kind == ZrdKind.Array).ToArray() ?? [];
            if (samples.Length != frames + 1 || samples.Any(s => s.Children.Count != 7 || s.Children.Any(c => c.Kind is not (ZrdKind.Int or ZrdKind.Float))))
                throw new InvalidDataException($"{name}: SAMPLES must contain {frames + 1} translation and WXYZ quaternion samples.");
            foreach (var sample in samples) for (int i = 0; i < 3; i++) writer.Write(Value(sample.Children[i]));
            foreach (var sample in samples) for (int i = 3; i < 7; i++) writer.Write(Value(sample.Children[i]));
        }
        byte[] bytes = stream.ToArray(); _ = MotionClip.Read(bytes, token); return bytes;
    }
    private static void Sized(BinaryWriter writer, string text)
    {
        if (text.Length > 4096 || text.Any(c => c == 0 || c > 255)) throw new InvalidDataException("A library name must fit 4096 Latin-1 characters without NUL.");
        byte[] bytes = Encoding.Latin1.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes);
    }
    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    private static string F(float value) { string text = value.ToString("R", CultureInfo.InvariantCulture); return text.IndexOfAny(['.', 'E', 'e']) < 0 ? text + ".0" : text; }
    private static float Value(ZrdNode node) => node.Kind == ZrdKind.Float ? BitConverter.UInt32BitsToSingle(node.Bits) : unchecked((int)node.Bits);
}
