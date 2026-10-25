namespace zStudio.Core.Animation;

/// <summary>Editor frame coordinates only; never a simulation or script compilation rate.</summary>
public static class AnimationTimelineFrames
{
    public const int Rate = 40;
    public const int Origin = 1;
    public const double Step = 1.0 / Rate;
    public static double Frame(double seconds) => seconds * Rate + Origin;
    public static double Seconds(double frame) => (frame - Origin) / Rate;
    public static double Snap(double seconds) => Math.Round(seconds * Rate, MidpointRounding.AwayFromZero) / Rate;
    /// <summary>Containing cell, allowing only float-storage noise at a whole-frame boundary.</summary>
    public static double Cell(double seconds)
    {
        double scaled = seconds * Rate, nearest = Math.Round(scaled);
        float stored = (float)seconds;
        double tolerance = Math.Abs((double)MathF.BitIncrement(stored) - stored) * Rate / 2 + 1e-9;
        return Math.Floor(Math.Abs(scaled - nearest) <= tolerance ? nearest : scaled) + Origin;
    }
}
