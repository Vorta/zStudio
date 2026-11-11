using zStudio.Core.Formats;

namespace zStudio.Core;

public sealed record Mw3ValveRoute(bool Passable, IReadOnlyList<string> Reasons, IReadOnlyList<(string Valve, int Value)> Assignments);

public static class Mw3ValveRoutes
{
    /// <summary>One physical outgoing slot, never a deduplicated pair of node names.</summary>
    public static Mw3ValveRoute Evaluate(AiNetwork network, AiNode node, int slot, Mw3ValveRuntime runtime)
    {
        if (!network.IsMw3 || node.Source is not { } source) return new(true, [], []);
        var links = source.Children[2].Children;
        if (slot < 0 || slot >= links.Count || unchecked((int)links[slot].Bits) < 0) throw new ArgumentException("Choose an outgoing link slot.");
        int last = links.Count - 1;
        while (last >= 0 && unchecked((int)links[last].Bits) < 0) last--;
        int target = unchecked((int)links[slot].Bits);
        List<string> reasons = []; List<(string, int)> assignments = [];
        Inspect(source, slot == last ? 1 : 0);
        if (network.ValveEdgesByNode.TryGetValue(node.Index, out var edges)) foreach (var edge in edges)
        {
            var ends = edge.Children[0].Children;
            if (unchecked((int)ends[0].Bits) != node.Index) continue;
            for (int j = 1; j < ends.Count; j++)
                if (unchecked((int)ends[j].Bits) == target) Inspect(edge, j == ends.Count - 1 ? 1 : 0);
        }
        return new(reasons.Count == 0, reasons.AsReadOnly(), assignments.AsReadOnly());
        void Inspect(ZrdNode record, int expectedDefault)
        {
            var condition = Mw3ValveProgram.Find(record, "valve");
            if (condition is { Children.Count: >= 2 } && Mw3ValveRuntime.Integer(condition, 0) != 0 && Mw3ValveRuntime.Text(condition, 1) is { } name)
            {
                int expected = condition.Children.Count > 2 ? Mw3ValveRuntime.Integer(condition, 2) : expectedDefault;
                if (runtime.Value(name) != expected && reasons.Count < 64) reasons.Add($"{MissionAiValves.Short(name)} must equal {expected}.");
            }
            var union = Mw3ValveProgram.Find(record, "valveunion");
            if (union != null && !union.Children.Any(term => Mw3ValveRuntime.Integer(term, 0) != 0
                && Mw3ValveRuntime.Text(term, 1) is { } n && runtime.Value(n) == (term.Children.Count > 2 ? Mw3ValveRuntime.Integer(term, 2) : 1))
                && reasons.Count < 64) reasons.Add("No enabled valve-union term matches.");
            var assignment = Mw3ValveProgram.Find(record, "valve_assign");
            if (assignment != null && Mw3ValveRuntime.Text(assignment, 0) is { } a)
            {
                if (assignments.Count == 1024) throw new InvalidDataException("Too many route assignments for preview.");
                assignments.Add((a, Mw3ValveRuntime.Integer(assignment, 1)));
            }
        }
    }
}
