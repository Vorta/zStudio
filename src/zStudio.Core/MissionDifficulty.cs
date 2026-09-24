namespace Recoil.Zbd.Core;

public enum MissionDifficulty { Easy = 0, Medium = 1, Hard = 2 }

/// <summary>The requested layout and the resources actually used, including independent fallbacks.</summary>
public sealed record MissionLayoutSelection(MissionDifficulty Difficulty, string AivResource, string VehicleResource, string PickupResource = "puppies.zrd")
{
    public static MissionLayoutSelection For(MissionDifficulty difficulty) => difficulty switch
    {
        MissionDifficulty.Easy => new(difficulty, "aiv_easy.zrd", "vehicle_easy.zrd", "puppies_easy.zrd"),
        MissionDifficulty.Medium => new(difficulty, "aiv.zrd", "vehicle.zrd"),
        MissionDifficulty.Hard => new(difficulty, "aiv_hard.zrd", "vehicle_hard.zrd", "puppies_hard.zrd"),
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty))
    };
    public string Label => $"Mission start · {Difficulty}";
    public string Description => $"{Label} · {AivResource} · {VehicleResource} · {PickupResource} (all authored pickups)";
}
