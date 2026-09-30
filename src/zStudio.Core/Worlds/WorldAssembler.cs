using System.Globalization;
using System.Numerics;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>Reads project files for the assembler; a build uses its frozen snapshot, the editor reads the disk.</summary>
public interface IProjectFiles
{
    bool Exists(string relative);
    byte[] Read(string relative, CancellationToken token);
}

/// <summary>
/// Assembles a mission world from a source project the way the original gamegen build did: it runs the mission's
/// scripts (<c>gamegen/mN.gs</c>) with the retail interpreter's command semantics and loads models with
/// <c>LoadGameGen</c>, reading glTF where the scripts name OpenFlight files. The world is captured where the script
/// writes it (<c>GameZWriteZBDFile</c>), after the engine's update pass.
/// </summary>
public sealed partial class WorldAssembler(IProjectFiles files, CancellationToken token = default)
{
    public const int MaximumScriptDepth = 32, MaximumInstructions = 1_000_000;
    public GameZWorld World { get; } = new();
    public List<string> Warnings => [.. warnings];
    private readonly List<string> warnings = []; private readonly HashSet<string> seenWarnings = new(StringComparer.Ordinal);
    private void Warn(string message) { if (seenWarnings.Add(message)) warnings.Add(message); }
    /// <summary>Texture files the loaded models referenced, by texture name (project-relative paths).</summary>
    public Dictionary<string, string> TextureFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Clamp words (1 clamps U, 2 clamps V) of the textures the models sample, from their glTF samplers.</summary>
    public Dictionary<string, int> TextureAddressing { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Texture names scripts registered (LensFlareTexture, CycleTextureSetMap, WriteTextureSetMap, TextureAdd).</summary>
    public HashSet<string> ScriptTextures { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Model sources the world was built from, for export inputs.</summary>
    public HashSet<string> ModelFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> ScriptFiles { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The root node of each <c>LoadGameGen</c> before the world was written, in script order.</summary>
    public List<WorldNode> LoadedRoots { get; } = [];
    public string? WorldFile { get; private set; }
    public string? AnimationFile { get; private set; }
    /// <summary>World children in the order they were added; their cells are assigned after the update pass.</summary>
    private readonly List<WorldNode> worldChildren = [];
    private readonly Dictionary<string, string> variables = new(StringComparer.Ordinal);
    private readonly List<string> modelDirectories = [], textureDirectories = [], readerDirectories = [];
    private readonly ScriptConditions conditions = new();
    private WorldNode? current, pendingWorld;
    private bool written;
    private int instructions;

    /// <summary>Runs <paramref name="script"/> (relative to the gamegen folder, e.g. <c>m1.gs</c>) and returns the written world.</summary>
    public GameZWorld Assemble(string script)
    {
        Source(script, 0);
        if (!written) throw new InvalidDataException($"{script} never writes a world (GameZWriteZBDFile).");
        return World;
    }

    private void Source(string script, int depth)
    {
        if (depth > MaximumScriptDepth) throw new InvalidDataException($"Scripts source each other more than {MaximumScriptDepth} levels deep.");
        string relative = $"{SourceProject.GameGenFolder}/{script.Replace('\\', '/')}";
        if (!files.Exists(relative)) { Warn($"Script {script} does not exist."); return; }
        ScriptFiles.Add(relative);
        var lines = GameGenScriptText.Tokenize(GameGenScriptText.Decode(files.Read(relative, token)));
        foreach (var raw in lines)
        {
            token.ThrowIfCancellationRequested();
            if (++instructions > MaximumInstructions) throw new InvalidDataException("The scripts run too many instructions.");
            string command = raw[0];
            // Conditions follow the retail interpreter (see ScriptConditions): TRUE-valued macros, no nesting while skipping.
            if (!conditions.Runs(raw, variables)) continue;
            // Macros expand in arguments only; an unknown macro expands to nothing.
            string[] args = raw.Skip(1).Select(Expand).ToArray();
            if (ScriptConditions.IsQuit(command)) return;
            // Macros are set before and after the world is written (tex_fx scripts may use them).
            if (ScriptConditions.IsSet(command)) { if (args.Length > 0) variables[args[0]] = args.Length > 1 ? args[1] : ""; continue; }
            if (ScriptConditions.IsSource(command)) { if (args.Length > 0) Source(args[0], depth + 1); continue; }
            if (written) { Late(command, args); continue; }
            Run(command, args, relative);
        }
    }

    private string Expand(string token) => ScriptConditions.Expand(token, variables);

    /// <summary>After the world is written, scripts (tex_fx) only register textures the mission pack must hold.</summary>
    private void Late(string command, string[] args)
    {
        if (command is "CycleTextureSetMap" or "WriteTextureSetMap" or "TextureAdd" or "LensFlareTexture" && args.Length > 0) ScriptTextures.Add(TextureStem(args[^1]));
    }

    private void Run(string command, string[] args, string script)
    {
        float F(int i) => i < args.Length ? Number(args[i]) : 0;
        // ParseBoolToken (retail 0x4C19C0): only "on" and "true" (any case) are on; a missing argument is off.
        bool On(int i) => i < args.Length && (args[i].Equals("on", StringComparison.OrdinalIgnoreCase) || args[i].Equals("true", StringComparison.OrdinalIgnoreCase));
        string A(int i) => i < args.Length ? args[i] : "";
        switch (command)
        {
            case "SetModelDirectory": AddDirectories(modelDirectories, A(0)); break;
            case "SetTextureDirectory": AddDirectories(textureDirectories, A(0)); break;
            case "RdrSetPath": AddDirectories(readerDirectories, A(0)); break;
            case "AnimSetZBDFile": AnimationFile = A(0); break;
            case "SetGameZNodeArraySize": World.NodeCapacity = Math.Clamp((int)F(0), 16, GameZWorld.MaximumNodeCapacity); break;
            case "SetModel3DArraySize": World.ModelCapacity = Math.Clamp((int)F(0), 16, GameZWorld.MaximumNodeCapacity); break;
            case "SetMaterialArraySize": World.MaterialCapacity = Math.Clamp((int)F(0), 16, 32767); break;
            case "LensFlareTexture" or "CycleTextureSetMap" or "WriteTextureSetMap" or "TextureAdd":
                if (args.Length > 0) { string name = TextureStem(args[^1]); ScriptTextures.Add(name); WorldGltf.Texture(World, name); }
                break;

            case "NewWorld":
                {
                    WorldNode world = Create(A(0), WorldNodeClass.World);
                    world.SetPayloadFloat(0x84, 1); world.SetPayloadFloat(0x88, 1); world.SetPayloadFloat(0x8C, 1);
                    // gwWorldNew marks fog pending; applying it in the update pass clears the flag again.
                    world.SetPayloadInt(0x4C, 16);
                    break;
                }
            case "GameGenSetWorld": if (current?.Class == WorldNodeClass.World) pendingWorld = current; break;
            case "WorldOrigin": WorldSet(w => { w.SetPayloadFloat(0x34, F(0)); w.SetPayloadFloat(0x38, F(1)); UpdateMax(w); }); break;
            case "WorldExtents": WorldSet(w => { w.SetPayloadFloat(0x3C, F(0)); w.SetPayloadFloat(0x40, F(1)); UpdateMax(w); }); break;
            case "WorldPartition": WorldSet(w => WorldUpdate.SetPartition(w, F(0), F(1))); break;
            case "WorldPartitionInclusionTolerance": WorldSet(w => { w.SetPayloadFloat(0x70, F(0)); w.SetPayloadFloat(0x74, F(1)); }); break;
            case "WorldPartitionMaxDECFeatureCount": WorldSet(w => w.SetPayloadInt(0x4C, Math.Clamp((int)F(0), 0, 255))); break;
            case "WorldSetFogState": WorldSet(w => w.SetPayloadInt(0x10, A(0).ToLowerInvariant() switch { "linear" => 1, "exponential" => 2, _ => 0 })); break;
            case "WorldSetFogColor": WorldSet(w => { w.SetPayloadFloat(0x14, F(0)); w.SetPayloadFloat(0x18, F(1)); w.SetPayloadFloat(0x1C, F(2)); }); break;
            case "WorldSetFogRange": WorldSet(w => { w.SetPayloadFloat(0x20, F(0)); w.SetPayloadFloat(0x24, F(1)); }); break;
            // WorldSetFogAltitude low high stores high first (fogHeightHigh at +0x28, low at +0x2C).
            case "WorldSetFogAltitude": WorldSet(w => { w.SetPayloadFloat(0x28, F(1)); w.SetPayloadFloat(0x2C, F(0)); }); break;
            case "WorldSetFogDensity": WorldSet(w => w.SetPayloadFloat(0x30, F(0))); break;
            case "WorldAddLight":
                if (current?.Class == WorldNodeClass.World && Find(A(0), WorldNodeClass.Light) is { } light)
                { current.WorldLights.Add(light); light.AttachedWorlds.Add(current); }
                break;

            case "NewWindow":
                { var w = Create(A(0), WorldNodeClass.Window); w.SetPayloadInt(8, 1); w.SetPayloadInt(12, 1); w.SetPayloadInt(0xE4, -1); break; }
            case "WindowOrigin": if (current?.Class == WorldNodeClass.Window) { current.SetPayloadInt(0, (int)F(0)); current.SetPayloadInt(4, (int)F(1)); } break;
            case "WindowResolution": if (current?.Class == WorldNodeClass.Window) { current.SetPayloadInt(8, (int)F(0)); current.SetPayloadInt(12, (int)F(1)); } break;
            case "NewDisplay":
                { var d = Create(A(0), WorldNodeClass.Display); d.SetPayloadInt(8, 1); d.SetPayloadInt(12, 1); d.SetPayloadFloat(16, 0.392f); d.SetPayloadFloat(20, 0.392f); d.SetPayloadFloat(24, 1); break; }
            case "DisplayOrigin": if (current?.Class == WorldNodeClass.Display) { current.SetPayloadInt(0, (int)F(0)); current.SetPayloadInt(4, (int)F(1)); } break;
            case "DisplayResolution": if (current?.Class == WorldNodeClass.Display) { current.SetPayloadInt(8, (int)F(0)); current.SetPayloadInt(12, (int)F(1)); } break;
            case "DisplaySetClearColor": if (current?.Class == WorldNodeClass.Display) { current.SetPayloadFloat(16, F(0)); current.SetPayloadFloat(20, F(1)); current.SetPayloadFloat(24, F(2)); } break;
            case "NewCamera": NewCamera(A(0)); break;
            case "CameraSetActive": break;
            case "CameraSetWorld": if (current?.Class == WorldNodeClass.Camera) current.CameraWorld = Find(A(0), WorldNodeClass.World); break;
            case "CameraSetWindow": if (current?.Class == WorldNodeClass.Camera) current.CameraWindow = Find(A(0), WorldNodeClass.Window); break;
            case "CameraSetHorizon": if (current?.Class == WorldNodeClass.Camera) current.CameraHorizon = Find(A(0), null); break;
            case "CameraSetLODMultiplier": if (current?.Class == WorldNodeClass.Camera) { float m = F(0); current.SetPayloadFloat(208, m); current.SetPayloadFloat(212, m == 0 ? 0 : 1 / (m * m)); } break;
            case "CameraSetNearFarClip": if (current?.Class == WorldNodeClass.Camera) { current.SetPayloadFloat(176, F(0)); current.SetPayloadFloat(180, F(1)); current.SetPayloadInt(248, 1); } break;
            case "CameraSetFOV": if (current?.Class == WorldNodeClass.Camera) CameraFov(current, F(0), F(1)); break;

            case "LightNew": NewLight(A(0)); break;
            case "LightSetColor": if (current?.Class == WorldNodeClass.Light) { current.SetPayloadFloat(172, F(0)); current.SetPayloadFloat(176, F(1)); current.SetPayloadFloat(180, F(2)); } break;
            case "LightSetDiffuse": if (current?.Class == WorldNodeClass.Light) current.SetPayloadFloat(164, F(0)); break;
            case "LightSetAmbient": if (current?.Class == WorldNodeClass.Light) current.SetPayloadFloat(168, F(0)); break;
            case "LightSetRanges":
                if (current?.Class == WorldNodeClass.Light)
                {
                    float near = Math.Min(F(0), F(1)), far = Math.Max(F(0), F(1));
                    current.SetPayloadFloat(204, near); current.SetPayloadFloat(208, far); current.SetPayloadFloat(212, far * far); current.SetPayloadFloat(216, far > near ? 1 / (far - near) : 0);
                }
                break;
            case "LightSetOrientation": if (current?.Class == WorldNodeClass.Light) { current.SetPayloadFloat(8, Radians(F(0))); current.SetPayloadFloat(12, Radians(F(1))); current.SetPayloadFloat(16, Radians(F(2))); current.SetPayloadInt(0, 1); } break;
            case "LightSetTranslate": if (current?.Class == WorldNodeClass.Light) { current.SetPayloadFloat(20, F(0)); current.SetPayloadFloat(24, F(1)); current.SetPayloadFloat(28, F(2)); current.SetPayloadInt(0, 1); } break;
            case "LightSetDirectedSource": LightMode(188); break;
            case "LightSetPointSource": LightMode(192); break;
            case "LightSetDirectional": LightMode(184); break;
            case "LightSetSaturated": if (current?.Class == WorldNodeClass.Light) current.SetPayloadInt(196, On(0) ? 1 : 0); break;
            case "LightSetActive": if (current?.Class == WorldNodeClass.Light) current.SetPayloadInt(4, On(0) ? 1 : 0); break;

            case "FindNode": current = Find(A(0), null); if (current == null) Warn($"{script}: FindNode {A(0)} found no node."); break;
            case "FindSubNode": current = current == null ? null : FindSub(current, A(0)); if (current == null) Warn($"{script}: FindSubNode {A(0)} found no node."); break;
            case "NodeSetDescription": if (current != null) current.Name = A(0); break;
            case "AddChild":
                if (current != null && Find(A(0), null) is { } child) AddChild(current, child);
                else Warn($"{script}: AddChild {A(0)} has no node or parent.");
                break;
            case "DeleteChild":
                if (current != null && FindSub(current, A(0)) is { } removed && removed != current) Unlink(current, removed);
                break;
            case "DeleteTree": if (Find(A(0), null) is { } tree) DeleteTree(tree); break;
            case "NewObject3D": Object3D(Create(A(0), WorldNodeClass.Object3D)); break;
            case "Object3DTranslate": Trs(o => { o.SetPayloadFloat(0x54, F(0)); o.SetPayloadFloat(0x58, F(1)); o.SetPayloadFloat(0x5C, F(2)); }); break;
            case "Object3DRotate": Trs(o => { o.SetPayloadFloat(0x18, Radians(F(0))); o.SetPayloadFloat(0x1C, Radians(F(1))); o.SetPayloadFloat(0x20, Radians(F(2))); }); break;
            case "Object3DScale": Trs(o => { o.SetPayloadFloat(0x24, F(0)); o.SetPayloadFloat(0x28, F(1)); o.SetPayloadFloat(0x2C, F(2)); }); break;
            case "SetAltitudeSurface": Flag(0x08, On(0)); break;
            case "SetIntersectSurface": Flag(0x10, On(0)); break;
            case "SetIntersectBBOX": Flag(0x20, On(0)); break;
            case "SetProximity": Flag(0x40, On(0)); break;
            case "SetLandmark": Flag(0x80, On(0)); break;
            case "NodeSetCanModify": Flag(0x10000, On(0)); break;
            case "NodeSetOverwrite": Flag(0x800000, On(0)); break;
            // CZNode::AssignInt32ToDiRecursive: the models of the node and everything below it.
            case "NodeSetLighting": if (current != null) foreach (var model in Subtree(current).Select(n => n.Model).OfType<WorldModel>()) model.Flags = On(0) ? model.Flags | 1 : model.Flags & ~1u; break;

            case "LoadGameGen": LoadGameGen(A(0), A(1), script); break;
            case "GameZWriteZBDFile": WorldFile = A(0); Finish(); break;
            // Rendering and runtime settings are not part of the world file; gamegen-only commands had no retail effect.
            // Commands that change nodes or models in the retail interpreter but are not built here are reported.
            default: if (Unsupported.Contains(command)) Warn($"{script}: {command} changes the world in the game's interpreter, but the source build does not apply it."); break;
        }

        void WorldSet(Action<WorldNode> action) { if (current?.Class == WorldNodeClass.World) action(current); else Warn($"{script}: {command} needs a world node."); }
        void LightMode(int offset)
        {
            if (current?.Class != WorldNodeClass.Light) return;
            foreach (int o in new[] { 184, 188, 192 }) current.SetPayloadInt(o, o == offset ? 1 : 0);
        }
        void Flag(uint bit, bool on) { if (current != null) current.Flags = on ? current.Flags | bit : current.Flags & ~bit; }
        void Trs(Action<WorldNode> set)
        {
            if (current?.Class != WorldNodeClass.Object3D) return;
            set(current);
            // The TRS dirty flag rebuilds the matrix in the update pass unless the matrix was authored.
            current.SetPayloadInt(0, (current.PayloadInt(0) & ~0x08) | 0x01);
        }
    }

    /// <summary>Retail interpreter commands (zinterp_parse) that create, free, attach or change nodes and models, which the build does not implement.</summary>
    private static readonly HashSet<string> Unsupported = new(["NewNode", "NewSEQ", "FreeNode", "NodeSetActive", "Object3DAddChild", "Object3DSetShowBackFace", "Object3DSetMorphVertex"], StringComparer.Ordinal);
    private static float Radians(float degrees) => degrees * (MathF.PI / 180f);
    /// <summary>ParseFloatToken's atof: the longest leading decimal number (after spaces), 0 when there is none.</summary>
    internal static float Number(string text)
    {
        var match = LeadingNumber().Match(text);
        return match.Success && double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? (float)value : 0;
    }
    [System.Text.RegularExpressions.GeneratedRegex(@"\A[ \t\n\v\f\r]*[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?")] private static partial System.Text.RegularExpressions.Regex LeadingNumber();
    private static string TextureStem(string path) => Path.GetFileNameWithoutExtension(path.Replace('\\', '/')).ToLowerInvariant();

    private void AddDirectories(List<string> list, string value)
    {
        // zRdrAddSearchPaths inserts at the head, so later directories are searched first.
        foreach (string part in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string? folder = ProjectPath(part);
            if (folder == null) continue;
            list.Remove(folder); list.Insert(0, folder);
        }
    }
    /// <summary><c>..\data\m1\models</c> (relative to the gamegen folder) → <c>data/m1/models</c>; other paths are outside the project.</summary>
    internal static string? ProjectPath(string scriptPath)
    {
        string path = scriptPath.Replace('\\', '/');
        while (path.Contains("//")) path = path.Replace("//", "/");
        if (!path.StartsWith("../", StringComparison.Ordinal)) return null;
        path = path[3..].TrimEnd('/');
        return path.Split('/').Any(p => p is "" or "." or "..") ? null : path;
    }

    // ---------------------------------------------------------------- nodes

    private WorldNode Create(string name, WorldNodeClass kind)
    {
        // Node lookups scan the table, so it never grows past what a world can hold.
        if (World.Nodes.Count >= GameZWorld.MaximumNodeCapacity) throw new InvalidDataException($"The scripts create more nodes than a world holds ({GameZWorld.MaximumNodeCapacity:N0}).");
        WorldNode node = new(name, kind) { Flags = 0x0108001C, Zone = 0xFF };
        World.Nodes.Add(node); current = node;
        return node;
    }
    private static void Object3D(WorldNode node)
    {
        node.Flags |= 0x02000000; node.BoundsFlags = 4;
        node.SetPayloadInt(0, 0x09); node.SetPayloadFloat(0x24, 1); node.SetPayloadFloat(0x28, 1); node.SetPayloadFloat(0x2C, 1);
        node.SetPayloadFloat(0x30, 1); node.SetPayloadFloat(0x40, 1); node.SetPayloadFloat(0x50, 1);
    }
    private void NewCamera(string name)
    {
        var camera = Create(name, WorldNodeClass.Camera);
        camera.SetPayloadFloat(176, 1); camera.SetPayloadFloat(180, 1000); camera.SetPayloadFloat(208, 1); camera.SetPayloadFloat(212, 1);
        camera.SetPayloadFloat(216, 1); camera.SetPayloadFloat(220, 1);
        camera.SetPayloadInt(248, 1); camera.SetPayloadInt(312, 1); camera.SetPayloadInt(388, 1); camera.SetPayloadInt(484, unchecked((int)0xFFFFFF00));
        CameraFov(camera, 60, 45);
    }
    /// <summary>CameraSetFOV: field-of-view bases in radians with their scaled values, half angles and half-angle cotangents.</summary>
    private static void CameraFov(WorldNode camera, float horizontal, float vertical)
    {
        float h = Radians(horizontal), v = Radians(vertical);
        camera.SetPayloadFloat(224, h); camera.SetPayloadFloat(228, v); camera.SetPayloadFloat(232, h); camera.SetPayloadFloat(236, v);
        camera.SetPayloadFloat(240, h / 2); camera.SetPayloadFloat(244, v / 2);
        camera.SetPayloadFloat(468, (float)(1 / Math.Tan(h / 2.0))); camera.SetPayloadFloat(472, (float)(1 / Math.Tan(v / 2.0))); camera.SetPayloadInt(312, 1);
    }
    private void NewLight(string name)
    {
        var light = Create(name, WorldNodeClass.Light);
        // gwLightNew: enabled, dirty, a small cached box and valid cached bounds.
        light.Flags |= WorldUpdate.CachedBoundsFlag;
        light.CachedBounds = new(new(1, 1, -2), new(2, 2, -1));
        light.SetPayloadInt(0, 1); light.SetPayloadInt(4, 1); light.SetPayloadFloat(144, 1); light.SetPayloadInt(196, 1); light.SetPayloadInt(200, 1);
    }

    /// <summary>FindByTypeAndName returns the most recently created live node with the name.</summary>
    private WorldNode? Find(string name, WorldNodeClass? kind)
    {
        for (int i = World.Nodes.Count - 1; i >= 0; i--)
            if (World.Nodes[i].Name == name && (kind == null || World.Nodes[i].Class == kind)) return World.Nodes[i];
        return null;
    }
    /// <summary>
    /// FindSubNodeByName: the node itself, then its children depth first from the last child. A node shared by several
    /// parents is searched once (a repeat visit cannot find what the first did not), so the search stays linear.
    /// </summary>
    internal static WorldNode? FindSub(WorldNode node, string name) => Subtree(node).FirstOrDefault(n => n.Name == name);
    /// <summary>A node and its descendants, each once, in FindSubNodeByName's order; the walk keeps its own stack.</summary>
    internal static IEnumerable<WorldNode> Subtree(WorldNode node)
    {
        HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance); Stack<WorldNode> stack = new([node]);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            if (!seen.Add(next)) continue;
            yield return next;
            foreach (var child in next.Children) stack.Push(child);
        }
    }

    private void AddChild(WorldNode parent, WorldNode child)
    {
        if (parent == child || Descends(parent, child)) { Warn($"AddChild would make {child.Name} its own ancestor."); return; }
        child.Parents.Add(parent);
        if (parent.Class == WorldNodeClass.World) worldChildren.Add(child); else parent.Children.Add(child);
    }
    /// <summary>Whether <paramref name="ancestor"/> is above <paramref name="node"/>; each ancestor is visited once.</summary>
    private static bool Descends(WorldNode node, WorldNode ancestor)
    {
        HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance); Stack<WorldNode> stack = new(node.Parents);
        while (stack.Count > 0)
        {
            var parent = stack.Pop();
            if (parent == ancestor) return true;
            if (seen.Add(parent)) foreach (var above in parent.Parents) stack.Push(above);
        }
        return false;
    }
    private void Unlink(WorldNode parent, WorldNode child)
    {
        parent.Children.Remove(child); child.Parents.Remove(parent);
        if (parent.Class == WorldNodeClass.World) worldChildren.Remove(child);
    }
    /// <summary>DestroyNodeRecursive: a node without parents is freed; each child is detached and freed only when it has no other parent.</summary>
    private void DeleteTree(WorldNode node)
    {
        if (node.Parents.Count > 0) { Warn($"DeleteTree {node.Name}: the node still has parents."); return; }
        Destroy(node);
        void Destroy(WorldNode n)
        {
            foreach (var child in n.Children.ToList())
            {
                n.Children.Remove(child); child.Parents.Remove(n);
                if (child.Parents.Count == 0) Destroy(child);
            }
            World.Nodes.Remove(n);
            if (current == n) current = null;
        }
    }

