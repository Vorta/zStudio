using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// One executed gamegen instruction: its script, command and expanded arguments, and the model and texture directories
/// at that point, most recently added first (the order they are searched).
/// </summary>
public sealed record TracedInstruction(string Script, string Command, IReadOnlyList<string> Args, IReadOnlyList<string> ModelDirectories, string? ScriptModelDirectory, IReadOnlyList<string> TextureDirectories);

/// <summary>
/// The retail interpreter's built-in commands that decide whether a line runs (CZInterp::HandleBuiltinCommand, retail
/// 0x4C1C50). Built-ins match by case-sensitive prefix (<c>ifdef</c>, <c>ifndef</c>, <c>endif</c>, <c>set</c>,
/// <c>source</c>; <c>Quit</c> exactly). The skip depth is one counter for the whole run, across sourced files: a false
/// condition starts skipping, and while skipping every line but <c>endif</c> is ignored, including nested conditions,
/// so the first <c>endif</c> ends the skip. A condition is true when its macro holds exactly <c>TRUE</c>
/// (IsMacroTrue, 0x4C1710); several combine left to right with <c>||</c> and <c>&amp;&amp;</c> (EvalConditionExpr, 0x4C1B50).
/// Macro names are case-sensitive.
/// </summary>
internal sealed class ScriptConditions
{
    private int skipping;
    public static bool IsQuit(string command) => command == "Quit";
    public static bool IsSet(string command) => command.StartsWith("set", StringComparison.Ordinal);
    public static bool IsSource(string command) => command.StartsWith("source", StringComparison.Ordinal);

    /// <summary>Whether the line is an instruction to run; conditional lines and skipped lines are consumed.</summary>
    public bool Runs(IReadOnlyList<string> tokens, IReadOnlyDictionary<string, string> macros)
    {
        string command = tokens[0];
        if (command.StartsWith("endif", StringComparison.Ordinal)) { if (skipping > 0) skipping--; return false; }
        if (skipping > 0) return false;
        if (command.StartsWith("ifdef", StringComparison.Ordinal)) { if (!Evaluate(tokens, macros)) skipping++; return false; }
        if (command.StartsWith("ifndef", StringComparison.Ordinal)) { if (Evaluate(tokens, macros)) skipping++; return false; }
        return true;
    }

    /// <summary>The longest argument a macro expansion can produce: the retail scratch buffer holds 1,024 bytes.</summary>
    public const int MaximumExpansion = 1023;
    /// <summary>
    /// ExpandMacroRefs (retail 0x4C1250): each <c>%name%</c> pair becomes the macro's value (nothing when it is not set).
    /// A longer result would overrun the engine's buffer, and repeated self-references would otherwise grow without bound.
    /// </summary>
    public static string Expand(string token, IReadOnlyDictionary<string, string> macros)
    {
        if (!token.Contains('%')) return token;
        System.Text.StringBuilder text = new();
        for (int i = 0; i < token.Length; i++)
        {
            int end = token[i] == '%' ? token.IndexOf('%', i + 1) : -1;
            if (end > i) { text.Append(macros.GetValueOrDefault(token[(i + 1)..end]) ?? ""); i = end; } else text.Append(token[i]);
            if (text.Length > MaximumExpansion) throw new InvalidDataException($"A macro expands '{token[..Math.Min(token.Length, 64)]}' past {MaximumExpansion} characters.");
        }
        return text.ToString();
    }

    private static bool Evaluate(IReadOnlyList<string> tokens, IReadOnlyDictionary<string, string> macros)
    {
        bool True(string name) => macros.TryGetValue(name, out string? value) && value == "TRUE";
        if (tokens.Count == 1) return false;
        int op = 0; bool result = false;
        for (int i = 1; i < tokens.Count;)
        {
            bool value = True(tokens[i++]);
            result = op switch { 1 => result | value, 2 => result & value, _ => value };
            if (i < tokens.Count)
            {
                string text = tokens[i++];
                if (text.StartsWith("||", StringComparison.Ordinal)) op = 1;
                else if (text.StartsWith("&&", StringComparison.Ordinal)) op = 2;
                else break;
            }
        }
        return result;
    }
}

/// <summary>
/// The instructions a mission's build script runs, in order, with <c>source</c> followed and macros expanded, up to
/// the point where it writes the world. Model directory changes are tracked, including the most recent one made by
/// the running script itself (the folder its models came from).
/// </summary>
public static class ScriptTrace
{
    public static List<TracedInstruction> Trace(Func<string, IReadOnlyList<IReadOnlyList<string>>?> script, string entry, List<string> notes)
    {
        List<TracedInstruction> result = []; Dictionary<string, string> variables = new(StringComparer.Ordinal);
        List<string> directories = [], textures = []; bool written = false; ScriptConditions conditions = new();
        // Instructions share a directory list until it changes.
        IReadOnlyList<string> modelView = [], textureView = [];
        Run(entry, 0);
        return result;

        void Run(string name, int depth)
        {
            if (depth > WorldAssembler.MaximumScriptDepth || written) return;
            var lines = script(name.Replace('/', '\\'));
            if (lines == null) { notes.Add($"Script {name} is missing."); return; }
            string? ownDirectory = null;
            foreach (var line in lines)
            {
                if (written) return;
                string command = line[0];
                if (!conditions.Runs(line, variables)) continue;
                string[] args = line.Skip(1).Select(Expand).ToArray();
                if (ScriptConditions.IsQuit(command)) return;
                if (ScriptConditions.IsSet(command)) { if (args.Length > 0) variables[args[0]] = args.Length > 1 ? args[1] : ""; }
                else if (ScriptConditions.IsSource(command)) { if (args.Length > 0) Run(args[0], depth + 1); continue; }
                if (command == "SetModelDirectory")
                {
                    foreach (string part in (args.Length > 0 ? args[0] : "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (WorldAssembler.ProjectPath(part) is { } folder) { directories.Remove(folder); directories.Insert(0, folder); ownDirectory = folder; }
                    modelView = [.. directories];
                }
                if (command == "SetTextureDirectory")
                {
                    foreach (string part in (args.Length > 0 ? args[0] : "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                        if (WorldAssembler.ProjectPath(part) is { } folder) { textures.Remove(folder); textures.Insert(0, folder); }
                    textureView = [.. textures];
                }
                result.Add(new(name, command, args, modelView, ownDirectory, textureView));
                if (command == "GameZWriteZBDFile") written = true;
            }
        }
        string Expand(string token) => ScriptConditions.Expand(token, variables);
    }
}
