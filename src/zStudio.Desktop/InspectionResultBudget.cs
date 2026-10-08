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
