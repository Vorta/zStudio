using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// One executed gamegen instruction: its script, command and expanded arguments, and the model and texture directories
/// at that point in the order they are searched (<see cref="DirectorySearchList"/>: the most recently added new folder
/// first; a folder named again keeps its place).
/// </summary>
public sealed record TracedInstruction(string Script, string Command, IReadOnlyList<string> Args, IReadOnlyList<string> ModelDirectories, string? ScriptModelDirectory, IReadOnlyList<string> TextureDirectories);

/// <summary>One operation's retained model and texture directory history, charged before an array is copied.</summary>
internal sealed class ScriptTraceBudget(long maximumUnits = ScriptTraceBudget.MaximumUnits, long maximumWorkUnits = DirectoryWorkBudget.MaximumUnits,
    long maximumOperandReferences = ScriptOperandBudget.MaximumReferences, long maximumOperandCharacters = ScriptOperandBudget.MaximumCharacters)
{
    internal const long MaximumUnits = 4_194_304;
    private readonly long maximum = maximumUnits is >= 0 and <= MaximumUnits ? maximumUnits : throw new ArgumentOutOfRangeException(nameof(maximumUnits));
    internal long UsedUnits { get; private set; }
    private bool snapshotsExhausted;
    internal DirectoryWorkBudget Work { get; } = new(maximumWorkUnits);
    internal ScriptOperandBudget Operands { get; } = new(maximumOperandReferences, maximumOperandCharacters);
    internal bool Exhausted => snapshotsExhausted || Work.Exhausted || Operands.Exhausted;

    internal void ReserveSnapshot(int count)
    {
        long units = (long)count + 4; // References plus array overhead, in reference-sized units.
        if (Exhausted || units > maximum - UsedUnits)
        {
            snapshotsExhausted = true;
            throw new InvalidDataException("The scripts retain too much model and texture directory history. Reduce directory changes or split the source project into fewer missions.");
        }
        UsedUnits += units;
    }
}

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

    /// <summary>Whether <see cref="Expand"/> changes the token: it holds a pair of <c>%</c>, whose name only the run's macros resolve.</summary>
    public static bool HasMacro(string token) => token.IndexOf('%') is >= 0 and int first && token.IndexOf('%', first + 1) > first;
    /// <summary>The longest argument a macro expansion can produce: the retail scratch buffer holds 1,024 bytes.</summary>
    public const int MaximumExpansion = 1023;
    /// <summary>
    /// ExpandMacroRefs (retail 0x4C1250): each <c>%name%</c> pair becomes the macro's value (nothing when it is not set).
    /// A longer result would overrun the engine's buffer, and repeated self-references would otherwise grow without bound.
    /// </summary>
    public static string Expand(string token, IReadOnlyDictionary<string, string> macros)
        => Expand(token, macros, null);

    internal static string Expand(string token, IReadOnlyDictionary<string, string> macros, Action<long>? reserveCharacters)
    {
        if (!token.Contains('%')) return token;
        reserveCharacters?.Invoke(32);
        System.Text.StringBuilder text = new();
        for (int i = 0; i < token.Length; i++)
        {
            int end = token[i] == '%' ? token.IndexOf('%', i + 1) : -1;
            if (end > i)
            {
                reserveCharacters?.Invoke(end - i - 1L); // Macro-key substring, before copying it.
                string value = macros.GetValueOrDefault(token[(i + 1)..end]) ?? "";
                Check(text, token, value.Length, reserveCharacters); text.Append(value); i = end;
            }
            else { Check(text, token, 1, reserveCharacters); text.Append(token[i]); }
        }
        reserveCharacters?.Invoke(text.Length);
        return text.ToString();

        static void Check(System.Text.StringBuilder text, string token, int count, Action<long>? reserveCharacters)
        {
            if (count > MaximumExpansion - text.Length) throw new InvalidDataException($"A macro expands '{token[..Math.Min(token.Length, 64)]}' past {MaximumExpansion} characters.");
            reserveCharacters?.Invoke(2L * count); // Growing builder storage, before Append.
        }
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
/// the point where it writes the world. Model directory changes are tracked, including the most recent folder named by
/// the running script itself (the folder its models came from). Folders are listed by the engine's rule
/// (<see cref="DirectorySearchList"/>); without a folder test every folder a script names counts as present, as in the
/// original build tree. Scripts the build refuses (sourcing each other more than
/// <see cref="WorldAssembler.MaximumScriptDepth"/> levels deep, or running more than
/// <see cref="WorldAssembler.MaximumInstructions"/> instructions) are refused with <see cref="InvalidDataException"/>.
/// </summary>
public static class ScriptTrace
{
    public static List<TracedInstruction> Trace(Func<string, IReadOnlyList<IReadOnlyList<string>>?> script, string entry, List<string> notes)
        => Trace(script, entry, notes, new ScriptTraceBudget());

    internal static List<TracedInstruction> Trace(Func<string, IReadOnlyList<IReadOnlyList<string>>?> script, string entry, List<string> notes, ScriptTraceBudget budget,
        BoundedDiagnostics? diagnostics = null, CancellationToken token = default, Func<string, bool>? folderExists = null)
        => RunTrace(script, entry, notes, budget, diagnostics, null, true, folderExists, token);

    /// <summary>Observe executed operands without retaining instruction history, optionally including post-write scripts.</summary>
    internal static void Visit(Func<string, IReadOnlyList<IReadOnlyList<string>>?> script, string entry,
        Action<TracedInstruction> inspect, CancellationToken token, bool stopAtWorldWrite = true, ScriptTraceBudget? budget = null,
        Func<string, bool>? folderExists = null)
        => RunTrace(script, entry, [], budget ?? new(), null, inspect, stopAtWorldWrite, folderExists, token);

    private static List<TracedInstruction> RunTrace(Func<string, IReadOnlyList<IReadOnlyList<string>>?> script, string entry,
        List<string> notes, ScriptTraceBudget budget, BoundedDiagnostics? diagnostics, Action<TracedInstruction>? inspect,
        bool stopAtWorldWrite, Func<string, bool>? folderExists, CancellationToken token)
    {
        diagnostics ??= new(notes);
        List<TracedInstruction> result = []; Dictionary<string, string> variables = new(StringComparer.Ordinal);
        DirectorySearchList directories = new(), textures = new(); bool written = false; ScriptConditions conditions = new();
        // As many instructions as the build runs: scripts sourcing each other repeatedly would otherwise multiply.
        int instructions = 0;
        // Instructions share a directory list until it changes.
        IReadOnlyList<string> modelView = [], textureView = [];
        Run(entry, 0);
        return result;

        void Run(string name, int depth)
        {
            token.ThrowIfCancellationRequested();
            // The build refuses scripts that source each other this deep.
            if (depth > WorldAssembler.MaximumScriptDepth) throw new InvalidDataException($"Scripts source each other more than {WorldAssembler.MaximumScriptDepth} levels deep.");
            if (written) return;
            var lines = script(name.Replace('/', '\\'));
            if (lines == null) { diagnostics.Add($"Script {name} is missing."); return; }
            string? ownDirectory = null;
            foreach (var line in lines)
            {
                token.ThrowIfCancellationRequested();
                if (written) return;
                if (++instructions > WorldAssembler.MaximumInstructions) throw new InvalidDataException("The scripts run too many instructions.");
                budget.Operands.Inspect(line, token);
                string command = line[0];
                if (!conditions.Runs(line, variables)) continue;
                string[] args = budget.Operands.Expand(line, variables, token);
                if (ScriptConditions.IsQuit(command)) return;
                if (ScriptConditions.IsSet(command)) { if (args.Length > 0) variables[args[0]] = args.Length > 1 ? args[1] : ""; }
                else if (ScriptConditions.IsSource(command)) { if (args.Length > 0) Run(args[0], depth + 1); continue; }
                command = ScriptCommands.Core(command);
                if (command == "SetModelDirectory")
                {
                    ownDirectory = directories.Add(args.Length > 0 ? args[0] : "", budget.Work, folderExists) ?? ownDirectory;
                    modelView = Snapshot(directories.Folders, modelView);
                }
                if (command == "SetTextureDirectory")
                {
                    textures.Add(args.Length > 0 ? args[0] : "", budget.Work, folderExists);
                    textureView = Snapshot(textures.Folders, textureView);
                }
                TracedInstruction instruction = new(name, command, args, modelView, ownDirectory, textureView);
                if (inspect == null) result.Add(instruction); else inspect(instruction);
                if (stopAtWorldWrite && command == "GameZWriteZBDFile") written = true;
            }
        }
        IReadOnlyList<string> Snapshot(IReadOnlyList<string> current, IReadOnlyList<string> previous)
        {
            // A command that names only listed directories leaves the order as it was. Its own-directory
            // assignment still matters, but it needs no new immutable history when the search order matches.
            if (WorldDirectoryPaths.SameOrder(current, previous, budget.Work)) return previous;
            budget.ReserveSnapshot(current.Count);
            return current.ToArray();
        }
    }
}
