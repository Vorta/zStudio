using System.Globalization;
using System.Numerics;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Sources;
using Recoil.Zbd.Core.Terrain;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// Assembles a mission world from a source project the way the original gamegen build did: it runs the mission's
/// scripts (<c>gamegen/mN.gs</c>) with the retail interpreter's command semantics and loads models with
/// <c>LoadGameGen</c>, reading the glTF files the scripts name (or, for an OpenFlight name, its glTF). The world is captured where the script
/// writes it (<c>GameZWriteZBDFile</c>), after the engine's update pass.
/// </summary>
public sealed partial class WorldAssembler(IProjectFiles files, CancellationToken token = default)
{
    public const int MaximumScriptDepth = 32, MaximumInstructions = 1_000_000;
    public const long MaximumScriptSourceBytes = 64 * 1024 * 1024;
    internal long ScriptSourceByteLimit { get; init; } = MaximumScriptSourceBytes;
    internal int ModelJsonByteLimit { get; init; } = GltfDocument.MaximumJsonBytes;
    internal long ModelBufferByteLimit { get; init; } = GltfDocument.MaximumBufferBytes;
    internal long LookupWorkLimit { get; init; } = LookupWorkBudget.MaximumUnits;
    internal long CommandAllocationLimit { get; init; } = WorldCommandBudget.MaximumBytes;
    internal long OperandReferenceLimit { get; init; } = ScriptOperandBudget.MaximumReferences;
    internal long OperandCharacterLimit { get; init; } = ScriptOperandBudget.MaximumCharacters;
    internal long ProbeLimit { get; init; } = WorldSearchBudget.MaximumProbes;
    internal long ModelByteLimit { get; init; } = WorldSearchBudget.MaximumModelBytes;
    private WorldSearchBudget? searchBudget;
    /// <summary>The probes and model bytes this build has spent (see <see cref="WorldSearchBudget"/>).</summary>
    internal WorldSearchBudget Search => searchBudget ??= new(ProbeLimit, ModelByteLimit);
    private ScriptOperandBudget? operandBudget;
    private ScriptOperandBudget Operands => operandBudget ??= new(OperandReferenceLimit, OperandCharacterLimit);
    private LookupWorkBudget? lookupWork;
    private LookupWorkBudget Lookups => lookupWork ??= new(LookupWorkLimit, token);
    private WorldCommandBudget? commandBudget;
    private WorldCommandBudget Commands => commandBudget ??= new(CommandAllocationLimit, token);
    private long scriptSourceBytes, scriptSourceTokens;
    public GameZWorld World { get; } = new();
    public List<string> Warnings => diagnostics.Snapshot();
    private readonly BoundedDiagnostics diagnostics = new();
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
    /// <summary>Where each node came from and which instructions last changed it.</summary>
    public Dictionary<WorldNode, WorldNodeProvenance> Provenance { get; } = new(ReferenceEqualityComparer.Instance);
    /// <summary>How many times each instruction (script, line) ran; an instruction that ran more than once changed several things. Scripts compare without case, as the file system finds them.</summary>
    public Dictionary<(string Script, int Line), int> Executions { get; } = new(ScriptLineComparer.Instance);
    /// <summary>The GameZWriteZBDFile that wrote the world: lines added before the world is written go right before it.</summary>
    public SourceInstruction? WriteInstruction { get; private set; }
    private sealed class ScriptLineComparer : IEqualityComparer<(string Script, int Line)>
    {
        public static readonly ScriptLineComparer Instance = new();
        public bool Equals((string Script, int Line) a, (string Script, int Line) b) => a.Line == b.Line && string.Equals(a.Script, b.Script, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Script, int Line) key) => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(key.Script), key.Line);
    }
    private SourceInstruction? instruction;
    private WorldNodeProvenance Origin(WorldNode node) => Provenance.TryGetValue(node, out var p) ? p : Provenance[node] = new();
    /// <summary>Commands that change the node they apply to (the current node), whose last writer provenance records.</summary>
    private static readonly HashSet<string> NodeCommands = new([
        "WorldOrigin", "WorldExtents", "WorldPartition", "WorldPartitionInclusionTolerance", "WorldPartitionMaxDECFeatureCount", "WorldSetFogState", "WorldSetFogColor",
        "WorldSetFogRange", "WorldSetFogAltitude", "WorldSetFogDensity", "WorldAddLight", "WindowOrigin", "WindowResolution", "DisplayOrigin", "DisplayResolution",
        "DisplaySetClearColor", "CameraSetWorld", "CameraSetWindow", "CameraSetHorizon", "CameraSetHorizonXZ", "CameraSetLODMultiplier", "CameraSetNearFarClip", "CameraSetFOV",
        "LightSetColor", "LightSetDiffuse", "LightSetAmbient", "LightSetRanges", "LightSetOrientation", "LightSetTranslate", "LightSetDirectedSource",
        "LightSetPointSource", "LightSetDirectional", "LightSetSaturated", "LightSetActive", "NodeSetDescription", "Object3DTranslate", "Object3DRotate",
        "Object3DScale", "SetAltitudeSurface", "SetIntersectSurface", "SetIntersectBBOX", "SetProximity", "SetLandmark", "NodeSetCanModify", "NodeSetOverwrite",
        "NodeSetLighting"], StringComparer.Ordinal);
    private static readonly HashSet<string> CreateCommands = new(["NewWorld", "NewWindow", "NewDisplay", "NewCamera", "LightNew", "NewObject3D", "LoadGameGen"], StringComparer.Ordinal);
    /// <summary>Commands that act on the current node besides <see cref="NodeCommands"/>; with those, what <see cref="WorldNodeProvenance.Applied"/> records.</summary>
    private static readonly HashSet<string> CurrentCommands = new(["AddChild", "DeleteChild", "FindSubNode", "GameGenSetWorld"], StringComparer.Ordinal);
    public string? WorldFile { get; private set; }
    public string? AnimationFile { get; private set; }
    /// <summary>Current script macro values, with the interpreter's case-sensitive identities.</summary>
    private readonly Dictionary<string, string> variables = new(StringComparer.Ordinal);
    private readonly DirectorySearchList modelDirectories = new(), textureDirectories = new(), readerDirectories = new();
    private readonly DirectoryWorkBudget directoryWork = new();
    private readonly ScriptConditions conditions = new();
    /// <summary>The instruction lines of each script read so far, by project path (the file system finds scripts without case).</summary>
    private readonly Dictionary<string, Sources.GameGenScriptLine[]> parsedScripts = new(StringComparer.OrdinalIgnoreCase);
    private WorldNode? current, pendingWorld;
    private bool written;
    private int instructions;
    /// <summary>
    /// The engine's node table (gwNodeNew 0x4478c0, FreeNodeToFreeList 0x447a70): one free list for every node class,
    /// chained in slot order at start; a freed slot goes to its head and is taken again before any other, last in, first
    /// out. Lookups by name in the game take the highest slot, so the written world keeps every node in the slot the
    /// build gave it, with freed slots that were not taken again between them.
    /// </summary>
    private readonly Dictionary<WorldNode, int> slots = new(ReferenceEqualityComparer.Instance);
    private readonly Stack<int> freeSlots = new();
    /// <summary>The node each freed slot last held: the slot keeps its name, as the engine leaves it.</summary>
    private readonly Dictionary<int, WorldNode> freedNodes = [];
    private int nextSlot;

    /// <summary>Runs <paramref name="script"/> (relative to the gamegen folder, e.g. <c>m1.gs</c>) and returns the written world.</summary>
    public GameZWorld Assemble(string script)
    {
        ReadMapZones(script);
        Source(script, 0);
        if (!written) throw new InvalidDataException($"{script} never writes a world (GameZWriteZBDFile).");
        return World;
    }

    private void Source(string script, int depth)
    {
        if (depth > MaximumScriptDepth) throw new InvalidDataException($"Scripts source each other more than {MaximumScriptDepth} levels deep.");
        string relative = $"{SourceProject.GameGenFolder}/{script.Replace('\\', '/')}";
        if (!Exists(relative)) { diagnostics.Add($"Script {script} does not exist."); return; }
        ScriptFiles.Add(relative);
        // Each script is read once per assembly: one sourced many times (or holding only comments) costs its instructions, not its text again.
        if (!parsedScripts.TryGetValue(relative, out var lines))
        {
            byte[] source;
            try { source = files.Read(relative, token, ProjectReadLimits.Text(ScriptSourceByteLimit - scriptSourceBytes)); }
            catch (InvalidDataException ex)
            { throw new InvalidDataException($"{relative}: the world's script sources together cannot be read within their per-file and cumulative limits: {ex.Message}", ex); }
            if (source.LongLength > ScriptSourceByteLimit - scriptSourceBytes)
                throw new InvalidDataException("The world's script sources together exceed 64 MiB; split or simplify the sources.");
            scriptSourceBytes += source.Length;
            var syntax = Sources.GameGenScriptSyntax.Parse(source, token);
            List<GameGenScriptLine> active = [];
            foreach (var parsed in syntax.Lines)
            {
                token.ThrowIfCancellationRequested();
                if ((scriptSourceTokens += parsed.Tokens.Count) > GameGenScriptText.MaximumTokens)
                    throw new InvalidDataException("The world's script sources together exceed four million tokens.");
                if (parsed.IsInstruction) active.Add(parsed);
            }
            token.ThrowIfCancellationRequested();
            parsedScripts[relative] = lines = [.. active];
        }
        foreach (var line in lines)
        {
            token.ThrowIfCancellationRequested();
            if (++instructions > MaximumInstructions) throw new InvalidDataException("The scripts run too many instructions.");
            var raw = line.Tokens;
            Operands.Inspect(raw, token);
            string command = raw[0];
            // Conditions follow the retail interpreter (see ScriptConditions): TRUE-valued macros, no nesting while skipping.
            if (!conditions.Runs(raw, variables)) continue;
            // Macros expand in arguments only; an unknown macro expands to nothing.
            string[] args = Operands.Expand(raw, variables, token);
            if (ScriptConditions.IsQuit(command)) return;
            // Macros are set before and after the world is written (tex_fx scripts may use them).
            if (ScriptConditions.IsSet(command)) { if (args.Length > 0) variables[args[0]] = args.Length > 1 ? args[1] : ""; continue; }
            if (ScriptConditions.IsSource(command)) { if (args.Length > 0) Source(args[0], depth + 1); continue; }
            command = ScriptCommands.Core(command);
            if (written) { Late(command, args); continue; }
            instruction = new(relative, line.Number, command, raw, args);
            Executions[(relative, line.Number)] = Executions.GetValueOrDefault((relative, line.Number)) + 1;
            var target = current;
            bool unapplied = Run(command, args, relative);
            if (CreateCommands.Contains(command) && current != null && current != target) Origin(current).Created = instruction;
            else if (target != null && (NodeCommands.Contains(command) || CurrentCommands.Contains(command) || unapplied))
            {
                var origin = Origin(target); origin.Applied.Add(instruction);
                if (NodeCommands.Contains(command)) origin.Writers[command] = instruction;
            }
            instruction = null;
        }
    }


    /// <summary>
    /// After the world is written, scripts (tex_fx) only register textures the mission pack must hold and set up the
    /// texture cycles, on the world as the game loads it.
    /// </summary>
    private void Late(string command, string[] args)
    {
        if (ScriptTexture(command, args) is { } texture) ScriptTextures.Add(TextureStem(texture));
        if (command is "FindNode" or "FindSubNode" or "CycleTextureSetOn" or "CycleTextureSetMap") (cycles ??= new(World, Lookups, token)).Run(command, args);
    }
    private TextureCycleReader? cycles;
    /// <summary>The texture cycles the scripts set up after writing the world, with the materials they are shown on.</summary>
    internal IReadOnlyList<CycledTextures> TextureCycles => cycles?.Cycles ?? [];

    private bool Run(string command, string[] args, string script)
    {
        float F(int i) => i < args.Length ? Number(args[i]) : 0;
        // ParseBoolToken (retail 0x4C19C0): only "on" and "true" (any case) are on; a missing argument is off.
        bool On(int i) => i < args.Length && (args[i].Equals("on", StringComparison.OrdinalIgnoreCase) || args[i].Equals("true", StringComparison.OrdinalIgnoreCase));
        string A(int i) => i < args.Length ? args[i] : "";
        bool unapplied = false;
        switch (command)
        {
            case "SetModelDirectory": AddDirectories(modelDirectories, A(0)); break;
            case "SetTextureDirectory": AddDirectories(textureDirectories, A(0)); break;
            // zRdrSetPath (0x48cca0) replaces the zReader path list; the other two only add to theirs.
            case "RdrSetPath": readerDirectories.Clear(); AddDirectories(readerDirectories, A(0)); break;
            case "AnimSetZBDFile": AnimationFile = A(0); break;
            case "SetGameZNodeArraySize": World.NodeCapacity = Math.Clamp((int)F(0), 16, GameZWorld.MaximumNodeCapacity); break;
            case "SetModel3DArraySize": World.ModelCapacity = Math.Clamp((int)F(0), 16, GameZWorld.MaximumNodeCapacity); break;
            case "SetMaterialArraySize": World.MaterialCapacity = Math.Clamp((int)F(0), 16, 32767); break;
            case "LensFlareTexture" or "CycleTextureSetMap" or "WriteTextureSetMap" or "TextureAdd":
                if (ScriptTexture(command, args) is { } texture)
                {
                    string name = TextureStem(texture);
                    // The directory only grows; a name registered by an earlier script command is already present.
                    if (!ScriptTextures.Contains(name))
                    {
                        WorldGltf.Texture(World, name);
                        ScriptTextures.Add(name);
                    }
                }
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
            case "WorldPartition": WorldSet(w => WorldUpdate.SetPartition(w, F(0), F(1), Commands, token)); break;
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
                { current.WorldLights.Add(light); light.AttachedWorlds.Add(current); Name(light); }
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
            case "CameraSetWorld": if (current?.Class == WorldNodeClass.Camera) current.CameraWorld = Name(Find(A(0), WorldNodeClass.World)); break;
            case "CameraSetWindow": if (current?.Class == WorldNodeClass.Camera) current.CameraWindow = Name(Find(A(0), WorldNodeClass.Window)); break;
            case "CameraSetHorizon": if (current?.Class == WorldNodeClass.Camera) current.CameraHorizon = Name(Find(A(0), WorldNodeClass.Object3D)); break;
            case "CameraSetHorizonXZ": if (current?.Class == WorldNodeClass.Camera) current.CameraHorizonXZ = Name(Find(A(0), WorldNodeClass.Object3D)); break;
            case "CameraSetLODMultiplier": if (current?.Class == WorldNodeClass.Camera) { float m = F(0), squared = WorldNumbers.Finite(m * m), inverse = WorldNumbers.Finite(m == 0 ? 0 : 1 / squared); current.SetPayloadFloat(208, m); current.SetPayloadFloat(212, inverse); } break;
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
                    float squared = WorldNumbers.Finite(far * far), difference = WorldNumbers.Finite(far - near), inverse = WorldNumbers.Finite(far > near ? 1 / difference : 0);
                    current.SetPayloadFloat(204, near); current.SetPayloadFloat(208, far); current.SetPayloadFloat(212, squared); current.SetPayloadFloat(216, inverse);
                }
                break;
            case "LightSetOrientation": if (current?.Class == WorldNodeClass.Light) { current.SetPayloadFloat(8, Radians(F(0))); current.SetPayloadFloat(12, Radians(F(1))); current.SetPayloadFloat(16, Radians(F(2))); current.SetPayloadInt(0, 1); } break;
            case "LightSetTranslate": if (current?.Class == WorldNodeClass.Light) { current.SetPayloadFloat(20, F(0)); current.SetPayloadFloat(24, F(1)); current.SetPayloadFloat(28, F(2)); current.SetPayloadInt(0, 1); } break;
            case "LightSetDirectedSource": LightMode(188); break;
            case "LightSetPointSource": LightMode(192); break;
            case "LightSetDirectional": LightMode(184); break;
            case "LightSetSaturated": if (current?.Class == WorldNodeClass.Light) current.SetPayloadInt(196, On(0) ? 1 : 0); break;
            case "LightSetActive": if (current?.Class == WorldNodeClass.Light) current.SetPayloadInt(4, On(0) ? 1 : 0); break;

            case "FindNode": current = Find(A(0), null); if (current == null) diagnostics.Add($"{script}: FindNode {A(0)} found no node."); break;
            case "FindSubNode": current = current == null ? null : Lookups.FindSub(current, A(0)); if (current == null) diagnostics.Add($"{script}: FindSubNode {A(0)} found no node."); break;
            case "NodeSetDescription": if (current != null) { current.Name = A(0); Lookups.Invalidate(current); } break;
            case "AddChild":
                if (current != null && Find(A(0), null) is { } child) { AddChild(current, child); if (instruction != null) Origin(child).Attached = instruction; Name(child); }
                else diagnostics.Add($"{script}: AddChild {A(0)} has no node or parent.");
                break;
            case "DeleteChild":
                if (current != null && Lookups.FindSub(current, A(0)) is { } removed && removed != current) { Name(removed); Unlink(current, removed); }
                break;
            case "DeleteTree": if (Find(A(0), null) is { } tree) { Name(tree); DeleteTree(tree); } break;
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
            case "NodeSetLighting": if (current != null) foreach (var model in Lookups.Subtree([current], reserveTraversal: Commands.Traversal).Select(n => n.Model).OfType<WorldModel>()) model.Flags = On(0) ? model.Flags | 1 : model.Flags & ~1u; break;

            case "LoadGameGen": LoadGameGen(A(0), A(1), script); break;
            case "GameZWriteZBDFile": WorldFile = A(0); WriteInstruction = instruction; Finish(); break;
            // Rendering and runtime settings are not part of the world file; gamegen-only commands had no retail effect.
            // Commands that change nodes or models in the retail interpreter but are not built here are reported.
            default:
                unapplied = ScriptCommands.IsRecognized(command) && !NonWorldCommands.Contains(command);
                if (unapplied) diagnostics.Add($"{script}: {command} is recognized by the game's interpreter, but the source build does not apply it.");
                break;
        }

        return unapplied;

        // A node an instruction found by name to act on.
        WorldNode? Name(WorldNode? node) { if (node != null && instruction != null) Origin(node).Named.Add(instruction); return node; }
        void WorldSet(Action<WorldNode> action) { if (current?.Class == WorldNodeClass.World) action(current); else diagnostics.Add($"{script}: {command} needs a world node."); }
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

    /// <summary>Recognized commands whose only effects are diagnostics or renderer/runtime global settings, not stored world data.
    /// Every other recognized command reaching the default dispatch is reported, so new commands cannot silently disappear.</summary>
    private static readonly HashSet<string> NonWorldCommands = new([
        "AnimSetDebugFrame", "CameraSetDynamicLOD", "CameraSetObjectHSETest", "CountUsedNodes", "ClearScreenBuffer",
        "echo", "Echo", "GetBFETolerance", "PerspectiveTexture", "PrintNodeCount", "PrintUsedNodes", "SetPaletteName",
        "SetPaletteShading", "SetPerspectiveAdaptiveCorrection", "SetPerspectiveTextureDeltaX", "SetInverseZTolerance",
        "SetPerspectiveTextureFarZ", "SetVertexShading", "Verbose"], StringComparer.Ordinal);
    private static float Radians(float degrees) => degrees * (MathF.PI / 180f);
    /// <summary>ParseFloatToken's atof: the longest leading decimal number (after spaces), 0 when there is none.</summary>
    internal static float Number(string text)
    {
        var match = LeadingNumber().Match(text);
        return match.Success && double.TryParse(match.ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ? WorldNumbers.Finite((float)value) : 0;
    }
    [System.Text.RegularExpressions.GeneratedRegex(@"\A[ \t\n\v\f\r]*[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?")] private static partial System.Text.RegularExpressions.Regex LeadingNumber();
    private static string TextureStem(string path) => Path.GetFileNameWithoutExtension(path.Replace('\\', '/')).ToLowerInvariant();
    /// <summary>
    /// The texture a script command registers (zImage::TexDirFindOrAppendByPath of the interpreter's next token, retail
    /// DispatchCoreCommand 0x4C20A0): the first operand of <c>CycleTextureSetMap</c>, <c>WriteTextureSetMap</c> and
    /// <c>TextureAdd</c>, and the one after the stage index of <c>LensFlareTexture</c>; later operands are ignored.
    /// </summary>
    internal static string? ScriptTexture(string command, IReadOnlyList<string> args)
    {
        int at = command switch { "CycleTextureSetMap" or "WriteTextureSetMap" or "TextureAdd" => 0, "LensFlareTexture" => 1, _ => -1 };
        return at >= 0 && at < args.Count ? args[at] : null;
    }

    /// <summary>SetModelDirectory, SetTextureDirectory and RdrSetPath add by the engine's search-path rule (<see cref="DirectorySearchList"/>), on the project's folders.</summary>
    private void AddDirectories(DirectorySearchList list, string value) => list.Add(value, directoryWork, FolderExists);
    private bool FolderExists(string folder) { Search.Probe(); return files.FolderExists(folder); }
    /// <summary>Every file the build looks for is charged to <see cref="Search"/> before the provider is asked.</summary>
    private bool Exists(string relative) { Search.Probe(); return files.Exists(relative); }
    /// <summary>
    /// Reads a model, buffer or terrain input within what is left of <see cref="Search"/>'s model bytes; the provider refuses a
    /// larger file before it holds it.
    /// </summary>
    private byte[] ReadModelInput(string path, ProjectReadLimits limits)
    {
        long left = Search.ModelBytesLeft;
        bool reduced = left < limits.MaximumBytes;
        byte[] bytes;
        try { bytes = files.Read(path, token, reduced ? limits.WithMaximum(left) : limits); }
        catch (InvalidDataException ex) when (reduced) { throw Search.Exceeded(path, ex); }
        Search.Read(path, bytes.LongLength);
        return bytes;
    }
    /// <summary><c>..\data\m1\models</c> (relative to the gamegen folder) → <c>data/m1/models</c>; other paths are outside the project.</summary>
    internal static string? ProjectPath(string scriptPath)
    {
        string path = scriptPath.Replace('\\', '/');
        while (path.Contains("//")) path = path.Replace("//", "/");
        if (!path.StartsWith("../", StringComparison.Ordinal)) return null;
        path = path[3..].TrimEnd('/');
        string first = path.Split('/')[0];
        if (!first.Equals("data", StringComparison.OrdinalIgnoreCase) && !first.Equals("gamegen", StringComparison.OrdinalIgnoreCase)) return null;
        return path.Split('/').Any(p => p is "" or "." or "..") ? null : path;
    }

    // ---------------------------------------------------------------- nodes

    private WorldNode Create(string name, WorldNodeClass kind)
    {
        // Node lookups scan the table, so it never grows past what a world can hold.
        ReservePersistentNode();
        WorldNode node = new(name, kind) { Flags = 0x0108001C, Zone = 0xFF };
        Allocate(node); current = node;
        return node;
    }
    private void ReservePersistentNode()
    {
        token.ThrowIfCancellationRequested();
        if (World.Nodes.Count >= GameZWorld.MaximumNodeCapacity)
            throw new InvalidDataException($"The scripts create more nodes than a world holds ({GameZWorld.MaximumNodeCapacity:N0}).");
    }
    private void Allocate(WorldNode node)
    {
        token.ThrowIfCancellationRequested();
        Lookups.Invalidate(node);
        if (deferredRemovals?.Remove(node) == true) World.Nodes.Remove(node);
        World.Nodes.Add(node);
        int slot = freeSlots.Count > 0 ? freeSlots.Pop() : nextSlot++;
        freedNodes.Remove(slot); slots[node] = slot;
    }
    private void Free(WorldNode node)
    {
        Lookups.Invalidate(node);
        if (deferredRemovals != null) deferredRemovals.Add(node); else World.Nodes.Remove(node);
        if (!slots.Remove(node, out int slot)) return;
        freeSlots.Push(slot); freedNodes[slot] = node;
    }
    /// <summary>Nodes a model load freed (its caches), removed from the world's list once the load ends rather than one search each.</summary>
    private HashSet<WorldNode>? deferredRemovals;
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
        float cotH = WorldNumbers.Finite((float)(1 / Math.Tan(h / 2.0))), cotV = WorldNumbers.Finite((float)(1 / Math.Tan(v / 2.0)));
        camera.SetPayloadFloat(224, h); camera.SetPayloadFloat(228, v); camera.SetPayloadFloat(232, h); camera.SetPayloadFloat(236, v);
        camera.SetPayloadFloat(240, h / 2); camera.SetPayloadFloat(244, v / 2);
        camera.SetPayloadFloat(468, cotH); camera.SetPayloadFloat(472, cotV); camera.SetPayloadInt(312, 1);
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
            if (Lookups.Matches(World.Nodes[i], name) && (kind == null || World.Nodes[i].Class == kind)) return World.Nodes[i];
        return null;
    }
    /// <summary>
    /// FindSubNodeByName: the node itself, then its children depth first from the last child. A node shared by several
    /// parents is searched once (a repeat visit cannot find what the first did not), so the search stays linear.
    /// </summary>
    internal static WorldNode? FindSub(WorldNode node, string name) => new LookupWorkBudget().FindSub(node, name);
    /// <summary>A node and its descendants, each once, in FindSubNodeByName's order; the walk keeps its own stack.</summary>
    internal static IEnumerable<WorldNode> Subtree(WorldNode node) => Subtree([node]);
    /// <summary>
    /// The nodes and their descendants, each once: records sharing nodes (instances) are walked together, not once per
    /// record. Each subtree comes in FindSubNodeByName's order, without the nodes an earlier one held.
    /// </summary>
    internal static IEnumerable<WorldNode> Subtree(IEnumerable<WorldNode> roots)
    {
        HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance); Stack<WorldNode> stack = new();
        foreach (var root in roots)
        {
            stack.Push(root);
            while (stack.Count > 0)
            {
                var next = stack.Pop();
                if (!seen.Add(next)) continue;
                yield return next;
                foreach (var child in next.Children) stack.Push(child);
            }
        }
    }

    private void AddChild(WorldNode parent, WorldNode child)
    {
        if (parent == child || Descends(parent, child)) { diagnostics.Add($"AddChild would make {child.Name} its own ancestor."); return; }
        child.Parents.Add(parent);
        parent.Children.Add(child);
    }
    /// <summary>Whether <paramref name="ancestor"/> is above <paramref name="node"/>; each ancestor is visited once.</summary>
    private bool Descends(WorldNode node, WorldNode ancestor)
    {
        Lookups.Reserve(node.Parents.Count);
        HashSet<WorldNode> seen = new(ReferenceEqualityComparer.Instance); Stack<WorldNode> stack = new(node.Parents);
        while (stack.Count > 0)
        {
            Lookups.Reserve(1);
            var parent = stack.Pop();
            if (parent == ancestor) return true;
            if (seen.Add(parent)) { Lookups.Reserve(parent.Parents.Count); foreach (var above in parent.Parents) stack.Push(above); }
        }
        return false;
    }
    private void Unlink(WorldNode parent, WorldNode child)
    {
        // List.Remove scans for the occurrence and shifts its tail; repeated duplicate edges must pay for
        // that actual list work, not only for visiting the two nodes.
        Lookups.Reserve((long)parent.Children.Count + child.Parents.Count);
        parent.Children.Remove(child); child.Parents.Remove(parent);
    }
    /// <summary>DestroyNodeRecursive: a node without parents is freed; each child is detached and freed only when it has no other parent.</summary>
    private void DeleteTree(WorldNode node)
    {
        if (node.Parents.Count > 0) { diagnostics.Add($"DeleteTree {node.Name}: the node still has parents."); return; }
        // Script-built hierarchies have not reached Finish's depth validation yet. Follow child order without using
        // the process stack; shared children are released only when their last parent edge has been removed.
        bool ownsRemovals = deferredRemovals == null;
        if (ownsRemovals) Lookups.Reserve(World.Nodes.Count);
        deferredRemovals ??= new(ReferenceEqualityComparer.Instance);
        Commands.Traversal(1);
        Stack<(WorldNode Node, int Next)> pending = new([(node, 0)]);
        try
        {
            while (pending.TryPop(out var frame))
            {
                token.ThrowIfCancellationRequested();
                var (n, next) = frame;
                if (next < n.Children.Count)
                {
                    Commands.Traversal(2);
                    pending.Push((n, next + 1));
                    var child = n.Children[next];
                    Lookups.Reserve(child.Parents.Count);
                    child.Parents.Remove(n);
                    if (child.Parents.Count == 0) pending.Push((child, 0));
                    continue;
                }
                n.Children.Clear(); Free(n);
                if (current == n) current = null;
            }
        }
        finally
        {
            if (ownsRemovals)
            {
                var removed = deferredRemovals; deferredRemovals = null;
                World.Nodes.RemoveAll(removed.Contains);
            }
        }
    }

    // ---------------------------------------------------------------- models

    /// <summary>
    /// LoadGameGen file name: an object3d root named <paramref name="name"/> holding the file's scene. After
    /// GameGenSetWorld the next load is the mission database: its objects (its scene roots, or below its groups, also in
    /// the parts its groups reference) also become world children, in source traversal order, so they survive when the
    /// script deletes the root with the groups. OpenFlight names resolve
    /// to .gltf files in the model directories. Nodes take slots in the order the original loader created them (see
    /// <see cref="OriginalLoader"/>).
    /// </summary>
    private void LoadGameGen(string file, string name, string script)
    {
        // Every load retains its root, including absent models and valid empty scenes. Refuse before changing
        // current/LoadedRoots. OriginalLoader's temporary cache slots keep their separate lifetime and budget.
        ReservePersistentNode();
        // The root takes its slot after the caches of the files the load references (see OriginalLoader).
        WorldNode root = new(name, WorldNodeClass.Object3D) { Flags = 0x0108001C, Zone = 0xFF }; Object3D(root); LoadedRoots.Add(root); current = root;
        string? path = ResolveModel(file);
        if (path == null) { Allocate(root); diagnostics.Add($"{script}: LoadGameGen found no model for {file} in {BoundedDiagnostics.DirectoryList(modelDirectories.Folders)}."); pendingWorld = null; return; }
        // One load parses each file once; models follow the loader's caches (see WorldGltf.ImportContext).
        Dictionary<string, (GltfDocument, string)> documents = new(StringComparer.OrdinalIgnoreCase);
        (GltfDocument, string) Load(string file)
        {
            _ = ModelExists(file);
            string geometry = GeometryPath(file);
            if (!documents.TryGetValue(geometry, out var loaded)) documents[geometry] = loaded = LoadDocument(geometry);
            return (loaded.Item1, file);
        }
        var (doc, documentPath) = Load(path);
        if (WorldGltf.RootFlags(doc) is { } rootFlags) root.Flags = (root.Flags & ~WorldGltf.CarriedFlags) | rootFlags;
        if (ZoneAsset(path)?.Profile.LoadRoot is { } rootZone)
        {
            root.Zone = rootZone.Word;
            root.Flags = rootZone.Gate ? root.Flags | 0x01000000u : root.Flags & ~0x01000000u;
        }
        bool database = pendingWorld != null; var load = instruction;
        Origin(root).LoadedFile = GeometryPath(documentPath);
        Origin(root).LogicalLoadedFile = documentPath;
        Origin(root).ZoneManifest = ZoneAsset(documentPath) != null ? zoneManifest : null;
        Origin(root).ImportedZoneWord = root.Zone;
        Origin(root).ImportedZoneGate = (root.Flags & WorldGltf.ZoneGate) != 0;
        HashSet<WorldNode> groups = new(ReferenceEqualityComparer.Instance);
        List<WorldNode> imported = [];
        // A reference's file as its node names it: references naming one file by different paths each have a cache.
        Dictionary<WorldNode, string> referenceText = new(ReferenceEqualityComparer.Instance);
        HashSet<string> parts = new(StringComparer.OrdinalIgnoreCase);
        WorldGltf.ImportContext context = null!;
        context = new()
        {
            World = World, Diagnostics = diagnostics, LinkWork = Lookups, Created = 1, // Reserve the not-yet-allocated load root too.
            ZoneProfile = (file, _) => ZoneAsset(file)?.Profile,
            AssetReference = ZoneReference,
            NodeImported = (node, file, source, place) =>
            {
                imported.Add(node);
                if (context.Referencing is { } referencing) Origin(node).ReferencedBy = Origin(referencing);
                var origin = Origin(node); origin.ModelFile = GeometryPath(file); origin.LogicalModelFile = file; origin.ModelNode = source.Index; origin.ModelNodeName = source.Name; origin.ModelTransformAuthored = source.Matrix is { } m && !m.IsIdentity; origin.Load = load;
                var layout = ZoneLayout(documents[GeometryPath(file)].Item1)[source];
                int zoneNode = layout.Node;
                origin.ZoneManifest = ZoneAsset(file) != null ? zoneManifest : null;
                origin.ZoneNode = zoneNode; origin.ZoneMesh = layout.Mesh;
                origin.Instance = place;
                // The database's nodes: of its file and of its parts, the files its groups reference.
                bool part = parts.Contains(file);
                origin.Database = database && (part || string.Equals(file, documentPath, StringComparison.OrdinalIgnoreCase));
                origin.Part = database && part;
                string? uri = SourceMissionModels.EffectiveReference(source.Extras, ZoneReference(file, zoneNode));
                if (uri != null) referenceText[node] = uri;
                if (origin.Database && WorldGltf.IsGroup(source, file)) { groups.Add(node); if (uri != null) parts.Add(Relative(file, uri)); }
            },
            Reference = (uri, from) => Load(Relative(from, uri)),
            ReadFile = (uri, from) =>
            {
                string file = Relative(GeometryPath(from), uri);
                if (!Exists(file)) throw new InvalidDataException($"{from} names {JsonData.ShownText(uri)}, which does not exist.");
                ModelFiles.Add(file);
                return (ReadModelInput(file, ProjectReadLimits.Bytes(TerrainRecipe.MaximumBytes)), file);
            },
            Grid = database ? () => Grid(pendingWorld!) : null,
            TerrainPieceImported = (node, recipe, piece, surface) =>
            {
                var origin = Origin(node); origin.Terrain = recipe; origin.TerrainSurface = surface; origin.TerrainCell = (piece.Column, piece.Row); origin.Load = load;
            },
            TextureName = (uri, name, from) => Texture(uri, name, GeometryPath(from)),
            Token = token,
        };
        List<WorldNode> nodes;
        int firstModel = World.Models.Count;
        try { nodes = WorldGltf.Import(doc, documentPath, root.Zone & 0xFF, context); }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{path}: {ex.Message}", ex); }
        finally { zoneLayouts.Clear(); } // Do not retain parsed geometry after this load's provenance is complete.
        foreach (var node in imported)
        {
            token.ThrowIfCancellationRequested();
            Origin(node).ImportedZoneWord = node.Zone;
            Origin(node).ImportedZoneGate = (node.Flags & WorldGltf.ZoneGate) != 0;
            if (node.Model != null && context.SourcePolygons.TryGetValue(node.Model, out var polygons)) Origin(node).ZonePolygons = polygons;
        }
        foreach (var (texture, addressing) in context.TextureAddressing)
            if (!TextureAddressing.TryAdd(texture, addressing) && TextureAddressing[texture] != addressing)
                throw new InvalidDataException($"Texture {JsonData.ShownText(texture)} is sampled with different edge modes in different models; use one mode per texture or give the images distinct names.");
        // Nodes take slots as the original loader made and freed them (caches of referenced files, the root, the records,
        // each reference's content after the next record). A referenced file's nodes are the children from another file.
        string FileOf(WorldNode node) => Provenance.TryGetValue(node, out var p) && p.LogicalModelFile != null ? p.LogicalModelFile : documentPath;
        // Each node's content once: the loader asks for it several times per node (the load's children no longer change).
        Dictionary<WorldNode, List<WorldNode>> contents = new(ReferenceEqualityComparer.Instance);
        List<WorldNode> Content(WorldNode node) => contents.TryGetValue(node, out var found) ? found
            : contents[node] = [.. node.Children.Where(c => !string.Equals(FileOf(c), FileOf(node), StringComparison.OrdinalIgnoreCase))];
        // The loader made a model when it read the node from its file (a cache's load or the file's own records), so the
        // load's models are in that order, as the world stores them.
        Dictionary<WorldModel, int> read = new(ReferenceEqualityComparer.Instance);
        deferredRemovals = new(ReferenceEqualityComparer.Instance);
        try
        {
            OriginalLoader.Load(root, nodes, nodes, new()
            {
                Allocate = Allocate, Free = Free, Content = Content,
                File = node => $"{FileOf(node)}|{(referenceText.TryGetValue(node, out var text) ? text : FileOf(Content(node)[0]))}",
                Read = node => { if (node.Model != null) read.TryAdd(node.Model, read.Count); },
                // A reference to a file without nodes is cached and copied like any other.
                IsReference = referenceText.ContainsKey, Token = token,
            });
        }
        finally
        {
            var removed = deferredRemovals; deferredRemovals = null;
            if (removed.Count > 0) World.Nodes.RemoveAll(removed.Contains);
        }
        var loaded = World.Models.GetRange(firstModel, World.Models.Count - firstModel).OrderBy(m => read.GetValueOrDefault(m, int.MaxValue)).ToList();
        World.Models.RemoveRange(firstModel, loaded.Count); World.Models.AddRange(loaded);
        foreach (var node in nodes) { root.Children.Add(node); node.Parents.Add(root); }
        if (pendingWorld != null)
        {
            // Attachment follows source traversal, not allocation: a delayed part copy allocates its next record
            // first, but the part's objects still precede that record in the world's encounter order.
            foreach (var member in Members(nodes, new(ReferenceEqualityComparer.Instance)))
            { member.Parents.Add(pendingWorld); pendingWorld.Children.Add(member); }
        }
        // FindNode and AddChild take the newest node with a name, and the file's own nodes are newer than the root.
        if (Subtree(nodes).Any(n => n.Name == name)) diagnostics.Add($"{script}: {file} has a node of its own named {name}, so FindNode and AddChild {name} find that node rather than the loaded root.");
        pendingWorld = null; current = root;
        // The database's objects: its scene roots, and for a group the objects below it. A group's own transform would be
        // lost when the script deletes it (its objects keep theirs), so a group has none.
        IEnumerable<WorldNode> Members(IEnumerable<WorldNode> records, HashSet<WorldNode> seen)
        {
            foreach (var node in records)
            {
                if (!seen.Add(node)) continue;
                if (!groups.Contains(node)) { yield return node; continue; }
                if (node.Class != WorldNodeClass.Object3D || node.Model != null || WorldUpdate.LocalMatrix(node) is { } m && !m.IsIdentity)
                    throw new InvalidDataException($"{path}: group {node.Name} has geometry or a transform; a group of the mission database holds only objects, which keep their own transforms when the build deletes it.");
                foreach (var member in Members(node.Children, seen)) yield return member;
            }
        }
    }

    /// <summary>The world's area grid as WorldOrigin, WorldExtents and WorldPartition set it.</summary>
    private static Terrain.TerrainGrid Grid(WorldNode world) => new(world.PayloadFloat(0x34), world.PayloadFloat(0x38), world.PayloadFloat(0x3C), world.PayloadFloat(0x40),
        world.PayloadFloat(0x54), world.PayloadFloat(0x58), world.PayloadInt(0x78), world.PayloadInt(0x7C));

    /// <summary>
    /// The first model directory (in search order, <see cref="DirectorySearchList"/>) holding the file the script names: a
    /// .gltf or .glb file as named, or for another name (the original OpenFlight .flt of older projects) its .gltf or .glb.
    /// </summary>
    public string? ResolveModel(string file) => ResolveModel(file, modelDirectories.Folders, ModelExists);
    /// <summary>The model a <c>LoadGameGen</c> of <paramref name="file"/> loads from <paramref name="directories"/> (searched in order), or null.</summary>
    internal static string? ResolveModel(string file, IReadOnlyList<string> directories, Func<string, bool> exists)
    {
        string name = Path.GetFileName(file.Replace('\\', '/')), stem = Path.GetFileNameWithoutExtension(name), named = Path.GetExtension(name).ToLowerInvariant();
        string[] extensions = named is ".gltf" or ".glb" ? [named] : [".gltf", ".glb"];
        foreach (string directory in directories)
            foreach (string extension in extensions)
            {
                string candidate = $"{directory}/{stem}{extension}";
                if (exists(candidate)) return candidate;
            }
        return null;
    }
    private (GltfDocument Document, string Path) LoadDocument(string path)
    {
        if (!Exists(path)) throw new InvalidDataException($"The model {JsonData.ShownText(path)} does not exist.");
        ModelFiles.Add(path);
        try
        {
            var doc = ReadModel(ReadModelInput(path, ProjectReadLimits.Model(maximumJsonBytes: ModelJsonByteLimit)), path, (buffer, remaining) =>
            {
                ModelFiles.Add(buffer);
                return ReadModelInput(buffer, ProjectReadLimits.Bytes(remaining));
            }, token, ModelBufferByteLimit);
            return (doc, path);
        }
        catch (InvalidDataException ex) { throw new InvalidDataException($"{path}: {ex.Message}", ex); }
    }
    /// <summary>
    /// Reads a project model with external-buffer admission. Canonical project aliases share the same held array and do not spend its
    /// remaining allowance again. The cache is local to one document read; full project identities are retained.
    /// </summary>
    internal static GltfDocument ReadModel(ReadOnlySpan<byte> bytes, string from, Func<string, long, byte[]> read,
        CancellationToken token, long bufferBytes = GltfDocument.MaximumBufferBytes)
        => GltfDocument.Read(bytes, (uri, remaining) => read(Relative(from, uri), remaining), bufferBytes, token,
            uri => Relative(from, uri).ToUpperInvariant());

    /// <summary>A URI relative to <paramref name="from"/>, as a project path that stays inside the project.</summary>
    internal static string Relative(string from, string uri)
    {
        if (uri.Contains(':') || uri.StartsWith('/') || uri.StartsWith('\\')) throw new InvalidDataException($"'{JsonData.ShownText(uri)}' is not a relative reference.");
        List<string> parts = [.. Path.GetDirectoryName(from)!.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries)];
        foreach (string part in uri.Replace('\\', '/').Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) throw new InvalidDataException($"'{JsonData.ShownText(uri)}' leaves the project."); parts.RemoveAt(parts.Count - 1); }
            else parts.Add(part);
        }
        return string.Join('/', parts);
    }
    /// <summary>
    /// The texture name a material uses and the file behind it: the name of the image it shows, or without one the name it
    /// records (an image an editor assigned in place of the recorded texture is the one the build uses). The engine finds
    /// textures by name in the mission's texture folders, so those come first; a texture found only where the glTF points
    /// (a model brought in from another mission) is taken from there, so the mission's packs include it.
    /// </summary>
    private string Texture(string uri, string? name, string from)
    {
        string textureName = (uri.Length > 0 ? Path.GetFileNameWithoutExtension(uri) : name ?? "").ToLowerInvariant();
        // The world stores the name in a 20-byte Latin-1 field and the packs are built from files of that name.
        if (textureName.Length is < 1 or > 19 || textureName.Any(c => c > 255 || char.IsControl(c) || c is '/' or '\\' or ':'))
            throw new InvalidDataException($"{from}: texture name '{JsonData.ShownText(textureName)}' needs 1–19 Latin-1 characters without path separators{(uri.Length > 0 ? $"; rename its PNG, {JsonData.ShownText(Path.GetFileName(uri))}" : "")}.");
        string? file = textureDirectories.Folders.Select(d => $"{d}/{textureName}{TextureSources.Extension}").FirstOrDefault(Exists);
        if (file == null && uri.Length > 0) { string candidate = Relative(from, uri); if (Exists(candidate)) file = candidate; }
        if (file == null) diagnostics.Add($"{from}: texture {textureName} has no PNG; the game shows its default texture.");
        else if (TextureFiles.TryGetValue(textureName, out string? other) && !other.Equals(file, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add($"Texture {textureName} comes from both {other} and {file}; the pack uses {other}.");
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
        WorldUpdate.RebuildBounds(World, token);
        foreach (var world in World.Nodes.Where(n => n.Class == WorldNodeClass.World))
            WorldUpdate.Partition(world, world.Children.ToArray(), parentsAttached: true);
        WorldUpdate.SingleParentFlags(World);
        // References to nodes that are no longer in the world are dropped.
        HashSet<WorldNode> live = new(World.Nodes, ReferenceEqualityComparer.Instance);
        foreach (var node in World.Nodes)
        {
            node.Parents.RemoveAll(p => !live.Contains(p)); node.Children.RemoveAll(c => !live.Contains(c));
            if (node.CameraHorizon != null && !live.Contains(node.CameraHorizon)) node.CameraHorizon = null;
            if (node.CameraHorizonXZ != null && !live.Contains(node.CameraHorizonXZ)) node.CameraHorizonXZ = null;
            if (node.CameraWorld != null && !live.Contains(node.CameraWorld)) node.CameraWorld = null;
            if (node.CameraWindow != null && !live.Contains(node.CameraWindow)) node.CameraWindow = null;
        }
        foreach (var model in World.Models) WorldUpdate.RebuildModel(model);
        // Models no node uses are not written.
        var used = World.Nodes.Where(n => n.Model != null).Select(n => n.Model!).ToHashSet(ReferenceEqualityComparer.Instance);
        World.Models.RemoveAll(m => !used.Contains(m));
        // Every node in its slot; the free list runs from the last freed slot down to the first, then on to the never-used slots.
        var ordered = World.Nodes.OrderBy(n => slots[n]).ToList();
        World.Nodes.Clear(); World.Nodes.AddRange(ordered);
        World.FreedSlots.Clear();
        int[] chain = [.. freeSlots];
        // A full table ends the list with -1, as the engine's initial chain does.
        int tail = nextSlot < World.NodeCapacity ? nextSlot : -1;
        for (int i = 0; i < chain.Length; i++) World.FreedSlots[chain[i]] = FreedSlot(freedNodes[chain[i]], i + 1 < chain.Length ? chain[i + 1] : tail);
        World.FreeHead = chain.Length > 0 ? chain[0] : tail;
    }
    /// <summary>A slot on the free list as the world file stores it: the name it last held, no model, and the next free slot.</summary>
    private static byte[] FreedSlot(WorldNode node, int next)
    {
        byte[] slot = new byte[GameZWriter.NodeSlotSize];
        node.NameField.CopyTo(slot, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(slot.AsSpan(60), -1);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(slot.AsSpan(GameZWriter.NodeSlotSize - 4), (uint)next & 0x00FFFFFF);
        return slot;
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
