using Recoil.Zbd.Automation;
using Recoil.Zbd.Core;

namespace Recoil.Zbd.Desktop;

/// <summary>Conservative JSON costs, computed from retained identities before projection or escaping.</summary>
internal static class InspectionResultBudget
{
    internal const long PageBytes = 1024 * 1024;
    internal static long Text(string? value) => 8 + 6L * (value?.Length ?? 0);
    internal static long Source(MissionPickupSource? source) => source == null ? 4 :
        128 + Text(source.ArchivePath) + Text(source.ResourceName);
    internal static long Actor(MissionActor? actor) => actor == null ? 4 :
        1024 + Text(actor.Name) + Text(actor.PlacementSource) + Source(actor.CoordinateSource)
        + (actor.Pickup is { } pickup ? 512 + Text(pickup.LogicalName) + Source(pickup.Source) : 4);
    internal static long Pickup(PickupPlacementRecord record, string target, string scope) =>
        1024 + Source(record.Source) + Text(record.Type) + Text(target) + Text(scope);
    // PreviewObject uses at most64 nodes and1024 total text characters. Include keys, truncation markers and scalars.
    internal static long SceneNode(string name, string kind, MissionActor? actor) =>
        16_384 + Text(name) + Text(kind) + Actor(actor);
    internal static long Pack(string name, string? path) => 128 + Text(name) + Text(path);
    // Count the displayed prefixes without constructing them. Six bytes per UTF-16 unit also covers
    // JSON escaping; the suffix allowance covers both ShortAiText and Core's truncation marker.
    private static long Prefix(string? value, int limit) => 8 + 6L * (Math.Min(value?.Length ?? 0, limit) + 16);
    internal static long AiNetwork(string snapshot, AiNetwork network)
    {
        long bytes = 2048 + Text(snapshot) + Text(network.Id) + Text(network.Archive) + Text(network.Member)
            + Prefix(network.Name, 70) + Prefix(network.Type, 70) + Prefix(network.AttackStrategy.Value, 1024);
        foreach (var diagnostic in network.Diagnostics.Take(8))
            bytes += 256 + Text(diagnostic.Severity) + Prefix(diagnostic.Message, 128);
        return bytes;
    }
    internal static long AiNode(string snapshot, AiNetwork network, AiNode node)
    {
        // Fixed fields include all property names/numbers, strategy labels, and two valve summaries:
        // each summary has one 64-character attribute and two 64-character reference prefixes.
        long bytes = 8192 + Text(snapshot) + Text(network.Id) + Text(node.Id)
            + Text(network.Archive) + Text(network.Member) + Prefix(network.Name, 256)
            + Prefix(network.Type, 256) + Prefix(network.AttackStrategy.Value, 1024);
        foreach (var link in node.PreviewLinks)
            bytes += 256 + Text(link.Target) + Text(link.Problem);
        return bytes;
    }
    // File is also serialized through computed Scope and Details. Estimate all three from the retained
    // path before either getter allocates; the fixed allowance includes their labels and numeric fields.
    internal static long Problem(StudioProblem problem) =>
        1024 + Text(problem.Severity) + Text(problem.Category) + Text(problem.Message) + 3 * Text(problem.File);

    /// <summary>Preserve combined displayed-text search, but admit all temporary copies before concatenation.</summary>
    internal sealed class Search(long maximum = 32L * 1024 * 1024)
    {
        private long remaining = maximum;
        internal bool Pickup(PickupPlacementRecord record, string target, string query)
        {
            long characters = (long)record.Type.Length + record.Source.ResourceName.Length + target.Length + 2;
            long cost = 64 + 2 * characters;
            if (cost > remaining)
                throw new StudioCommandException("too_large", "The combined pickup search text exceeds its inspection budget; omit query and follow the result pages.");
            remaining -= cost;
            return string.Concat(record.Type, " ", record.Source.ResourceName, " ", target).Contains(query, StringComparison.OrdinalIgnoreCase);
        }
    }
}
