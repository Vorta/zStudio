using System.Text.Json.Nodes;
using zStudio.Core.Animation;
using zStudio.Core.Formats;
using zStudio.Core.Worlds;

namespace zStudio.Core.Sources;

/// <summary>A resource and its exact source syntax in one accepted project revision.</summary>
public sealed record SourceSceneResource(string Path, byte[] Bytes, ZrdNode? Root, ZrdTextSyntax? Syntax);
public sealed record SourceSceneTexture(string Path, string Hash, byte[] Bytes, int Width, int Height)
{
    public DecodedImage Decode(CancellationToken token) => SourceBuilder.DecodeTexture(Path, Bytes, token);
}

/// <summary>An evaluated mission. No archive, archive bytes or preview directory belongs to this snapshot.</summary>
public sealed class SourceSceneSnapshot : AssetDocument
{
    public SourceSceneSnapshot(string root, string mission, GameVariant game = GameVariant.Recoil) : base(System.IO.Path.Combine(root, "gamegen", mission + ".gs"), FileStamp.Unverified,
        new(FormatFamily.GameZ, game == GameVariant.MechWarrior3 ? 27u : 15u, Recognition.Supported, "Evaluated source world"), ReadOnlyMemory<byte>.Empty)
    { Root = root; Mission = mission; SourceSyntax = "source-world"; }
    public string Root { get; }
    public string Mission { get; }
    public string? SourceMission { get; init; }
    internal AssetDocument? NativeLibrary { get; init; }
    public required GameZWorld World { get; init; }
    internal long RetainedBytes { get; init; }
    internal IReadOnlySet<string> TerrainModelInputs { get; init; } = new HashSet<string>();
    internal IReadOnlyDictionary<string, byte[]> ModelSources { get; init; } = new Dictionary<string, byte[]>();
    internal IReadOnlyDictionary<int, int[]> WorldChildOrder { get; init; } = new Dictionary<int, int[]>();
    public required IReadOnlyDictionary<string, SourceSceneResource> Resources { get; init; }
    public required IReadOnlyDictionary<string, string> TextureFiles { get; init; }
    public required IReadOnlyDictionary<string, SourceSceneTexture> Textures { get; init; }
    public IReadOnlyDictionary<string, byte[]> Sounds { get; init; } = new Dictionary<string, byte[]>();
    public required IReadOnlyDictionary<string, ScriptContent> TextureScripts { get; init; }
    public required IReadOnlyDictionary<int, WorldNodeProvenance> Provenance { get; init; }
    public required IReadOnlyList<WorldNodeProvenance> Freed { get; init; }
    public required SourceAuthoredHierarchy AuthoredHierarchy { get; init; }
    public required IReadOnlyDictionary<(string Script, int Line), int> Executions { get; init; }
    public SourceInstruction? WriteInstruction { get; init; }
    public required IReadOnlyDictionary<string, FileStamp> Inputs { get; init; }
    public required IReadOnlyDictionary<string, string> ReadHashes { get; init; }
    public required IReadOnlyDictionary<string, string> InputHashes { get; init; }
    public required IReadOnlyCollection<string> Dependencies { get; init; }
    public required IReadOnlyList<string> MissingInputs { get; init; }
    public required IReadOnlyList<string> MissingFolders { get; init; }
    public IReadOnlyList<string> TextureInputs { get; init; } = [];
    public IReadOnlyList<string> SoundInputs { get; init; } = [];
    public IReadOnlyList<string> ScriptInputs { get; init; } = [];
    public IReadOnlyList<string>? LibraryInputs { get; init; }
    public required IReadOnlyList<string> ResourceInputs { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public required IReadOnlyList<SourceLookup> Lookups { get; init; }
    /// <summary>The map's animation definitions as its list reaches them, with what each binds to (read-only).</summary>
    public SourceAnimationDefinitions AnimationDefinitions { get; init; } = SourceAnimationDefinitions.Empty;
    [System.Text.Json.Serialization.JsonIgnore]
    public AnimationSourceProvenance? AnimationProvenance { get; init; }
    public SourceSceneState BuildState => new(Mission, Root, Inputs)
    {
        Game = Game, SourceMission = SourceMission,
        Notes = Notes, Dependencies = Dependencies, MissingInputs = MissingInputs, MissingFolders = MissingFolders, InputHashes = InputHashes,
        ReadHashes = ReadHashes, ResourceInputs = ResourceInputs, TextureInputs = TextureInputs, SoundInputs = SoundInputs, ScriptInputs = ScriptInputs, LibraryInputs = LibraryInputs, Lookups = Lookups, Provenance = Provenance,
        Freed = Freed, AuthoredHierarchy = AuthoredHierarchy, Executions = Executions, WriteInstruction = WriteInstruction
    };

    /// <param name="playerTank">How to draw the local player's tank (a preview choice); the defaults when null.</param>
    public MissionSceneContext MissionScene(MissionDifficulty difficulty, CancellationToken token = default, PlayerTankState? playerTank = null)
    {
        if (Game == GameVariant.MechWarrior3)
            return MissionSceneLoader.BuildMw3SourceScene(this, Resources.Values.Where(r => r.Root != null).Select(r => new SourceResourceDocument(Root, r, token)).ToArray(), NativeLibrary, SourceMission, token);
        var requested = MissionLayoutSelection.For(difficulty);
        List<string> notes = [.. Notes];
        // A file named like a network that the game reads no network from (net_5.zrd, net_00.zrd) is noted, at most four.
        foreach (var (name, resource) in Resources.Where(r => MissionAiNetworks.LooksLikeNetwork(r.Key)).Take(4))
            notes.Add($"{resource.Path}: the game reads no AI network from this file. A network file is net_ and a number of two or more digits from 01 (net_05.zrd, net_150.zrd).");
        string Select(string name, string fallback)
        {
            if (Resources.ContainsKey(name) || name == fallback) return name;
            notes.Add($"Mission {difficulty}: {name} is unavailable; using {fallback}."); return fallback;
        }
        string aiv = Select(requested.AivResource, "aiv.zrd"), vehicles = Select(requested.VehicleResource, "vehicle.zrd"), pickups = Select(requested.PickupResource, "puppies.zrd");
        JsonNode? Decode(string name) => Resources.TryGetValue(name, out var resource) ? resource.Root?.ToJson(token) : null;
        MissionResourceSource? Source(string name) => Resources.TryGetValue(name, out var resource) ? new(System.IO.Path.Combine(Root, resource.Path), 0, name) : null;
        var scene = MissionSceneLoader.Build(this, Animations, Decode(aiv), Decode(vehicles), Decode("startanims.zrd"), notes, token,
            new(difficulty, aiv, vehicles, pickups), Decode("ai.zrd"), Decode(pickups), Source(pickups), Source(aiv), Decode("weapons.zrd"), playerTank);
        List<AiNetwork> networks = []; int retainedNotes = 0; long omittedNotes = 0;
        // The game reads a network number from the first member that names it: the common archive's, then the mission's,
        // each in the path order the build stores them in (net_005.zrd before net_05.zrd). A later file of that number is
        // never read; the network that is read says so.
        // A file that does not parse keeps its place in that order: it is shown, empty, with its problem.
        var candidates = Resources.Where(r => MissionAiNetworks.IsCandidate(r.Key))
            .OrderBy(r => r.Value.Path.StartsWith("data/common/", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(r => r.Value.Path, StringComparer.Ordinal).ToArray();
        Dictionary<int, List<string>> unread = [];
        HashSet<int> numbers = [];
        foreach (var (name, resource) in candidates)
            if (MissionAiNetworks.TryNetworkNumber(name, out int number) && !numbers.Add(number))
            {
                if (!unread.TryGetValue(number, out var paths)) unread.Add(number, paths = []);
                paths.Add(resource.Path);
            }
        numbers.Clear();
        foreach (var (name, resource) in candidates)
        {
            token.ThrowIfCancellationRequested();
            MissionAiNetworks.TryNetworkNumber(name, out int number);
            if (!numbers.Add(number)) continue;
            AiNetwork network;
            try { network = MissionAiNetworks.Decode(resource.Path, System.IO.Path.Combine(Root, resource.Path), 0, name, resource.Root ?? throw new InvalidDataException("The file could not be read; the map's notes say why."), token); }
            catch (InvalidDataException error)
            { network = new(resource.Path, System.IO.Path.Combine(Root, resource.Path), 0, name, name, "", 10, [], [new("Warning", $"{resource.Path}: {JsonData.ShownText(error.Message, 512)}")]); }
            if (unread.TryGetValue(number, out var others))
                network = network with { Diagnostics = [.. others.Take(4).Select(other => new Diagnostic("Warning", $"{resource.Path}: {other} also names network {number}; the game reads only this file.")), .. network.Diagnostics] };
            int keep = Math.Min(network.Diagnostics.Count, PreviewNotes.MaximumItems - retainedNotes);
            omittedNotes += network.Diagnostics.Count - keep; retainedNotes += keep;
            networks.Add(network with { Diagnostics = network.Diagnostics.Take(keep).ToArray() });
        }
        scene.AiNetworks = new(Guid.NewGuid().ToString("N"), networks) { OmittedDiagnostics = omittedNotes };
        return scene;
    }

    /// <summary>Evaluate another native mission against this same immutable world/library, reading only its resources.</summary>
    internal MissionSceneContext NativeMissionScene(string? sourceMission, IReadOnlyDictionary<string, byte[]> overlay,
        SourceWorkspace? tracking, CancellationToken token)
    {
        if (Game != GameVariant.MechWarrior3) throw new InvalidOperationException("Native missions require an MW3 snapshot.");
        SourceBuilder.Snapshot inputs = new(Root, overlay, inventoryToken: token);
        var paths = SourceGameProfile.Resources(Root, Mission, inputs, sourceMission, token, Game);
        Dictionary<string, SourceSceneResource> resources = new(StringComparer.OrdinalIgnoreCase);
        PreviewResourceBudget budget = new();
        foreach (string path in paths)
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes = inputs.Read(path, token, ProjectReadLimits.Resource());
            ZrdTextSyntax? syntax = ZrdText.LooksLikeText(bytes) ? ZrdTextSyntax.Parse(bytes, token) : null;
            var root = syntax?.Root ?? ZrdDecoder.Read(bytes, token);
            budget.Interpret(root, token);
            resources.TryAdd(System.IO.Path.GetFileName(path), new(path, bytes, root, syntax));
            if (tracking != null && (tracking.Read(path, token, ProjectReadLimits.Resource()) is not { } actual || !actual.AsSpan().SequenceEqual(bytes)))
                throw new SourceFileChangedException($"{path} changed during mission validation.", [path]);
        }
        inputs.CheckUnchanged(token);
        return MissionSceneLoader.BuildMw3SourceScene(this, resources.Values.Select(r => new SourceResourceDocument(Root, r, token)).ToArray(), NativeLibrary, sourceMission, token);
    }
}

public static partial class SourceWorlds
{
    /// <summary>Loads source geometry, resources and animations without writing or reading any derived archive.</summary>
    public static Task<SourceSceneSnapshot> LoadSceneAsync(string root, string mission, IReadOnlyDictionary<string, byte[]>? overlay = null,
        CancellationToken token = default, IReadOnlyList<SourceModelAddition>? additions = null, string? sourceMission = null)
        => Task.Run(() => LoadScene(root, mission, overlay, token, additions, sourceMission), token);

    private static SourceSceneSnapshot LoadScene(string root, string mission, IReadOnlyDictionary<string, byte[]>? overlay, CancellationToken token,
        IReadOnlyList<SourceModelAddition>? additions, string? sourceMission)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        SourceGameProfile.RequireMap(mission);
        mission = mission.ToLowerInvariant(); SourceProject.RejectLinkedProject(root);
        SourceBuilder.Snapshot snapshot = new(root, overlay, path => new SourceFileChangedException($"{path} changed while loading {mission}; reload the map.", [path]), inventoryToken: token, retainModelSources: true);
        if (snapshot.Game == GameVariant.MechWarrior3)
        {
            var choices = SourceGameProfile.Missions(root, mission, snapshot, token);
            sourceMission ??= choices.FirstOrDefault()?.SourceMission;
            if (sourceMission != null && !choices.Any(c => c.SourceMission.Equals(sourceMission, StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("The requested source mission is unavailable.");
        }
        else if (sourceMission != null) throw new InvalidDataException("sourceMission applies to MW3 source maps; RECOIL selects a difficulty layout.");
        var assembled = snapshot.World(mission, token);
        if (additions != null) CheckAdditions(assembled.LoadedRoots, additions, assembled.Provenance);
        var scene = WorldScene.Create(assembled.World, token);
        snapshot.Retain(SourceExtractor.Footprint(assembled.World));
        var resourceInputs = SourceGameProfile.Resources(root, mission, snapshot, sourceMission, token);
        foreach (var scope in resourceInputs.GroupBy(input => snapshot.Game == GameVariant.MechWarrior3 ? Path.GetDirectoryName(input) : input.StartsWith("data/common/", StringComparison.OrdinalIgnoreCase).ToString()))
            if (scope.GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).FirstOrDefault(group => group.Count() > 1) is { } duplicate)
                throw new InvalidDataException($"Multiple resource sources in the same scope are named {duplicate.Key}; resolve the ambiguity before opening the map.");
        List<string> diagnostics = [.. assembled.Warnings]; BoundedDiagnostics notes = new(diagnostics);
        Dictionary<string, SourceSceneResource> resources = new(StringComparer.OrdinalIgnoreCase);
        PreviewResourceBudget resourceBudget = new();
        foreach (string input in resourceInputs)
        {
            token.ThrowIfCancellationRequested();
            byte[] bytes = snapshot.Read(input, token, ProjectReadLimits.Resource());
            // Retain invalid resource identities: a broken authored variant must never select a fallback.
            ZrdTextSyntax? syntax = null; ZrdNode? tree = null;
            try { syntax = ZrdText.LooksLikeText(bytes) ? ZrdTextSyntax.Parse(bytes, token) : null; tree = syntax?.Root ?? ZrdDecoder.Read(bytes, token); }
            catch (InvalidDataException error) { notes.Add($"{input}: {error.Message}"); }
            if (tree != null) resourceBudget.Interpret(tree, token);
            snapshot.Retain(bytes.LongLength * 3);
            resources.TryAdd(Path.GetFileName(input), new(input, bytes, tree, syntax));
        }
        AnimationPackage? animations = null;
        AnimationSourceProvenance? animationProvenance = null;
        var definitions = SourceAnimationDefinitions.Empty;
        if (snapshot.Exists(SourceBuilder.AnimationRoot(mission)))
        {
            try
            {
                // The bindings say which definition produced each entry: the World tree lists definitions with what they bind to.
                var evaluated = AnimationCompiler.Evaluate(snapshot.Files(), SourceBuilder.AnimationRoot(mission),
                    assembled.World.Nodes.Select(n => n.Name).ToArray(), snapshot.Effects(mission, token), FormatRegistry.MaximumDocumentBytes, token, captureSourceBindings: true, game: snapshot.Game);
                if (evaluated.EngineRejection is { } rejected) notes.Add($"Animations: {rejected}");
                else
                {
                    if (evaluated.Provenance != null) snapshot.Retain(evaluated.Provenance.RetainedBytes);
                    animations = evaluated.Package;
                    animationProvenance = evaluated.Provenance;
                }
                foreach (var warning in evaluated.Warnings) notes.Add($"{warning}");
                if (evaluated.Definitions is { } set) definitions = SourceAnimationDefinitions.Summarize(set, evaluated.SourceBindings, evaluated.EngineRejection, token, snapshot.Game);
            }
            catch (Exception error) when (error is InvalidDataException && error is not SourceFileChangedException)
            {
                notes.Add($"Animation definitions are unavailable: {error.Message}");
                definitions = SourceAnimationDefinitions.Empty with { BindingKnown = false, Rejection = SourceAnimationDefinitions.Short(error.Message, 1024) };
            }
        }
        Dictionary<string, ScriptContent> scripts = new(StringComparer.OrdinalIgnoreCase);
        long scriptBytes = 0;
        var scriptInputs = SourceProject.Files(root, SourceProject.GameGenFolder, SourceSceneState.IsScript, snapshot.Added, token, snapshot.Inventory);
        foreach (string input in scriptInputs)
        {
            byte[] bytes = snapshot.Read(input, token, ProjectReadLimits.Text(SourceProject.MaximumSourceTextBytes - scriptBytes));
            scriptBytes += bytes.LongLength;
            var instructions = GameGenScriptText.TokenizeCancellable(GameGenScriptText.Decode(bytes, token), token).Select(row => row.ToArray()).ToArray();
            snapshot.Retain(bytes.LongLength * 8);
            scripts.Add(input[(SourceProject.GameGenFolder.Length + 1)..].Replace('/', '\\'), new ScriptContent(instructions, token));
        }
        var slots = GameZWriter.NodeSlots(assembled.World, token);
        var textureInputs = SourceBuilder.MissionTextures(root, mission, snapshot.Added, token, snapshot);
        var resolvedTextures = SourceBuilder.TextureInputs(textureInputs, mission, snapshot, token);
        Dictionary<string, string> textureFiles = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (input, name) in resolvedTextures)
            if (!textureFiles.TryAdd(name, input)) throw new InvalidDataException($"Multiple texture sources are named {name}.");
        Dictionary<string, SourceSceneTexture> textures = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, SourceSceneTexture> images = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, input) in textureFiles)
        {
            token.ThrowIfCancellationRequested();
            if (!images.TryGetValue(input, out var image))
            {
                byte[] bytes = snapshot.Read(input, token); snapshot.Retain(bytes.LongLength);
                if (TextureSources.PngSize((ReadOnlySpan<byte>)bytes) is not { } size)
                { notes.Add($"{input}: invalid PNG header; this texture is unavailable."); continue; }
                images.Add(input, image = new(input, SourceProject.Sha256(bytes), bytes, size.Width, size.Height));
            }
            textures.Add(name, image);
        }
        var soundInputs = SourceProject.Files(root, SourceBuilder.SoundsFolder, SourceSceneState.IsSound, snapshot.Added, token, snapshot.Inventory);
        Dictionary<string, byte[]> sounds = new(StringComparer.OrdinalIgnoreCase);
        foreach (string input in soundInputs)
        {
            byte[] bytes = snapshot.Read(input, token); snapshot.Retain(bytes.LongLength);
            resourceBudget.Member(bytes.LongLength, token);
            string name = Path.GetFileName(input);
            if (!sounds.TryAdd(name, bytes)) throw new InvalidDataException($"Multiple sound sources are named {name}.");
            try { _ = WaveDecoder.Read(bytes, token); }
            catch (InvalidDataException error) { sounds.Remove(name); notes.Add($"{input}: {error.Message}"); }
        }
        var provenance = assembled.Provenance.Where(pair => slots.ContainsKey(pair.Key)).ToDictionary(pair => slots[pair.Key], pair => pair.Value);
        IReadOnlyList<SourceLookup> lookups;
        try { lookups = SourceBuilder.MissionLookups(mission, snapshot, animations, token, assembled.World); }
        catch (Exception error) when (error is InvalidDataException && error is not SourceFileChangedException)
        { notes.Add($"Name lookup diagnostics are unavailable: {error.Message}"); lookups = []; }
        AssetDocument? nativeLibrary = null;
        string[]? libraryInputs = null;
        if (snapshot.Game == GameVariant.MechWarrior3)
        {
            libraryInputs = SourceGameProfile.LibraryModels(root, snapshot, token);
            var library = Mw3LibrarySources.EvaluateModels(libraryInputs, snapshot, token);
            snapshot.Retain(SourceExtractor.Footprint(library.World));
            nativeLibrary = library.SceneDocument(Path.Combine(root, "data/common/mechs/models"), token);
            foreach (var (name, input) in library.TextureFiles)
            {
                byte[] bytes = snapshot.Read(input, token);
                if (TextureSources.PngSize((ReadOnlySpan<byte>)bytes) is not { } size) continue;
                snapshot.Retain(bytes.LongLength);
                // The renderer resolves library materials against the merged texture table by name.
                textures.TryAdd(name, new(input, SourceProject.Sha256(bytes), bytes, size.Width, size.Height));
            }
        }
        snapshot.CheckUnchanged(token);
        InventoryBudget finalInventory = new();
        SourceBuilder.Snapshot discovery = new(root, overlay, inventoryToken: token);
        var now = SourceGameProfile.Resources(root, mission, discovery, sourceMission, token);
        if (!resourceInputs.SequenceEqual(now, StringComparer.OrdinalIgnoreCase)
            || !textureInputs.SequenceEqual(SourceBuilder.MissionTextures(root, mission, snapshot.Added, token, snapshot: discovery, inventory: finalInventory), StringComparer.OrdinalIgnoreCase)
            || !soundInputs.SequenceEqual(SourceProject.Files(root, SourceBuilder.SoundsFolder, SourceSceneState.IsSound, snapshot.Added, token, finalInventory), StringComparer.OrdinalIgnoreCase)
            || !scriptInputs.SequenceEqual(SourceProject.Files(root, SourceProject.GameGenFolder, SourceSceneState.IsScript, snapshot.Added, token, finalInventory), StringComparer.OrdinalIgnoreCase)
            || libraryInputs != null && !libraryInputs.SequenceEqual(SourceGameProfile.LibraryModels(root, discovery, token), StringComparer.OrdinalIgnoreCase))
            throw new SourceFileChangedException("Source files changed while loading the map; reload it.", []);
        SourceSceneSnapshot result = new(root, mission, snapshot.Game) { World = assembled.World, NativeLibrary = nativeLibrary, LibraryInputs = libraryInputs, SourceMission = sourceMission, Scene = scene, Resources = resources, ModelSources = snapshot.ModelSources, RetainedBytes = snapshot.RetainedBytes, TerrainModelInputs = assembled.TerrainModelInputs,
            WorldChildOrder = assembled.WorldChildOrder.ToDictionary(p => slots[p.Key], p => p.Value.Select(n => slots[n]).ToArray()),
            Sounds = sounds, TextureFiles = textureFiles, Textures = textures, TextureScripts = scripts, Animations = animations,
            Provenance = provenance, Freed = assembled.Provenance.Where(pair => !slots.ContainsKey(pair.Key) && (pair.Value.Applied.Count > 0 || pair.Value.Named.Count > 0)).Select(pair => pair.Value).ToArray(), AuthoredHierarchy = assembled.AuthoredHierarchy, Executions = assembled.Executions, WriteInstruction = assembled.WriteInstruction,
            Inputs = snapshot.Stamps(), ReadHashes = snapshot.ReadHashes(), InputHashes = snapshot.Hashes(), Dependencies = snapshot.Dependencies(),
            MissingInputs = snapshot.Missing(), MissingFolders = snapshot.MissingFolders(), ResourceInputs = resourceInputs, TextureInputs = textureInputs, SoundInputs = soundInputs, ScriptInputs = scriptInputs, Notes = diagnostics, Lookups = lookups,
            AnimationDefinitions = definitions, AnimationProvenance = animationProvenance };
        result.Assets.Add(new() { Id = new(result.Path, AssetKind.World, 0), Name = "Whole world", Content = scene });
        foreach (var model in scene.Models) result.Assets.Add(new() { Id = new(result.Path, AssetKind.Model, model.Index), Name = $"Model {model.Index}", Content = model, Metadata = model.Metadata });
        foreach (var node in scene.Nodes) result.Assets.Add(new() { Id = new(result.Path, AssetKind.Node, node.Index), Name = node.Name, Content = node, Metadata = node.Metadata });
        return result;
    }
}
