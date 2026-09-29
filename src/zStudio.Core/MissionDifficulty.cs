namespace Recoil.Zbd.Core;

public enum MissionDifficulty { Easy = 0, Medium = 1, Hard = 2 }

/// <summary>The requested layout and the resources actually used, including independent fallbacks.</summary>
public sealed record MissionLayoutSelection(MissionDifficulty Difficulty, string AivResource, string VehicleResource, string PickupResource = "puppies.zrd")
{
    public string? MissionArchive { get; init; }
    /// <summary>A remembered mission reader that no longer qualified; MissionArchive is the reported fallback.</summary>
    public string? UnavailableMission { get; init; }
    /// <summary>False for MechWarrior 3, whose authored missions do not use RECOIL's difficulty resource variants.</summary>
    public bool DifficultyApplies { get; init; } = true;
    public static MissionLayoutSelection For(MissionDifficulty difficulty) => difficulty switch
    {
        MissionDifficulty.Easy => new(difficulty, "aiv_easy.zrd", "vehicle_easy.zrd", "puppies_easy.zrd"),
        MissionDifficulty.Medium => new(difficulty, "aiv.zrd", "vehicle.zrd"),
        MissionDifficulty.Hard => new(difficulty, "aiv_hard.zrd", "vehicle_hard.zrd", "puppies_hard.zrd"),
        _ => throw new ArgumentOutOfRangeException(nameof(difficulty))
    };
    public string Label => MissionArchive != null ? $"Authored mission · {Path.GetFileNameWithoutExtension(MissionArchive)}" : DifficultyApplies ? $"Mission start · {Difficulty}" : "Stored world layout";
    public string Description => MissionArchive != null ? $"{Label} · {MissionArchive} · gameplay activation is not simulated" :
        DifficultyApplies ? $"{Label} · {AivResource} · {VehicleResource} · {PickupResource} (all authored pickups)" : $"{Label} · no MechWarrior 3 mission reader · gameplay activation is not simulated";
}
