namespace zStudio.Core;

/// <summary>What wakes an AI vehicle by distance (<see cref="AiVehicleActivation"/>).</summary>
public enum AiActivationState { Unavailable, NoNetwork, MissingNetwork, Asleep, Radius }

/// <summary>
/// An AI vehicle's activation radius: the <c>activate_rad</c> of the network its record names, which every vehicle of that
/// network shares. A sleeping vehicle wakes when the local player's vehicle is within that horizontal (XZ) distance of its own
/// position, or when alerted; a radius of 0 or none stored wakes it only when alerted (docs/agents/engine-evidence.md#net_nnzrd).
/// <see cref="Radius"/> is the distance drawn: the stored value's magnitude (the game squares it), 0 unless
/// <see cref="State"/> is <see cref="AiActivationState.Radius"/>. RECOIL only.
/// </summary>
public sealed record AiVehicleActivation(AiActivationState State, int? NetworkNumber, AiNetwork? Network, float Radius)
{
    /// <summary>
    /// Unavailable without a RECOIL tank record with an integer network number; NoNetwork for number 0 (or below); MissingNetwork
    /// when no network of <paramref name="networks"/> has the number; else Asleep (radius 0 or not stored) or Radius.
    /// </summary>
    public static AiVehicleActivation Resolve(MissionCoordinateRecord? record, AiNetworkSnapshot networks)
    {
        if (record is not { Kind: "tank", NetworkId: int number }) return new(AiActivationState.Unavailable, null, null, 0);
        if (number <= 0) return new(AiActivationState.NoNetwork, number, null, 0);
        var network = networks.Networks.FirstOrDefault(n => MissionAiNetworks.TryNetworkNumber(n.Member, out int found) && found == number);
        if (network == null) return new(AiActivationState.MissingNetwork, number, null, 0);
        float radius = Math.Abs(network.ActivationRadius ?? 0);
        return new(radius > 0 ? AiActivationState.Radius : AiActivationState.Asleep, number, network, radius);
    }
}
