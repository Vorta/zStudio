using System.Numerics;

namespace zStudio.Core;

public sealed partial class PickupPlacementEditSession
{
    /// <summary>
    /// Validate all selected AI positions without changing history, archive ownership or source data; the result holds them
    /// as they are stored (changed components at one decimal, see <see cref="PlacementPrecision"/>).
    /// </summary>
    public IReadOnlyDictionary<MissionPickupSource, PlacementTransform> PreviewAiPositions(
        IReadOnlyDictionary<MissionPickupSource, Vector3> values)
    {
        if (values.Count is < 1 or > AiPathTransforms.MaximumNodes)
            throw new InvalidDataException($"Select between 1 and {AiPathTransforms.MaximumNodes} AI nodes.");
        Dictionary<MissionPickupSource, PlacementTransform> after = new(values.Count, sourceComparer);
        foreach (var (source, position) in values)
        {
            if (!otherCoordinates.TryGetValue(source, out var coordinate) || coordinate.Kind != "ai")
                throw new InvalidDataException("The selection contains an unavailable AI node; select the current network again.");
            AiPathTransforms.RequirePosition(position);
            after.Add(source, new(PlacementPrecision.Position(position, positions[source]), rotations[source]));
        }
        return after;
    }

    /// <summary>Accept all selected AI positions as one coordinate edit and one undo operation.</summary>
    public bool MoveAiNodesTo(IReadOnlyDictionary<MissionPickupSource, Vector3> values)
    {
        if (ReadOnlyReason is { } reason) throw new InvalidOperationException(reason);
        if (saving) throw new InvalidOperationException("Wait for the current save to finish.");
        return AcceptTransforms(PreviewAiPositions(values));
    }
}
