using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Recoil.Zbd.Core.Formats;
using Recoil.Zbd.Core.Gltf;
using Recoil.Zbd.Core.Terrain;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A database root kept out of a conversion, and why.</summary>
public sealed record TerrainConversionKept(string Node, string Reason);
/// <summary>A planned surface: its id, node attributes and the database roots (glTF node indices, in root order) it merges.</summary>
public sealed record TerrainConversionSurface(string Id, uint Flags, int Zone, IReadOnlyList<int> Nodes)
{
    internal int Run { get; init; }
    /// <summary>The pieces' model values (lighting, scrolling), which the surface's mesh carries.</summary>
    public JsonObject? ModelValues { get; init; }
    /// <summary>The pieces' Object3D appearance, including inactive alpha/color values and retained flags.</summary>
    public JsonObject? Appearance { get; init; }
}
/// <summary>What converting a mission database's pieces to editable terrain would do.</summary>
public sealed record TerrainConversionPlan(string Database, string Surfaces, string Recipe, IReadOnlyList<TerrainConversionSurface> Groups, IReadOnlyList<TerrainConversionKept> Kept)
{
    public int Converted => Groups.Sum(g => g.Nodes.Count);
    /// <summary>Ordinary recipes for uninterrupted sibling runs, in database encounter order.</summary>
    public IReadOnlyList<string> Recipes => Groups.Select(g => RecipeForRun(g.Run)).Distinct(StringComparer.Ordinal).ToArray();
    internal string RecipeForRun(int run)
    {
        if (run == 0) return Recipe;
        int folder = Recipe.LastIndexOf('/') + 1;
        string name = Recipe[folder..];
        // Import retains only the first twelve ASCII label characters. Put the distinguishing run before
        // the basename, with a leading letter different even case-insensitively from the first recipe's label.
        char first = name.FirstOrDefault(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        char prefix = first is 't' or 'T' or '\0' ? 'u' : 't';
        return Recipe[..folder] + prefix + (run + 1).ToString("D3", System.Globalization.CultureInfo.InvariantCulture) + "_" + name;
    }
}

/// <summary>
/// Convert to editable terrain: the mission database's untransformed mesh pieces become surfaces of a terrain recipe.
/// Adjacent pieces whose node attributes match merge into one surface, unless they overlap in plan view (the altitude probe
/// takes the first polygon of a node, so stacked sheets stay separate surfaces). Each surface keeps its pieces'
/// polygons, UVs, normals and materials (with their polygon zones and soils) exactly, together with their Object3D
/// appearance; the recipe gives it the pieces' exact node flags and zone. Pieces inside the database's groups (which the build deletes) count too; the parts
/// of the database in files of their own stay, since every copy of a part shares its file. Objects stay as they are:
/// the horizon and other landmarks, transformed nodes or nodes with children, references, shared nodes, and any piece
/// a script, resource or animation names (or matches by wildcard).
/// </summary>
public static partial class SourceTerrainConversion
{
    [GeneratedRegex(@"[A-Za-z0-9_\-\.\*%]+", RegexOptions.CultureInvariant)] private static partial Regex Word();
    /// <summary>
    /// The most distinct wildcard patterns the sources may hold (the releases' projects hold under 400 in all their files):
    /// planning matches every piece against each one.
    /// </summary>
    internal const int MaximumPatterns = 4096;
    /// <summary>
    /// The most distinct names, and characters of them, the sources may hold, all of which planning keeps: all the sources
    /// of the 1998 or 1999 release together name under 76,000 in under 700,000 characters.
    /// </summary>
    internal const int MaximumNames = 262_144, MaximumNameCharacters = 4 * 1024 * 1024;
    /// <summary>The most characters the distinct wildcard patterns may hold together; each becomes a regular expression.</summary>
    internal const int MaximumPatternCharacters = 256 * 1024;

    /// <summary>
    /// Names and wildcard patterns (each * one digit) the given sources mention. A text source larger than the text-source
    /// limit (<see cref="SourceProject.MaximumSourceTextBytes"/>) is refused before it is decoded; a compiled resource is read
    /// as its decoded strings, never as text. Sources naming more than <see cref="MaximumNames"/> distinct words (or
    /// <see cref="MaximumNameCharacters"/> characters of them) are refused before the name past the limit is kept.
    /// </summary>
    public static (HashSet<string> Names, IReadOnlyList<Regex> Patterns) References(SourceWorkspace workspace, IEnumerable<string> files, CancellationToken token = default)
        => ReferencesCore(workspace, files, null, token);