    // ---------------------------------------------------------------- models

    /// <summary>
    /// LoadGameGen file name: an object3d root named <paramref name="name"/> holding the file's scene. After
    /// GameGenSetWorld the next load is the mission database: its scene roots also become world children, so they
    /// survive when the script deletes the root. OpenFlight names resolve to .gltf files in the model directories.
    /// </summary>
    private void LoadGameGen(string file, string name, string script)
    {
        var root = Create(name, WorldNodeClass.Object3D); Object3D(root); LoadedRoots.Add(root);
        string? path = ResolveModel(file);
        if (path == null) { Warn($"{script}: LoadGameGen found no model for {file} in {string.Join(", ", modelDirectories)}."); pendingWorld = null; return; }
        // One load reads each referenced file once, so repeated references share their models, as the loader shared them.
        Dictionary<string, (GltfDocument, string)> documents = new(StringComparer.OrdinalIgnoreCase);
        (GltfDocument, string) Load(string file) => documents.TryGetValue(file, out var loaded) ? loaded : documents[file] = LoadDocument(file);
        var (doc, documentPath) = Load(path);
        if (WorldGltf.RootFlags(doc) is { } rootFlags) root.Flags = (root.Flags & ~WorldGltf.CarriedFlags) | rootFlags;
        WorldGltf.ImportContext context = new()
        {
            World = World,
            Reference = (uri, from) => Load(Relative(from, uri)),
            TextureName = (uri, name, from) => Texture(uri, name, from),
            Token = token,
        };
        List<WorldNode> nodes;
        try { nodes = WorldGltf.Import(doc, documentPath, 0xFF, context); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{path}: {ex.Message}", ex); }
        foreach (var w in context.Warnings) Warn(w);
        foreach (var (texture, addressing) in context.TextureAddressing)
            if (!TextureAddressing.TryAdd(texture, addressing) && TextureAddressing[texture] != addressing)
                Warn($"Texture {texture} is sampled with different edge modes in different models; the pack keeps the first.");
        // A shared node is reached once per parent but enters the node table once.
        HashSet<WorldNode> added = new(ReferenceEqualityComparer.Instance);
        foreach (var node in nodes)
        {
            AddNodes(node);
            root.Children.Add(node); node.Parents.Add(root);
            if (pendingWorld != null) { node.Parents.Add(pendingWorld); worldChildren.Add(node); }
        }
        // FindNode and AddChild take the newest node with a name, and the file's own nodes are newer than the root.
        if (added.Any(n => n.Name == name)) Warn($"{script}: {file} has a node of its own named {name}, so FindNode and AddChild {name} find that node rather than the loaded root.");
        pendingWorld = null; current = root;
        void AddNodes(WorldNode node)
        {
            if (!added.Add(node)) return;
            World.Nodes.Add(node);
            foreach (var child in node.Children) AddNodes(child);
        }
    }

    /// <summary>The first model directory (most recently added first) holding the file, as .gltf or .glb.</summary>
    public string? ResolveModel(string file)
    {
        string stem = Path.GetFileNameWithoutExtension(file.Replace('\\', '/'));
        foreach (string directory in modelDirectories)
            foreach (string extension in new[] { ".gltf", ".glb" })
            {
                string candidate = $"{directory}/{stem}{extension}";
                if (files.Exists(candidate)) return candidate;
            }
        return null;
    }
    private (GltfDocument Document, string Path) LoadDocument(string path)
    {
        if (!files.Exists(path)) throw new InvalidDataException($"The model {path} does not exist.");
        ModelFiles.Add(path);
        try
        {
            var doc = GltfDocument.Read(files.Read(path, token), uri =>
            {
                string buffer = Relative(path, uri); ModelFiles.Add(buffer);
                return files.Read(buffer, token);
            }, token);
            return (doc, path);
        }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{path}: {ex.Message}", ex); }
    }
    /// <summary>A URI relative to <paramref name="from"/>, as a project path that stays inside the project.</summary>
    internal static string Relative(string from, string uri)
    {
        if (uri.Contains(':') || uri.StartsWith('/') || uri.StartsWith('\\')) throw new InvalidDataException($"'{uri}' is not a relative reference.");
        List<string> parts = [.. Path.GetDirectoryName(from)!.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)];
        foreach (string part in uri.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new InvalidDataException($"'{uri}' leaves the project."); parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }
    /// <summary>
    /// The texture name a material uses and the file behind it. The engine finds textures by name in the mission's
    /// texture folders, so those come first; a texture found only where the glTF points (a model brought in from another
    /// mission) is taken from there, so the mission's packs include it.
    /// </summary>
    private string Texture(string uri, string? name, string from)
    {
        string textureName = (name ?? Path.GetFileNameWithoutExtension(uri)).ToLowerInvariant();
        // The world stores the name in a 20-byte Latin-1 field and the packs are built from files of that name.
        if (textureName.Length is < 1 or > 19 || textureName.Any(c => c > 255 || char.IsControl(c) || c is '/' or '\\' or ':'))
            throw new InvalidDataException($"{from}: texture name '{textureName}' needs 1–19 Latin-1 characters without path separators.");
        string? file = textureDirectories.Select(d => $"{d}/{textureName}{TextureSources.Extension}").FirstOrDefault(files.Exists);
        if (file == null && uri.Length > 0) { string candidate = Relative(from, uri); if (files.Exists(candidate)) file = candidate; }
        if (file == null) Warn($"{from}: texture {textureName} has no PNG; the game shows its default texture.");
        else if (TextureFiles.TryGetValue(textureName, out string? other) && !other.Equals(file, StringComparison.OrdinalIgnoreCase))
            Warn($"Texture {textureName} comes from both {other} and {file}; the pack uses {other}.");
        else TextureFiles[textureName] = file;
        return textureName;
    }

    /// <summary>GameZWriteZBDFile: the engine's update pass, then the world as the file stores it.</summary>
    private void Finish()
    {
        written = true;
        foreach (var node in World.Nodes.Where(n => n.Class == WorldNodeClass.Object3D))
        {
            int flags = node.PayloadInt(0);
            if ((flags & 0x01) != 0)
            {
                if ((flags & 0x10) == 0) SetTrsMatrix(node);
                flags &= ~0x01;
            }
            // The update leaves the cached world matrix marked stale.
            node.SetPayloadInt(0, flags | 0x20);
        }
        // AddChild chains can make any hierarchy; the passes below follow it recursively.
        WorldUpdate.CheckHierarchy(World.Nodes);
        WorldUpdate.RebuildBounds(World);
        foreach (var world in World.Nodes.Where(n => n.Class == WorldNodeClass.World))
            WorldUpdate.Partition(world, worldChildren.Where(c => c.Parents.Contains(world)).ToList());
        WorldUpdate.SingleParentFlags(World);
        // References to nodes that are no longer in the world are dropped.
        HashSet<WorldNode> live = new(World.Nodes, ReferenceEqualityComparer.Instance);
        foreach (var node in World.Nodes)
        {
            node.Parents.RemoveAll(p => !live.Contains(p)); node.Children.RemoveAll(c => !live.Contains(c));
            if (node.CameraHorizon != null && !live.Contains(node.CameraHorizon)) node.CameraHorizon = null;
        }
        foreach (var model in World.Models) WorldUpdate.RebuildModel(model);
        // Models no node uses are not written.
        var used = World.Nodes.Where(n => n.Model != null).Select(n => n.Model!).ToHashSet(ReferenceEqualityComparer.Instance);
        World.Models.RemoveAll(m => !used.Contains(m));
    }
    /// <summary>MatApplyLocalTRS (retail 0x474010): R = Ry·Rx·Rz on column vectors, scaled rows, then the translation.</summary>
    private static void SetTrsMatrix(WorldNode node)
    {
        float rx = node.PayloadFloat(0x18), ry = node.PayloadFloat(0x1C), rz = node.PayloadFloat(0x20);
        float sx = node.PayloadFloat(0x24), sy = node.PayloadFloat(0x28), sz = node.PayloadFloat(0x2C);
        Vector3 t = new(node.PayloadFloat(0x54), node.PayloadFloat(0x58), node.PayloadFloat(0x5C));
        // Row-vector form of Ry·Rx·Rz: rows are the rotated X, Y and Z axes.
        var rotation = Matrix4x4.CreateRotationZ(rz) * Matrix4x4.CreateRotationX(rx) * Matrix4x4.CreateRotationY(ry);
        Vector3 x = new Vector3(rotation.M11, rotation.M12, rotation.M13) * sx, y = new Vector3(rotation.M21, rotation.M22, rotation.M23) * sy, z = new Vector3(rotation.M31, rotation.M32, rotation.M33) * sz;
        float[] rows = [x.X, x.Y, x.Z, y.X, y.Y, y.Z, z.X, z.Y, z.Z, t.X, t.Y, t.Z];
        for (int i = 0; i < 12; i++) node.SetPayloadFloat(0x30 + i * 4, rows[i]);
    }

    private static void UpdateMax(WorldNode world)
    {
        world.SetPayloadFloat(0x44, world.PayloadFloat(0x34) + world.PayloadFloat(0x3C));
        world.SetPayloadFloat(0x48, world.PayloadFloat(0x38) + world.PayloadFloat(0x40));
    }
}
