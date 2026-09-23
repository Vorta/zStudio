namespace Recoil.Zbd.Core.Animation;

public sealed record TextureCycle(IReadOnlyList<string> Textures, float Speed, bool Loop, float InitialFrame = 0)
{
    public string? At(double seconds, int variant = -1)
    {
        if (Textures.Count == 0) return null;
        double start = variant >= 0 ? variant % Textures.Count : InitialFrame;
        double raw = start + seconds * Speed;
        if (!double.IsFinite(raw)) raw = 0;
        // Retail UpdateCycleIfNeeded wraps negative playback, even for a non-looping cycle.
        if (Loop || raw < 0) raw = ((raw % Textures.Count) + Textures.Count) % Textures.Count;
        return Textures[(int)Math.Clamp(Math.Floor(raw + 1e-7), 0, Textures.Count - 1)];
    }
}
