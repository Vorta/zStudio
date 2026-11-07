namespace zStudio.Core.Animation;

/// <summary>MW3 SI rotation conversion. Kept separate from the verified RECOIL compiler arithmetic.</summary>
internal static class Mw3SiMath
{
    public static (float X, float Y, float Z) Spin(SiMath.Quat from, SiMath.Quat to, int fromFrame, int toFrame, float frameRate)
    {
        var delta = SiMath.MultiplyInverse(to, from);
        double x = delta.X, y = delta.Y, z = delta.Z;
        if ((float)(x * x + y * y + z * z) == 0)
        {
            // Native linear tracks multiply the zero logarithm by the duration divisor too.
            float zero = (float)(0.0 * SiMath.InverseDuration(fromFrame, toFrame, frameRate));
            return (zero, zero, zero);
        }
        // The c3 archive re-saved for the official 1.2 patch rounds both intermediate values to float.
        // Use that later observed arithmetic consistently for native SI sources.
        float norm2 = (float)(x * x + y * y + z * z);
        double length = BitConverter.UInt32BitsToSingle((SiMath.Bits(norm2) >> 1) + 0x1FC00000u);
        double angle = (float)(delta.W != 0 ? Math.Atan(length / delta.W) : Math.PI / 2);
        double factor = (float)(angle / length);
        double rho = SiMath.InverseDuration(fromFrame, toFrame, frameRate);
        return ((float)((double)(float)(x * factor) * rho), (float)((double)(float)(y * factor) * rho), (float)((double)(float)(z * factor) * rho));
    }

    /// <summary>Six-decimal native reconstruction keeps channel identity and frame times exact. Base components may
    /// differ by one millionth; rate differences may accumulate to at most two millionths over a segment. Four ULPs
    /// also cover float arithmetic at large magnitudes. This comparison applies only to linear SI channels, never cubic
    /// coefficients, sampled motions, procedural events or RECOIL.</summary>
    internal static bool Equivalent(AnimationKeyframe a, AnimationKeyframe b)
    {
        if (a.Flags != b.Flags || SiMath.Bits(a.Start) != SiMath.Bits(b.Start) || SiMath.Bits(a.End) != SiMath.Bits(b.End)) return false;
        double duration = Math.Abs((double)a.End - a.Start);
        for (int channel = 0; channel < 3; channel++)
        {
            int at = a.ChannelOffset(channel), bt = b.ChannelOffset(channel);
            if (at < 0) continue;
            for (int j = 0; j < (channel == 1 ? 4 : 3); j++)
                if (!Close(a.F32(at + j * 4), b.F32(bt + j * 4), 1e-6)) return false;
            for (int j = 0; j < 3; j++)
                if (!RateClose(a.F32(at + 16 + j * 4), b.F32(bt + 16 + j * 4), duration)) return false;
        }
        return true;
    }

    internal static bool SpinClose((float X, float Y, float Z) a, (float X, float Y, float Z) b, double duration)
        => RateClose(a.X, b.X, duration) && RateClose(a.Y, b.Y, duration) && RateClose(a.Z, b.Z, duration);

    private static bool RateClose(float a, float b, double duration) => Close(a, b, duration > 0 ? 2e-6 / duration : 1e-6);

    private static bool Close(float a, float b, double tolerance)
    {
        if (!float.IsFinite(a) || !float.IsFinite(b)) return false;
        if (a == b) return true;
        double magnitude = Math.Max(Math.Abs((double)a), Math.Abs((double)b));
        double ulp = magnitude == 0 ? float.Epsilon : magnitude - MathF.BitDecrement((float)magnitude);
        return Math.Abs((double)a - b) <= Math.Max(tolerance, 4 * ulp);
    }

    internal readonly record struct Trig(float Sin, float Cos)
    {
        public static Trig At(double angle) => new((float)Math.Sin(angle), (float)Math.Cos(angle));
    }

    public static SiMath.Quat CompileRotation(double alpha, double beta, double gamma)
        => CompileRotation(Trig.At((float)alpha), Trig.At((float)beta), Trig.At((float)gamma));

    internal static SiMath.Quat CompileRotation(Trig a, Trig b, Trig g)
    {
        double sa = a.Sin, ca = a.Cos, sb = b.Sin, cb = b.Cos, sg = g.Sin, cg = g.Cos;
        // MatRotateZ on the identity, then MatRotateY and MatRotateX on its rows (two products per element, so signed
        // zeros survive as in the shipped data).
        double xx = cg, xy = sg, xz = 0, yx = -sg, yy = cg, yz = 0, zx = 0, zy = 0, zz = 1;
        double nxx = (float)(cb * xx - sb * zx), nxy = (float)(cb * xy - sb * zy), nxz = (float)(cb * xz - sb * zz);
        double nzx = (float)(sb * xx + cb * zx), nzy = (float)(sb * xy + cb * zy), nzz = (float)(sb * xz + cb * zz);
        xx = nxx; xy = nxy; xz = nxz; zx = nzx; zy = nzy; zz = nzz;
        double nyx = (float)(ca * yx + sa * zx), nyy = (float)(ca * yy + sa * zy), nyz = (float)(ca * yz + sa * zz);
        nzx = (float)(ca * zx - sa * yx); nzy = (float)(ca * zy - sa * yy); nzz = (float)(ca * zz - sa * yz);
        yx = nyx; yy = nyy; yz = nyz; zx = nzx; zy = nzy; zz = nzz;

        // MW3's matrix extraction uses asin for pitch and atan2 for roll; it preserves the roll hemisphere.
        double pitch, yaw, roll;
        if (Math.Abs(zy) < 1)
        {
            pitch = (float)Math.Asin(-zy);
            yaw = (float)Math.Atan2(zx, zz);
            roll = (float)Math.Atan2(xy, yy);
        }
        else
        {
            pitch = (float)-Math.CopySign(1.570796326795, zy);
            yaw = (float)Math.Atan2(-xz, xx);
            roll = 0;
        }
        double s0 = (float)Math.Sin(yaw * 0.5), c0 = (float)Math.Cos(yaw * 0.5);
        double s1 = (float)Math.Sin(pitch * 0.5), c1 = (float)Math.Cos(pitch * 0.5);
        double s2 = (float)Math.Sin(roll * 0.5), c2 = (float)Math.Cos(roll * 0.5);
        double a0 = (float)(c1 * c0), b0 = (float)(s1 * c0), c0s = (float)(c1 * s0), d0 = (float)(s1 * s0);
        return new((float)(a0 * c2 + d0 * s2), (float)(b0 * c2 + c0s * s2),
            (float)(c0s * c2 - b0 * s2), (float)(a0 * s2 - d0 * c2));
    }
}