    internal static (HashSet<string> Names, IReadOnlyList<Regex> Patterns) References(SourceWorkspace workspace, IEnumerable<string> files,
        CancellationToken token, long maximumInputBytes)
        => ReferencesCore(workspace, files, null, token, maximumInputBytes);

    /// <summary>Includes the executed operands of the exact world entry script, with shared macros across sourced files.</summary>
    public static (HashSet<string> Names, IReadOnlyList<Regex> Patterns) References(SourceWorkspace workspace, IEnumerable<string> files,
        string entryScript, CancellationToken token = default) => ReferencesCore(workspace, files, entryScript, token);

    internal static (HashSet<string> Names, IReadOnlyList<Regex> Patterns) References(SourceWorkspace workspace, IEnumerable<string> files,
        string entryScript, CancellationToken token, long maximumInputBytes) => ReferencesCore(workspace, files, entryScript, token, maximumInputBytes);

    private static (HashSet<string> Names, IReadOnlyList<Regex> Patterns) ReferencesCore(SourceWorkspace workspace,
        IEnumerable<string> files, string? entryScript, CancellationToken token,
        long maximumInputBytes = FormatRegistry.MaximumDocumentBytes)
    {
        if (maximumInputBytes is < 0 or > FormatRegistry.MaximumDocumentBytes) throw new ArgumentOutOfRangeException(nameof(maximumInputBytes));
        HashSet<string> names = new(StringComparer.Ordinal), wildcards = new(StringComparer.Ordinal); List<Regex> patterns = [];
        // Looked up by span, so a word already kept (most of them) is not copied again.
        var knownNames = names.GetAlternateLookup<ReadOnlySpan<char>>();
        var knownWildcards = wildcards.GetAlternateLookup<ReadOnlySpan<char>>();
        long nameCharacters = 0, patternCharacters = 0;
        Dictionary<string, IReadOnlyList<IReadOnlyList<string>>> scripts = new(StringComparer.OrdinalIgnoreCase);
        HashSet<string> needsContext = new(StringComparer.OrdinalIgnoreCase);
        long inputBytes = 0, scriptBytes = 0, scriptTokens = 0, operandWork = 0;
        HashSet<string> inspected = new(StringComparer.OrdinalIgnoreCase);
        string reading = "";
        foreach (string input in files)
        {
            token.ThrowIfCancellationRequested();
            string file = SourceWorkspace.Normalize(input);
            if (!inspected.Add(file)) continue;
            reading = file;
            bool resource = file.EndsWith(".zrd", StringComparison.OrdinalIgnoreCase) || file.EndsWith(Animation.AnimationDefinitionSet.Extension, StringComparison.OrdinalIgnoreCase);
            bool keyframes = file.EndsWith(".zan", StringComparison.OrdinalIgnoreCase);
            if (!(resource || keyframes || file.EndsWith(".gs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".gw", StringComparison.OrdinalIgnoreCase))) continue;
            var limits = resource ? ProjectReadLimits.Resource() : ProjectReadLimits.Text(keyframes
                ? SourceProject.MaximumSourceTextBytes : WorldAssembler.MaximumScriptSourceBytes - scriptBytes);
            if (ReadInput(file, limits) is not { } bytes) continue;
            // Every word, and also a resource's strings whole (names may hold spaces) and a script's tokens (names may hold
            // other characters): more names only keep more pieces as objects.
            if (resource && !ZrdText.LooksLikeText(bytes))
            {
                // Compiled zReader data names what its strings hold, as its text form would: their words in order, then each whole.
                ZrdNode tree;
                try { tree = ZrdDecoder.Read(bytes, token); }
                catch (InvalidDataException ex)
                {
                    // Not readable as zReader data: its words, as for any text, within the same limit.
                    if (bytes.Length > SourceProject.MaximumSourceTextBytes) throw new InvalidDataException($"{file} is not valid zReader data ({ex.Message}), so the names it holds are unknown.", ex);
                    Words(GameGenScriptText.Decode(bytes, token));
                    continue;
                }
                foreach (string text in InOrder(tree)) { token.ThrowIfCancellationRequested(); Words(text); }
                foreach (string text in InOrder(tree)) Add(text);
                continue;
            }
            if (bytes.Length > SourceProject.MaximumSourceTextBytes)
                throw new InvalidDataException($"{file} exceeds {SourceProject.MaximumSourceTextBytes / (1024 * 1024)} MiB, the most a text source may hold, so the names it mentions are unknown.");
            string source = GameGenScriptText.Decode(bytes, token);
            Words(source);
            if (resource)
            {
                ZrdNode tree;
                try { tree = ZrdText.Parse(source, token); }
                catch (InvalidDataException) { continue; }
                // Only malformed text falls back to its words. A decoded reference exceeding a budget must refuse
                // the whole conversion, or a named object could be converted from an incomplete protection set.
                foreach (string text in InOrder(tree)) Add(text);
            }
            else if (!keyframes)
            {
                var lines = ReadScript(file, bytes, source);
                foreach (var line in lines)
                {
                    foreach (string t in line) Add(t);
                }
            }
        }
        if (entryScript != null)
        {
            string entry = SourceWorkspace.Normalize(entryScript);
            if (!entry.StartsWith("gamegen/", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Terrain reference discovery needs the world's entry script under gamegen/.");
            ScriptTraceBudget traceBudget = new();
            ScriptTrace.Visit(LoadScript, entry["gamegen/".Length..], Executed, token, stopAtWorldWrite: false, budget: traceBudget);
            // A mission's archive-mode load script is a separate, known entry point. Its macros do not inherit
            // the build's state. Dependencies also contain the whole interp.zbd inventory, including other
            // missions and unused fragments: those retain literal protection above, but are not execution
            // dependencies of this world. Only these roots and the scripts they source provide macro context.
            string runtime = WorldLookups.LoadScript(Path.GetFileNameWithoutExtension(entry));
            if (scripts.ContainsKey(runtime))
                ScriptTrace.Visit(LoadScript, runtime["gamegen/".Length..], Executed, token, stopAtWorldWrite: false, budget: traceBudget);
        }
        else if (needsContext.Count > 0)
            throw new InvalidDataException("Terrain reference discovery needs the world's entry script to resolve macros and sourced scripts; terrain conversion is refused without that execution context.");
        return (names, patterns);

        void Executed(TracedInstruction instruction)
        {
            reading = "gamegen/" + instruction.Script.Replace('\\', '/');
            foreach (string operand in instruction.Args)
            {
                // The trace streams records, so repeated operands cannot retain histories. Charge repeated
                // hashing/matching too: a long literal can be executed many times from one cached source.
                if (operand.Length + 1L > WorldAssembler.MaximumScriptSourceBytes - operandWork)
                    throw new InvalidDataException("Terrain reference discovery exceeds its executed-operand work budget; simplify the world scripts before converting.");
                operandWork += operand.Length + 1L;
                Add(operand);
            }
        }

        IReadOnlyList<IReadOnlyList<string>> LoadScript(string name)
        {
            token.ThrowIfCancellationRequested();
            string file = "gamegen/" + name.Replace('\\', '/');
            if (scripts.TryGetValue(file, out var cached)) return cached;
            byte[] bytes = ReadInput(file, ProjectReadLimits.Text(WorldAssembler.MaximumScriptSourceBytes - scriptBytes))
                ?? throw new InvalidDataException($"The script {JsonData.ShownText(file)} is unavailable; terrain references cannot be established, so conversion is refused.");
            return ReadScript(file, bytes, GameGenScriptText.Decode(bytes, token));
        }
        byte[]? ReadInput(string file, ProjectReadLimits limits)
        {
            // Name retention cannot bound files containing only whitespace or repeated words. Charge every actual
            // read before it allocates/decodes, including compiled resources and sourced scripts outside the inventory.
            byte[]? bytes;
            try { bytes = workspace.Read(file, token, limits.WithMaximum(maximumInputBytes - inputBytes)); }
            catch (InvalidDataException ex)
            { throw new InvalidDataException($"{file}: terrain reference inputs cannot be read within their per-file and combined {maximumInputBytes:N0}-byte allowance: {ex.Message}", ex); }
            if (bytes != null) inputBytes += bytes.LongLength;
            return bytes;
        }
        IReadOnlyList<IReadOnlyList<string>> ReadScript(string file, byte[] bytes, string source)
        {
            if (scripts.TryGetValue(file, out var cached)) return cached;
            if (bytes.LongLength > WorldAssembler.MaximumScriptSourceBytes - scriptBytes)
                throw new InvalidDataException("Terrain reference discovery's script sources exceed 64 MiB; simplify the world scripts before converting.");
            scriptBytes += bytes.Length;
            if ((scriptTokens += GameGenScriptText.CountTokens(source, token)) > GameGenScriptText.MaximumTokens)
                throw new InvalidDataException("Terrain reference discovery's script sources exceed four million tokens.");
            var lines = GameGenScriptText.TokenizeCancellable(source, token);
            if (NeedsContext(lines)) needsContext.Add(file);
            scripts.Add(file, lines);
            return lines;
        }

        bool NeedsContext(IReadOnlyList<IReadOnlyList<string>> lines)
        {
            foreach (var line in lines)
            {
                token.ThrowIfCancellationRequested();
                if (ScriptConditions.IsSource(line[0])) return true;
                foreach (string operand in line) { token.ThrowIfCancellationRequested(); if (ScriptConditions.HasMacro(operand)) return true; }
            }
            token.ThrowIfCancellationRequested();
            return false;
        }

        void Words(string text) { foreach (var m in Word().EnumerateMatches(text)) Add(text.AsSpan(m.Index, m.Length)); }
        // Each distinct name and pattern once, within the limits.
        void Add(ReadOnlySpan<char> t)
        {
            token.ThrowIfCancellationRequested();
            if (!t.Contains('*'))
            {
                if (knownNames.Contains(t)) return;
                if (names.Count >= MaximumNames || (nameCharacters += t.Length) > MaximumNameCharacters)
                    throw new InvalidDataException($"The sources this world reads name more than {MaximumNames:N0} different words, or more than {MaximumNameCharacters:N0} characters of them (the limit was passed in {reading}); terrain conversion keeps each to leave the pieces they name as objects, so it is refused.");
                names.Add(t.ToString());
                return;
            }
            if (knownWildcards.Contains(t)) return;
            if (wildcards.Count >= MaximumPatterns)
                throw new InvalidDataException($"The sources hold more than {MaximumPatterns:N0} wildcard patterns, and every piece would be matched against each; terrain conversion is refused.");
            if ((patternCharacters += t.Length) > MaximumPatternCharacters)
                throw new InvalidDataException($"The sources' wildcard patterns hold more than {MaximumPatternCharacters:N0} characters (the limit was passed in {reading}), and each becomes an expression every piece is matched against; terrain conversion is refused.");
            string pattern = t.ToString();
            wildcards.Add(pattern);
            patterns.Add(new Regex("^" + Regex.Escape(pattern).Replace("\\*", "[0-9]") + "$", RegexOptions.CultureInvariant));
        }
        // The tree's strings in the order its source lists them, holding only the path to each (a compiled tree may be wide).
        static IEnumerable<string> InOrder(ZrdNode root)
        {
            if (root.Kind == ZrdKind.String) { yield return root.Text; yield break; }
            Stack<(ZrdNode Node, int Next)> path = new([(root, 0)]);
            while (path.TryPop(out var at))
            {
                if (at.Next >= at.Node.Children.Count) continue;
                path.Push((at.Node, at.Next + 1));
                var child = at.Node.Children[at.Next];
                if (child.Kind == ZrdKind.String) yield return child.Text;
                else if (child.Children.Count > 0) path.Push((child, 0));
            }
        }
    }

    /// <summary>Plans converting <paramref name="database"/>'s pieces; names in <paramref name="references"/> stay objects.</summary>
    public static TerrainConversionPlan Plan(SourceWorkspace workspace, string database, (HashSet<string> Names, IReadOnlyList<Regex> Patterns) references, CancellationToken token = default)
        => Plan(workspace, database, references, TerrainConversionSerializationBudget.Limits.Default, token);

    internal static TerrainConversionPlan Plan(SourceWorkspace workspace, string database, (HashSet<string> Names, IReadOnlyList<Regex> Patterns) references,
        TerrainConversionSerializationBudget.Limits limits, CancellationToken token)
    {
        var (root, doc) = Read(workspace, database, token);
        ConversionZones.Read(workspace, database, root, doc, token)?.ApplyPlanningValues(doc, token);
        TerrainConversionGeometry geometry = new(token);
        long patternWork = 0;
        Regex? MatchingPattern(string name)
        {
            foreach (var pattern in references.Patterns)
            {
                token.ThrowIfCancellationRequested();
                // Charge text length too; even fixed-form patterns can be long.
                if ((patternWork += 1L + name.Length + pattern.ToString().Length) > 16_000_000)
                    throw new InvalidDataException("Terrain conversion exceeds its wildcard matching budget; reduce the nodes or wildcard references.");
                if (pattern.IsMatch(name)) return pattern;
            }
            return null;
        }
        string stem = database[(database.LastIndexOf('/') + 1)..database.LastIndexOf('.')];
        string folder = database[..database.LastIndexOf('/')];
        string surfaces = $"{folder}/{stem}_terrain.gltf", recipe = $"{folder}/{stem}_terrain{TerrainRecipe.Extension}", buffer = $"{folder}/{stem}_terrain.bin";
        if (workspace.Exists(surfaces, token) || workspace.Exists(recipe, token) || workspace.Exists(buffer, token)) throw new InvalidDataException($"{surfaces}, {buffer} or {recipe} already exists; the database was converted before.");
        List<TerrainConversionKept> kept = []; HashSet<string> shared = new(StringComparer.Ordinal);
        // Many pieces may share one mesh (a valid file may hold 200,000 nodes using one), and its model values may be large:
        // what a mesh decides is worked out once per mesh, and its values are written out once, as a small number naming them
        // in the group keys (the same written values, the same number), never once per piece.
        Dictionary<GltfMesh, MeshFacts> facts = new(ReferenceEqualityComparer.Instance);
        Dictionary<string, int> valueNumbers = new(StringComparer.Ordinal); List<string> valueTexts = [];
        Dictionary<string, int> appearanceNumbers = new(StringComparer.Ordinal) { [""] = 0 };
        List<JsonObject?> appearances = [null];
        List<((uint Flags, int Zone, int Values, int Appearance) Key, int Run, List<GltfNode> Nodes, HashSet<TerrainConversionGeometry.Sheet> Areas)> groups = [];
        int run = -1; bool continuing = false;
        foreach (var (node, inherited) in Members(doc.Roots, 0xFF))
        {
            token.ThrowIfCancellationRequested();
            if (node == null) { continuing = false; continue; }
            var extras = node.Extras?[WorldGltf.Key] as JsonObject;
            // A piece without a zone of its own takes its group's, as the importer gives it.
            int zone = Zone(extras, inherited);
            string name = WorldGltf.EngineName(node);
            MeshFacts? mesh = node.Mesh == null ? null : facts.TryGetValue(node.Mesh, out var known) ? known : facts[node.Mesh] = new(node.Mesh);
            string? reason = extras?["terrain"] != null ? "terrain marker"
                : mesh == null ? "no mesh of its own"
                : node.Children.Count > 0 ? "has children"
                : extras?["ref"] != null || extras?[WorldGltf.ZoneReference] != null ? "references a model file"
                : extras?["instance"] != null ? "shared by several parents"
                : extras?["class"] is JsonValue c && c.ToString() == "lod" ? "level-of-detail node"
                : node.Matrix is { } m && !m.IsIdentity ? "placed with a transform"
                : mesh.Morphs ? "has morph targets"
                : (Flags(extras) & 0x80) != 0 ? "landmark (the horizon and other always-drawn nodes)"
                : (Flags(extras) & 0x60) != 0 ? "collides by its bounding box or is a proximity node (both depend on the node's own bounds)"
                : mesh.ValuesReason is { } byValues ? byValues
                : references.Names.Contains(name) ? "named by a script, resource or animation"
                : MatchingPattern(name) is { } pattern ? $"matched by the wildcard {pattern}"
                : extras?["zoneWord"] is JsonValue word && word.ToString() is var w && zone is var z && !w.Equals($"0x{(uint)z:X}", StringComparison.OrdinalIgnoreCase) && !w.Equals($"0x{(uint)z:X8}", StringComparison.OrdinalIgnoreCase) ? "has a zone word beyond its zone"
                : null;
            if (reason != null) { kept.Add(new(name, reason)); continuing = false; continue; }
            if (mesh!.ValuesNumber is not int number)
            {
                string text = mesh.Values?.ToJsonString() ?? "";
                if (!valueNumbers.TryGetValue(text, out number)) { valueNumbers[text] = number = valueTexts.Count; valueTexts.Add(text); }
                mesh.ValuesNumber = number;
            }
            _ = WorldGltf.Appearance(extras, database);
            var appearance = extras?["appearance"] as JsonObject;
            string appearanceText = appearance?.ToJsonString() ?? "";
            if (!appearanceNumbers.TryGetValue(appearanceText, out int appearanceNumber))
            {
                appearanceNumbers[appearanceText] = appearanceNumber = appearances.Count;
                appearances.Add(appearance);
            }
            var key = (Flags(extras), zone, number, appearanceNumber);
            if (!continuing) { run++; continuing = true; }
            var area = mesh.Area ??= geometry.Read(node.Mesh!);
            // Only the immediately preceding surface can absorb this piece. Reusing an earlier surface would
            // move its polygons ahead of an intervening sheet, attribute group, or retained object.
            if (groups.Count > 0 && groups[^1].Run == run && groups[^1].Key == key
                && !groups[^1].Areas.Any(other => geometry.Overlaps(other, area)))
            { groups[^1].Nodes.Add(node); groups[^1].Areas.Add(area); }
            else
            {
                if (groups.Count >= TerrainRecipe.MaximumSurfaces)
                    throw new InvalidDataException($"The pieces need more than {TerrainRecipe.MaximumSurfaces} ordered terrain surfaces. Convert a smaller database.");
                groups.Add((key, run, [node], new(ReferenceEqualityComparer.Instance) { area }));
            }
        }
        var totals = groups.GroupBy(g => g.Key).ToDictionary(g => g.Key, g => g.Count());
        Dictionary<(uint Flags, int Zone, int Values, int Appearance), int> occurrences = [];
        Dictionary<int, (string Suffix, JsonObject? Values)> writtenValues = [];
        List<TerrainConversionSurface> result = [];
        foreach (var group in groups)
        {
            var (flags, zone, number, appearanceNumber) = group.Key;
            // Pieces with model values of their own (an unlit or scrolling surface) are a surface of their own, named by a short hash of the values.
            // Repeated, separated runs reuse this parsed metadata before expanded output admission.
            if (!writtenValues.TryGetValue(number, out var written))
            {
                string values = valueTexts[number];
                writtenValues[number] = written = (values.Length == 0 ? "" : "_m" + SourceProject.Sha256(Encoding.UTF8.GetBytes(values))[..6],
                    values.Length == 0 ? null : JsonNode.Parse(values) as JsonObject);
            }
            int occurrence = occurrences[group.Key] = occurrences.GetValueOrDefault(group.Key) + 1;
            result.Add(new($"z{(zone == 0xFF ? "any" : zone.ToString(System.Globalization.CultureInfo.InvariantCulture))}_{flags:x8}{written.Suffix}" + (appearanceNumber == 0 ? "" : $"_a{appearanceNumber}") + (totals[group.Key] > 1 ? $"_{occurrence}" : ""), flags, zone, [.. group.Nodes.Select(n => n.Index)])
            { ModelValues = written.Values, Appearance = appearances[appearanceNumber], Run = group.Run });
        }
        TerrainConversionPlan plan = new(database, surfaces, recipe, result, kept);
        foreach (string output in plan.Recipes)
            if (workspace.Exists(output, token)) throw new InvalidDataException($"{output} already exists; choose an unconverted database.");
        TerrainConversionSerializationBudget.Check(plan, doc.AllNodes().Where(n => n.Index >= 0).ToDictionary(n => n.Index), limits, token);
        return plan;

        // The database's objects: its roots and, in their place, what its groups hold (a part's reference holds the
        // database's own records; the part's objects are in its file), with the zone each inherits (the roots' 0xFF).
        IEnumerable<(GltfNode? Node, int Zone)> Members(IEnumerable<GltfNode> nodes, int inherited)
        {
            foreach (var node in nodes)
            {
                if (!WorldGltf.IsGroup(node, database)) { yield return (node, inherited); continue; }
                yield return (null, inherited); // A new parent is a new run, even when adjacent leaves match.
                var extras = node.Extras?[WorldGltf.Key] as JsonObject;
                // A group several parents share is written once per parent and imported from its first copy alone: its
                // pieces would be converted once per copy, and the copies would no longer agree.
                if (extras?["instance"] != null) { if (shared.Add(extras["instance"]!.ToJsonString())) kept.Add(new(WorldGltf.EngineName(node), "a group several parents share")); continue; }
                if (extras?["ref"] != null || extras?[WorldGltf.ZoneReference] != null)
                { kept.Add(new(WorldGltf.EngineName(node), "a part of the mission database in a file of its own")); continue; }
                if (!WorldGltf.IsTransparentTerrainGroup(node, database))
                { kept.Add(new(WorldGltf.EngineName(node), "a database group with geometry, transform or appearance")); continue; }
                foreach (var member in Members(node.Children, Zone(extras, inherited))) yield return member;
                yield return (null, inherited);
            }
        }
    }

    /// <summary>Applies the shared surfaces and ordinary recipes as one change, with each marker at its run's original position.</summary>
    public static SourceTransaction Apply(SourceWorkspace workspace, TerrainConversionPlan plan, CancellationToken token = default)
        => Apply(workspace, plan, TerrainConversionSerializationBudget.Limits.Default, token);

    internal static SourceTransaction Apply(SourceWorkspace workspace, TerrainConversionPlan plan, TerrainConversionSerializationBudget.Limits limits, CancellationToken token)
    {
        if (plan.Groups.Count == 0) throw new InvalidDataException("No piece of the mission database can become terrain.");
        var (root, doc) = Read(workspace, plan.Database, token);
        var zones = ConversionZones.Read(workspace, plan.Database, root, doc, token);
        var byIndex = doc.AllNodes().Where(n => n.Index >= 0).ToDictionary(n => n.Index);
        TerrainConversionSerializationBudget.Check(plan, byIndex, limits, token);
        // The surfaces: one node per surface whose mesh holds its pieces' primitives as they were.
        GltfDocument surfaces = new();
        foreach (var group in plan.Groups)
        {
            GltfMesh mesh = new() { Name = group.Id, Extras = group.ModelValues == null ? null : new JsonObject { [WorldGltf.Key] = group.ModelValues.DeepClone() } };
            foreach (int index in group.Nodes)
            {
                if (!byIndex.TryGetValue(index, out var node) || node.Mesh == null) throw new InvalidDataException($"{plan.Database} changed since the conversion was planned.");
                mesh.Primitives.AddRange(node.Mesh.Primitives);
            }
            surfaces.Roots.Add(new GltfNode { Name = group.Id, Mesh = mesh,
                Extras = group.Appearance == null ? null : new JsonObject { [WorldGltf.Key] = new JsonObject { ["appearance"] = group.Appearance.DeepClone() } } });
        }
        string binary = plan.Surfaces[(plan.Surfaces.LastIndexOf('/') + 1)..^".gltf".Length] + ".bin";
        var (json, bin) = surfaces.Write(binary, token);
        _ = GltfDocument.Read(json, uri => uri == binary ? bin
            : throw new InvalidDataException($"{plan.Surfaces}: unexpected buffer {JsonData.ShownText(uri)}."), token);
        string folder = plan.Surfaces[..plan.Surfaces.LastIndexOf('/')];
        // Each uninterrupted sibling run gets a normal recipe: adding or renaming its surfaces later needs no
        // hidden selector in the database. All recipes share the one generated surfaces model and zone profile.
        List<(string, byte[]?)> changes = [(plan.Surfaces, json), ($"{folder}/{binary}", bin)];
        Dictionary<int, int> nodeRuns = [];
        foreach (var group in plan.Groups)
            foreach (int index in group.Nodes)
                if (!nodeRuns.TryAdd(index, group.Run)) throw new InvalidDataException("The conversion plan repeats a piece.");
        foreach (var run in plan.Groups.GroupBy(g => g.Run))
        {
            token.ThrowIfCancellationRequested();
            string path = plan.RecipeForRun(run.Key);
            if (workspace.Exists(path, token)) throw new InvalidDataException($"{path} already exists; plan the conversion again.");
            TerrainRecipe recipe = new(TerrainRecipe.CurrentCompiler,
                [.. run.Select(g => new TerrainSurface(g.Id, SourceTerrain.RelativePath(path, plan.Surfaces), g.Id,
                    new() { Flags = g.Flags & WorldGltf.CarriedFlags, NodeZone = g.Zone == 0xFF ? TerrainAttributes.AnyZone : g.Zone }))],
                TerrainAttributes.None, []);
            changes.Add((path, TerrainRecipe.Parse(recipe.Write(), path).Write()));
        }
        // Rewrite each adjacency list once before removal renumbers it. A marker replaces exactly its original
        // contiguous run; retained roots and group children keep their relative encounter positions.
        var nodes = (JsonArray)root["nodes"]!;
        var scenes = (JsonArray)root["scenes"]!;
        int sceneIndex = GltfInteger.OptionalInt32(root["scene"], "scene") ?? 0;
        HashSet<int> placed = []; List<int> encountered = [];
        Rewrite((JsonObject)scenes[sceneIndex]!, "nodes", doc.Roots, true);
        if (!encountered.SequenceEqual(plan.Groups.SelectMany(g => g.Nodes)))
            throw new InvalidDataException("The conversion plan no longer follows the database's piece order. Plan the conversion again.");
        GltfNodeEdits.Remove(root, nodeRuns.Keys.ToHashSet(), token);
        byte[] database = GltfJson.Write(root, indented: false, token);
        if (database.Length > SourceTerrain.MaximumDatabaseJsonBytes)
            throw new InvalidDataException("The converted database exceeds its editable JSON limit. Convert a smaller database.");
        var updated = WorldAssembler.ReadModel(database, plan.Database, (path, remaining) => workspace.Read(path, token, ProjectReadLimits.Bytes(remaining))
            ?? throw new InvalidDataException($"{plan.Database}: buffer {JsonData.ShownText(path)} is unavailable."), token);
        changes.Add((plan.Database, database));
        if (zones != null) changes.Add(zones.Update(root, updated, plan.Surfaces, surfaces, plan.Groups, byIndex, token));
        return workspace.Apply($"Convert {Path.GetFileName(plan.Database)} to editable terrain",
            changes, token)
            ?? throw new InvalidDataException("The conversion changed nothing.");

        void Rewrite(JsonObject owner, string property, IReadOnlyList<GltfNode> siblings, bool allowed)
        {
            JsonArray rewritten = []; int? previousRun = null;
            foreach (var node in siblings)
            {
                token.ThrowIfCancellationRequested();
                if (nodeRuns.TryGetValue(node.Index, out int run))
                {
                    if (!allowed) throw new InvalidDataException("Terrain pieces must be roots or children of transparent database groups.");
                    encountered.Add(node.Index);
                    if (previousRun != run)
                    {
                        if (!placed.Add(run)) throw new InvalidDataException("A conversion recipe crosses a retained piece or group. Plan the conversion again.");
                        string path = plan.RecipeForRun(run), stem = Path.GetFileName(path)[..^TerrainRecipe.Extension.Length];
                        // Markers never become game nodes, but pass the shared source-name validator. The full
                        // recipe identity stays in the URI when a long/non-Latin-1 basename needs a display name.
                        if (!WorldGltf.IsEngineName(stem)) stem = $"terrain_{run + 1}";
                        rewritten.Add(nodes.Count);
                        nodes.Add(new JsonObject { ["name"] = stem, ["extras"] = new JsonObject { [WorldGltf.Key] = new JsonObject { ["terrain"] = SourceTerrain.RelativePath(plan.Database, path) } } });
                    }
                    previousRun = run;
                }
                else
                {
                    previousRun = null; rewritten.Add(node.Index);
                    if (node.Children.Count > 0)
                        Rewrite((JsonObject)nodes[node.Index]!, "children", node.Children, allowed && WorldGltf.IsTransparentTerrainGroup(node, plan.Database));
                }
            }
            owner[property] = rewritten;
        }
    }

    private static (JsonObject Root, GltfDocument Doc) Read(SourceWorkspace workspace, string database, CancellationToken token)
    {
        database = SourceWorkspace.Normalize(database);
        if (!database.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{database} must be a .gltf file to convert.");
        byte[] bytes = workspace.Read(database, token, SourceTerrain.MaximumDatabaseJsonBytes) ?? throw new InvalidDataException($"The project has no {database}.");
        var doc = WorldAssembler.ReadModel(bytes, database, (path, remaining) => workspace.Read(path, token, ProjectReadLimits.Bytes(remaining))
            ?? throw new InvalidDataException($"{database}: buffer {JsonData.ShownText(path)} is unavailable."), token);
        JsonObject root;
        try { root = JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 256 }) as JsonObject ?? throw new InvalidDataException($"{database} is not a JSON object."); }
        catch (JsonException ex) { throw new InvalidDataException($"{database} is not valid JSON: {ex.Message}", ex); }
        // Match the shared reader's implicit-root semantics before the editor removes/renumbers nodes.
        if (root["scenes"] is not JsonArray { Count: > 0 })
        {
            root["scene"] = 0;
            root["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(doc.Roots.Select(n => (JsonNode?)JsonValue.Create(n.Index)).ToArray()) });
        }
        else
        {
            int scene = GltfInteger.OptionalInt32(root["scene"], "scene") ?? 0;
            root["scenes"]![scene]!["nodes"] ??= new JsonArray();
        }
        return (root, doc);
    }
    private static uint Flags(JsonObject? extras) =>
        extras?["flags"] is JsonValue v && v.TryGetValue(out string? hex)
            && uint.TryParse(hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.AsSpan(2) : hex.AsSpan(), System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out uint flags) ? flags & WorldGltf.CarriedFlags : WorldGltf.DefaultCarried;
    /// <summary>A node's zone as the importer reads it (<see cref="WorldGltf.StatedZone"/>); without one, <paramref name="inherited"/>.</summary>
    private static int Zone(JsonObject? extras, int inherited = 0xFF) => WorldGltf.StatedZone(extras) is { } zone ? (int)zone : inherited;
    /// <summary>
    /// What planning reads of a mesh, the same for every piece that uses it: whether it has morph targets, the reason its model
    /// values keep it an object (point entries, a facade or point mode), and, once known, the number of its written values in
    /// the plan's grouping and its plan-view area.
    /// </summary>
    private sealed class MeshFacts(GltfMesh mesh)
    {
        public JsonObject? Values { get; } = mesh.Extras?[WorldGltf.Key] as JsonObject;
        public bool Morphs { get; } = mesh.Weights.Count > 0 || mesh.Primitives.Any(p => p.Targets.Count > 0);
        public string? ValuesReason { get; } = Reason(mesh.Extras?[WorldGltf.Key] as JsonObject);
        public int? ValuesNumber { get; set; }
        public TerrainConversionGeometry.Sheet? Area { get; set; }
        private static string? Reason(JsonObject? values) =>
            values?["points"] is JsonArray { Count: > 0 } ? "holds point entries (lens flares)"
            : values?["mode"] is JsonValue mode && mode.ToString() != "0" ? "a facade or point model"
            : null;
    }

}
