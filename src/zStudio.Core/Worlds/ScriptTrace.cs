using Recoil.Zbd.Core.Sources;

namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// One executed gamegen instruction: its script, command and expanded arguments, and the model and texture directories
/// at that point, most recently added first (the order they are searched).
/// </summary>
public sealed record TracedInstruction(string Script, string Command, IReadOnlyList<string> Args, IReadOnlyList<string> ModelDirectories, string? ScriptModelDirectory, IReadOnlyList<string> TextureDirectories);

/// <summary>
/// The instructions a mission's build script runs, in order, with <c>source</c> followed and macros expanded, up to
/// the point where it writes the world. Model directory changes are tracked, including the most recent one made by
/// the running script itself (the folder its models came from).
/// </summary>
public static class ScriptTrace
{
    public static List<TracedInstruction> Trace(Func<string, IReadOnlyList<IReadOnlyList<string>>?> script, string entry, List<string> notes)
    {
        List<TracedInstruction> result = []; Dictionary<string, string> variables = new(StringComparer.OrdinalIgnoreCase);
        List<string> directories = [], textures = []; bool written = false;
        // Instructions share a directory list until it changes.
        IReadOnlyList<string> modelView = [], textureView = [];
        Run(entry, 0);
        return result;

        void Run(string name, int depth)
        {
            if (depth > WorldAssembler.MaximumScriptDepth || written) return;
            var lines = script(name.Replace('/', '\\'));
            if (lines == null) { notes.Add($"Script {name} is missing."); return; }
            string? ownDirectory = null; int skipping = 0;
            foreach (var line in lines)
            {
                if (written) return;
                string command = line[0];
                if (command.Equals("endif", StringComparison.OrdinalIgnoreCase)) { if (skipping > 0) skipping--; continue; }
                if (command.Equals("ifdef", StringComparison.OrdinalIgnoreCase) || command.Equals("ifndef", StringComparison.OrdinalIgnoreCase))
                {
                    if (skipping > 0) { skipping++; continue; }
                    bool defined = line.Count > 1 && variables.ContainsKey(line[1]);
                    if (defined != command.Equals("ifdef", StringComparison.OrdinalIgnoreCase)) skipping = 1;
                    continue;
                }
                if (skipping > 0) continue;
                string[] args = line.Skip(1).Select(Expand).ToArray();
                if (command.Equals("Quit", StringComparison.OrdinalIgnoreCase)) return;
                if (command.Equals("source", StringComparison.OrdinalIgnoreCase)) { if (args.Length > 0) Run(args[0], depth + 1); continue; }
                if (command.Equals("set", StringComparison.OrdinalIgnoreCase) && args.Length > 0) variables[args[0]] = args.Length > 1 ? args[1] : "";
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
        string Expand(string token)
        {
            if (!token.Contains('%')) return token;
            System.Text.StringBuilder text = new();
            for (int i = 0; i < token.Length; i++)
            {
                int end = token[i] == '%' ? token.IndexOf('%', i + 1) : -1;
                if (end > i) { text.Append(variables.GetValueOrDefault(token[(i + 1)..end]) ?? ""); i = end; } else text.Append(token[i]);
            }
            return text.ToString();
        }
    }
}
