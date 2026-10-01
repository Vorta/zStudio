using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Recoil.Zbd.Core.Worlds;

namespace Recoil.Zbd.Core.Sources;

/// <summary>A world object's local transform: position, rotation in degrees as Object3DRotate takes it (about Y, then X, then Z) and scale.</summary>
public readonly record struct ObjectTransform(Vector3 Position, Vector3 RotationDegrees, Vector3 Scale)
{
    public static ObjectTransform Identity => new(Vector3.Zero, Vector3.Zero, Vector3.One);
    /// <summary>The engine's local matrix for this transform (MatApplyLocalTRS: R = Ry·Rx·Rz, scaled rows, then the translation).</summary>
    public Matrix4x4 Matrix()
    {
        Vector3 r = RotationDegrees * (MathF.PI / 180f);
        var m = Matrix4x4.CreateScale(Scale) * Matrix4x4.CreateRotationZ(r.Z) * Matrix4x4.CreateRotationX(r.X) * Matrix4x4.CreateRotationY(r.Y);
        m.Translation = Position;
        return m;
    }
    /// <summary>The transform a local matrix (rows: rotated, scaled axes, then translation) describes.</summary>
    public static ObjectTransform FromMatrix(Matrix4x4 m)
    {
        Vector3 x = new(m.M11, m.M12, m.M13), y = new(m.M21, m.M22, m.M23), z = new(m.M31, m.M32, m.M33);
        Vector3 scale = new(x.Length(), y.Length(), z.Length());
        if (scale.X == 0 || scale.Y == 0 || scale.Z == 0) return new(m.Translation, Vector3.Zero, scale);
        x /= scale.X; y /= scale.Y; z /= scale.Z;
        // Rows of Rz·Rx·Ry: M32 = −sin x, M31/M33 give y, M12/M22 give z (gimbal lock: z is 0).
        float rx = MathF.Asin(Math.Clamp(-z.Y, -1f, 1f)), ry, rz;
        if (MathF.Abs(MathF.Cos(rx)) > 1e-6f) { ry = MathF.Atan2(z.X, z.Z); rz = MathF.Atan2(x.Y, y.Y); }
        else { rz = 0; ry = MathF.Atan2(-x.Z, x.X); }
        const float degrees = 180f / MathF.PI;
        return new(m.Translation, new Vector3(rx, ry, rz) * degrees, scale);
    }
}

/// <summary>A planned change of a project's sources: the label for history, the files and their new content, what it edits and notes on its reach.</summary>
public sealed record SourceEditPlan(string Label, IReadOnlyList<(string Relative, byte[] Content)> Changes, string Target, IReadOnlyList<string> Notes);

/// <summary>
/// Plans edits of a world's objects as changes of the sources that made them (see <see cref="WorldNodeProvenance"/>): a
/// value a script instruction set is changed in that instruction, a node imported from a glTF file in that file, and a
/// value nothing set yet is added by a new instruction right after the one that created the node. An instruction that
/// ran more than once, or whose value comes from a macro, cannot be edited for one object and is refused.
/// </summary>
public static class SourceObjectEdits
{
    private static readonly string[] TransformCommands = ["Object3DTranslate", "Object3DRotate", "Object3DScale"];
    /// <summary>Node flag bits and the script command that sets each.</summary>
    public static readonly IReadOnlyDictionary<uint, string> FlagCommands = new Dictionary<uint, string>
    {
        [0x08] = "SetAltitudeSurface", [0x10] = "SetIntersectSurface", [0x20] = "SetIntersectBBOX", [0x40] = "SetProximity", [0x80] = "SetLandmark",
        [0x10000] = "NodeSetCanModify", [0x800000] = "NodeSetOverwrite",
    };

