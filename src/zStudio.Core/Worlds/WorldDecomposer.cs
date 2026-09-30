namespace Recoil.Zbd.Core.Worlds;

/// <summary>
/// A model load in a mission's build: the file the script named, the root it created, the nodes the file contributed,
/// and whether the script changed the root's flags afterwards (a root's flags otherwise come from its file).
/// </summary>
public sealed record LoadedModel(string File, string NodeName, TracedInstruction Instruction, bool Database, WorldNode? Root, IReadOnlyList<WorldNode> Content, bool RootEdited);

/// <summary>
/// Recovers what each <c>LoadGameGen</c> contributed to a shipped world. The build scripts edited the loaded models
/// afterwards (attaching them to the world, renaming parts, rearranging children), so those edits are undone in
/// reverse order on the shipped node graph; each load's root then holds exactly the file's scene, and the world's
/// remaining children are the mission database. Names resolve as they did when each command ran: FindNode takes the
/// newest node with the name among those created so far.
/// </summary>
public static class WorldDecomposer
{
    private abstract record Edit;
    private sealed record Added(WorldNode Parent, WorldNode Child) : Edit;
    private sealed record Removed(WorldNode Parent, WorldNode Child) : Edit;
    private sealed record Renamed(WorldNode Node, string From) : Edit;
    private static readonly HashSet<string> FlagCommands = ["SetAltitudeSurface", "SetIntersectSurface", "SetIntersectBBOX", "SetProximity", "SetLandmark", "NodeSetCanModify", "NodeSetOverwrite"];
    private static readonly HashSet<string> Creators = ["NewWorld", "NewWindow", "NewCamera", "NewDisplay", "LightNew", "NewObject3D"];