    /// <summary>A plan that moves, rotates or scales an object to <paramref name="requested"/> (its local transform).</summary>
    public static SourceEditPlan PlanTransform(SourceWorkspace workspace, string nodeName, WorldNodeProvenance origin, IReadOnlyDictionary<(string Script, int Line), int> executions, ObjectTransform requested, CancellationToken token = default)
    {
        Check(requested.Position); Check(requested.RotationDegrees); Check(requested.Scale);
        string label = $"Move {nodeName}";
        var writers = TransformCommands.Where(origin.Writers.ContainsKey).Select(c => origin.Writers[c]).ToArray();
        if (writers.Length > 0 || origin.ModelFile == null)
        {
            // The scripts place the object: change its transform instructions, adding those it lacks after the last one (or after the
            // instruction that created the object, which leaves it current).
            var anchor = writers.OrderBy(w => w.Line).LastOrDefault() ?? origin.Created ?? throw new InvalidDataException($"{nodeName} was neither loaded from a model nor created by a script instruction.");
            if (writers.Select(w => w.Script).Append(anchor.Script).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
                throw new InvalidDataException($"{nodeName}'s transform is set in several scripts; edit them in the scripts directly.");
            ScriptEdit edit = new(workspace, anchor.Script, executions, token);
            edit.Set(origin.Writers.GetValueOrDefault("Object3DTranslate"), "Object3DTranslate", requested.Position, Vector3.Zero, anchor);
            edit.Set(origin.Writers.GetValueOrDefault("Object3DRotate"), "Object3DRotate", requested.RotationDegrees, Vector3.Zero, anchor);
            edit.Set(origin.Writers.GetValueOrDefault("Object3DScale"), "Object3DScale", requested.Scale, Vector3.One, anchor);
            return new(label, edit.Changes(), $"{anchor.Script} line {anchor.Line}", edit.Notes);
        }
        // A node of a glTF file: its transform is the node's.
        return GltfEdit(workspace, origin, label, node =>
        {
            var m = requested.Matrix();
            Matrix4x4.Decompose(m, out var scale, out var rotation, out var translation);
            node.Remove("matrix");
            node["translation"] = Array(translation.X, translation.Y, translation.Z);
            node["rotation"] = Array(rotation.X, rotation.Y, rotation.Z, rotation.W);
            node["scale"] = Array(scale.X, scale.Y, scale.Z);
        }, token);
    }

    /// <summary>A plan that sets or clears one node flag bit (see <see cref="FlagCommands"/> and <see cref="WorldGltf.CarriedFlags"/>).</summary>
    public static SourceEditPlan PlanFlag(SourceWorkspace workspace, string nodeName, WorldNodeProvenance origin, IReadOnlyDictionary<(string Script, int Line), int> executions, uint bit, bool on, CancellationToken token = default)
    {
        if (System.Numerics.BitOperations.PopCount(bit) != 1 || (bit & WorldGltf.CarriedFlags) == 0) throw new InvalidDataException($"0x{bit:X} is not one node flag a source can set.");
        string label = $"{(on ? "Set" : "Clear")} flag 0x{bit:X} of {nodeName}";
        string value = on ? "on" : "off";
        if (FlagCommands.TryGetValue(bit, out string? command) && origin.Writers.TryGetValue(command, out var writer))
        {
            ScriptEdit edit = new(workspace, writer.Script, executions, token);
            edit.Replace(writer, new() { [1] = value });
            return new(label, edit.Changes(), $"{writer.Script} line {writer.Line}", edit.Notes);
        }
        if (origin.ModelFile != null)
            return GltfEdit(workspace, origin, label, node =>
            {
                var extras = node["extras"] as JsonObject ?? (JsonObject)(node["extras"] = new JsonObject());
                var recoil = extras[WorldGltf.Key] as JsonObject ?? (JsonObject)(extras[WorldGltf.Key] = new JsonObject());
                uint carried = recoil["flags"] is JsonValue text && text.TryGetValue<string>(out var hex) && hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    && uint.TryParse(hex.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed) ? parsed & WorldGltf.CarriedFlags : WorldGltf.DefaultCarried;
                carried = on ? carried | bit : carried & ~bit;
                if (carried == WorldGltf.DefaultCarried) recoil.Remove("flags"); else recoil["flags"] = $"0x{carried:X8}";
            }, token);
        if (command == null) throw new InvalidDataException($"No script command sets flag 0x{bit:X}; {nodeName} was created by a script.");
        var anchor = origin.Created ?? throw new InvalidDataException($"{nodeName} was neither loaded from a model nor created by a script instruction.");
        ScriptEdit insert = new(workspace, anchor.Script, executions, token);
        insert.Insert(anchor, [command, value]);
        return new(label, insert.Changes(), $"{anchor.Script} line {anchor.Line}", insert.Notes);
    }

    /// <summary>
    /// A plan that sets the arguments of one property command (WorldSetFogColor, LightSetDiffuse, …) of a node a script made:
    /// the instruction that last set it changes, or a new one follows the instruction that created the node.
    /// </summary>
    public static SourceEditPlan PlanCommand(SourceWorkspace workspace, string nodeName, WorldNodeProvenance origin, IReadOnlyDictionary<(string Script, int Line), int> executions, string command, IReadOnlyList<string> args, CancellationToken token = default)
    {
        if (args.Count == 0) throw new InvalidDataException($"{command} needs arguments.");
        string label = $"{command} on {nodeName}";
        if (origin.Writers.TryGetValue(command, out var writer))
        {
            ScriptEdit edit = new(workspace, writer.Script, executions, token);
            if (writer.Tokens.Count - 1 != args.Count) edit.ReplaceLine(writer, [command, .. args]);
            else edit.Replace(writer, args.Select((a, i) => (Index: i + 1, Value: a)).ToDictionary(p => p.Index, p => p.Value));
            return new(label, edit.Changes(), $"{writer.Script} line {writer.Line}", edit.Notes);
        }
        var anchor = origin.Created ?? throw new InvalidDataException($"No instruction sets {command} for {nodeName}, and no script created it.");
        ScriptEdit insert = new(workspace, anchor.Script, executions, token);
        insert.Insert(anchor, [command, .. args]);
        return new(label, insert.Changes(), $"{anchor.Script} line {anchor.Line}", insert.Notes);
    }

    /// <summary>A script number as the shipped scripts write them: shortest round-trip form with a decimal point.</summary>
    public static string Number(float value)
    {
        if (!float.IsFinite(value)) throw new InvalidDataException("Values must be finite.");
        string text = value.ToString("R", CultureInfo.InvariantCulture);
        return text.Contains('.') || text.Contains('E') ? text : text + ".0";
    }

    private static SourceEditPlan GltfEdit(SourceWorkspace workspace, WorldNodeProvenance origin, string label, Action<JsonObject> change, CancellationToken token)
    {
        string file = origin.ModelFile!;
        byte[] bytes = workspace.Read(file, token) ?? throw new InvalidDataException($"{file} no longer exists.");
        if (!file.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"{file} is a binary glTF; save it as .gltf to edit its nodes here.");
        JsonNode? root;
        try { root = JsonNode.Parse(bytes, documentOptions: new() { MaxDepth = 256 }); }
        catch (JsonException ex) { throw new InvalidDataException($"{file} is not valid JSON: {ex.Message}", ex); }
        if (root?["nodes"] is not JsonArray nodes || origin.ModelNode < 0 || origin.ModelNode >= nodes.Count || nodes[origin.ModelNode] is not JsonObject node)
            throw new InvalidDataException($"{file} no longer has node {origin.ModelNode}; rebuild the world.");
        change(node);
        // zStudio and Blender write glTF indented or minified; keep the file's style.
        bool indented = bytes.AsSpan(0, Math.Min(bytes.Length, 4096)).Contains((byte)'\n');
        byte[] content = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = indented }));
        List<string> notes = [];
        if (!origin.Database) notes.Add($"{file} is a model file: the change applies wherever it is loaded.");
        return new(label, [(file, content)], $"{file} node {origin.ModelNode}", notes);
    }
    private static JsonArray Array(params float[] values) => new(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray());
    private static void Check(Vector3 v)
    {
        if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z) || Math.Abs(v.X) > SourceWorlds.MaximumCoordinate || Math.Abs(v.Y) > SourceWorlds.MaximumCoordinate || Math.Abs(v.Z) > SourceWorlds.MaximumCoordinate)
            throw new InvalidDataException($"Values must be finite and within ±{SourceWorlds.MaximumCoordinate:N0}.");
    }

    /// <summary>Token replacements and inserted lines of one script, applied together: replacements first, then insertions from the bottom up.</summary>
    private sealed class ScriptEdit(SourceWorkspace workspace, string script, IReadOnlyDictionary<(string Script, int Line), int> executions, CancellationToken token)
    {
        private readonly Dictionary<int, Dictionary<int, string>> replacements = [];
        private readonly Dictionary<int, List<IReadOnlyList<string>>> insertions = [];
        private readonly Dictionary<int, IReadOnlyList<string>> lines = [];
        public List<string> Notes { get; } = [];

        private void Editable(SourceInstruction instruction)
        {
            if (!instruction.Script.Equals(script, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("The instructions of one edit must be in one script.");
            int runs = executions.GetValueOrDefault((instruction.Script, instruction.Line));
            if (runs > 1) throw new InvalidDataException($"{instruction.Script} line {instruction.Line} runs {runs} times while the world is built, so it sets this value for more than one object; edit the script directly.");
        }
        public void Replace(SourceInstruction instruction, Dictionary<int, string> values)
        {
            Editable(instruction);
            foreach (var index in values.Keys)
                if (index < instruction.Tokens.Count && instruction.Tokens[index].Contains('%')) throw new InvalidDataException($"{instruction.Script} line {instruction.Line} takes this value from a macro ({instruction.Tokens[index]}); edit the macro instead.");
            if (!replacements.TryGetValue(instruction.Line, out var existing)) replacements[instruction.Line] = existing = [];
            foreach (var (index, value) in values) existing[index] = value;
        }
        public void ReplaceLine(SourceInstruction instruction, IReadOnlyList<string> tokens)
        {
            Editable(instruction);
            if (instruction.Tokens.Skip(1).Any(t => t.Contains('%'))) throw new InvalidDataException($"{instruction.Script} line {instruction.Line} uses macros; edit it in the script directly.");
            lines[instruction.Line] = tokens;
        }
        public void Insert(SourceInstruction after, IReadOnlyList<string> tokens)
        {
            Editable(after);
            if (!insertions.TryGetValue(after.Line + 1, out var list)) insertions[after.Line + 1] = list = [];
            list.Add(tokens);
        }
        /// <summary>A vector instruction: the existing one changes; a missing one is added when the value differs from the default.</summary>
        public void Set(SourceInstruction? writer, string command, Vector3 value, Vector3 unset, SourceInstruction anchor)
        {
            string[] numbers = [Number(value.X), Number(value.Y), Number(value.Z)];
            if (writer != null)
            {
                if (writer.Tokens.Count >= 4 && Enumerable.Range(0, 3).All(i => WorldAssembler.Number(writer.Tokens[i + 1]) == value[i])) return;
                Replace(writer, new() { [1] = numbers[0], [2] = numbers[1], [3] = numbers[2] });
                return;
            }
            if (value != unset) Insert(anchor, [command, .. numbers]);
        }
        public IReadOnlyList<(string Relative, byte[] Content)> Changes()
        {
            string relative = script;
            var syntax = GameGenScriptSyntax.Parse(workspace.Read(relative, token) ?? throw new InvalidDataException($"{relative} no longer exists."));
            foreach (var (line, values) in replacements)
            {
                var current = syntax.Line(line);
                if (!current.IsInstruction) throw new InvalidDataException($"{relative} line {line} changed since the world was built; rebuild it first.");
                syntax = GameGenScriptSyntax.Parse(syntax.ReplaceTokens(line, values));
            }
            // Line-changing steps run from the bottom up, so the line numbers of those still to come stay valid.
            var steps = insertions.Select(i => (Line: i.Key, Rewrite: false)).Concat(lines.Keys.Select(l => (Line: l, Rewrite: true))).OrderByDescending(s => s.Line).ThenBy(s => s.Rewrite);
            foreach (var (line, rewrite) in steps)
            {
                if (!rewrite) { syntax = GameGenScriptSyntax.Parse(syntax.InsertLines(Math.Min(line, syntax.Lines.Count + 1), insertions[line])); continue; }
                // A whole instruction rewritten: the old one becomes a comment and the new one follows it, keeping both readable.
                syntax = GameGenScriptSyntax.Parse(syntax.InsertLines(line + 1, [lines[line]]));
                syntax = GameGenScriptSyntax.Parse(syntax.CommentOut(line));
            }
            if (replacements.Count == 0 && lines.Count == 0 && insertions.Count == 0) return [];
            return [(relative, syntax.Encode())];
        }
    }
}