    /// <summary>Changes <paramref name="world"/>'s links and names; pass a world read for this purpose.</summary>
    public static List<LoadedModel> Decompose(GameZWorld world, IReadOnlyList<TracedInstruction> trace, List<string> notes)
    {
        var root = world.Nodes.FirstOrDefault(n => n.Class == WorldNodeClass.World) ?? throw new InvalidDataException("The world has no world node.");
        // Work on a plain graph: the world's cells hold children the same way its own list does.
        foreach (var cell in root.Areas) { foreach (var n in cell.Nodes) if (!root.Children.Contains(n)) root.Children.Add(n); cell.Nodes.Clear(); }
        Dictionary<WorldNode, int> slot = new(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < world.Nodes.Count; i++) slot[world.Nodes[i]] = i;

        // 1. Nodes each instruction created by name: load roots are the world's children or detached nodes; they are
        //    claimed in trace order, lowest slot first among equal names.
        Dictionary<int, WorldNode> created = []; HashSet<WorldNode> claimed = new(ReferenceEqualityComparer.Instance);
        bool pending = false; int databaseStep = -1;
        for (int i = 0; i < trace.Count; i++)
        {
            var step = trace[i];
            if (step.Command == "GameGenSetWorld") { pending = true; continue; }
            string? name = step.Command == "LoadGameGen" && !pending ? Arg(step, 1) : Creators.Contains(step.Command) ? Arg(step, 0) : null;
            if (step.Command == "LoadGameGen" && pending) { databaseStep = i; pending = false; continue; }
            if (name == null) continue;
            var node = world.Nodes.Where(n => n.Name == name && !claimed.Contains(n) && (n.Parents.Count == 0 || n.Parents.All(p => p.Class == WorldNodeClass.World)))
                .OrderBy(n => slot[n]).FirstOrDefault();
            if (node != null) { created[i] = node; claimed.Add(node); }
        }
        // 2. When each node came to exist: a load's root subtree at its load, the database's world children at its load.
        Dictionary<WorldNode, int> since = new(ReferenceEqualityComparer.Instance);
        void Mark(WorldNode node, int step)
        {
            if (!since.TryAdd(node, step)) return;
            foreach (var child in node.Children) if (!claimed.Contains(child)) Mark(child, step);
        }
        for (int i = 0; i < trace.Count; i++)
        {
            if (i == databaseStep) foreach (var child in root.Children.Where(c => !claimed.Contains(c))) Mark(child, i);
            if (created.TryGetValue(i, out var node)) Mark(node, i);
        }
        // Detached nodes belong to the last load before the script first names them.
        foreach (var node in world.Nodes.Where(n => !since.ContainsKey(n)))
        {
            int first = Enumerable.Range(0, trace.Count).FirstOrDefault(i => trace[i].Args.Contains(node.Name), -1);
            int load = first < 0 ? trace.Count : Enumerable.Range(0, first + 1).Reverse().FirstOrDefault(i => trace[i].Command == "LoadGameGen", 0);
            Mark(node, load);
        }
        WorldNode? Newest(string name, int step, bool preferDetached = false)
        {
            var matches = world.Nodes.Where(n => n.Name == name && since.GetValueOrDefault(n, int.MaxValue) <= step).ToList();
            if (preferDetached && matches.Any(n => n.Parents.Count == 0)) matches = matches.Where(n => n.Parents.Count == 0).ToList();
            return matches.OrderByDescending(n => since[n]).ThenByDescending(n => slot[n]).FirstOrDefault();
        }

        // 3. The edits, resolved in order.
        List<Edit> edits = []; HashSet<WorldNode> editedRoots = new(ReferenceEqualityComparer.Instance);
        WorldNode? current = null; string? currentName = null;
        for (int i = 0; i < trace.Count; i++)
        {
            var step = trace[i];
            if (created.TryGetValue(i, out var made)) { current = made; currentName = made.Name; continue; }
            switch (step.Command)
            {
                case "LoadGameGen": current = null; currentName = null; break;
                case "FindNode": current = Newest(Arg(step, 0), i); currentName = Arg(step, 0); break;
                case "FindSubNode":
                    {
                        var found = current == null ? null : FindSub(current, Arg(step, 0));
                        // A part renamed right after it is found is found under its new name.
                        if (found == null && current != null && i + 1 < trace.Count && trace[i + 1].Command == "NodeSetDescription")
                            found = FindSub(current, Arg(trace[i + 1], 0));
                        current = found; currentName = Arg(step, 0);
                        break;
                    }
                case "NodeSetDescription":
                    if (current != null && currentName != null && currentName != Arg(step, 0)) edits.Add(new Renamed(current, currentName));
                    currentName = Arg(step, 0); break;
                case "AddChild":
                    if (current != null && Newest(Arg(step, 0), i) is { } child && child.Parents.Contains(current)) edits.Add(new Added(current, child));
                    break;
                case "DeleteChild":
                    if (current != null && Newest(Arg(step, 0), i, preferDetached: true) is { } removed && !removed.Parents.Contains(current)) edits.Add(new Removed(current, removed));
                    break;
                default:
                    if (FlagCommands.Contains(step.Command) && current != null && claimed.Contains(current)) editedRoots.Add(current);
                    break;
            }
        }
        // 4. Undo in reverse order.
        for (int i = edits.Count - 1; i >= 0; i--)
            switch (edits[i])
            {
                case Added(var parent, var child): parent.Children.Remove(child); child.Parents.Remove(parent); break;
                case Removed(var parent, var child): if (!parent.Children.Contains(child)) { parent.Children.Add(child); child.Parents.Add(parent); } break;
                case Renamed(var node, var from): node.Name = from; break;
            }

        List<LoadedModel> result = [];
        for (int i = 0; i < trace.Count; i++)
        {
            var step = trace[i]; if (step.Command != "LoadGameGen") continue;
            string file = Arg(step, 0), name = Arg(step, 1);
            if (i == databaseStep) result.Add(new(file, name, step, true, null, root.Children.ToList(), false));
            else if (!created.TryGetValue(i, out var loadRoot)) notes.Add($"{step.Script}: LoadGameGen {file} {name} left no node in the world.");
            else result.Add(new(file, name, step, false, loadRoot, loadRoot.Children.ToList(), editedRoots.Contains(loadRoot)));
        }
        return result;
    }

    private static string Arg(TracedInstruction step, int index) => index < step.Args.Count ? step.Args[index] : "";

    private static WorldNode? FindSub(WorldNode node, string name) => WorldAssembler.FindSub(node, name);
}
