namespace Recoil.Zbd.Core.Animation;

/// <summary>
/// The arithmetic of RECOIL's lost keyframe script compiler, reproduced bit for bit from the shipped
/// <c>anim.zbd</c> streams (all 14,747 keyframes of the 1999 release). Values are held in doubles that carry single
/// precision results: each <c>(float)</c> below is a store to a float in the original, and everything between two
/// stores is evaluated in double precision, as the original's x87 code did.
/// </summary>
internal static class SiMath
{
    /// <summary>A channel is keyed in a segment when it changes by more than this: any position/scale component, or
    /// the rotation's half angle in radians. Every keyed change of the shipped scripts is 2.009e-5 rad or more and every
    /// absorbed drift 1.998e-5 rad or less.</summary>
    public const double Threshold = 1e-5;
    private static readonly double PiF = (float)Math.PI;

    /// <summary>A stored rotation: the quaternion's four floats in file order (W X Y Z).</summary>
    public readonly record struct Quat(float W, float X, float Y, float Z)
    {
        public bool BitEquals(Quat other) =>
            Bits(W) == Bits(other.W) && Bits(X) == Bits(other.X) && Bits(Y) == Bits(other.Y) && Bits(Z) == Bits(other.Z);
    }

    public static uint Bits(float value) => BitConverter.SingleToUInt32Bits(value);

    /// <summary>
    /// The compiler's rotation for a script's Euler angles (radians about X, Y and Z, as decimal text): each angle as a
    /// float; the engine's matrix stack MatRotateZ(γ), MatRotateY(β), MatRotateX(α) with float elements;
    /// zMathMatExtractEulerAngles; zMathQuatFromEuler. Every temporary is stored as a float.
    /// </summary>
    public static Quat CompileRotation(double alpha, double beta, double gamma)
    {
        double a = (float)alpha, b = (float)beta, g = (float)gamma;
        double sa = (float)Math.Sin(a), ca = (float)Math.Cos(a), sb = (float)Math.Sin(b), cb = (float)Math.Cos(b);
        double sg = (float)Math.Sin(g), cg = (float)Math.Cos(g);
        // MatRotateZ on the identity, then MatRotateY and MatRotateX on its rows (two products per element, so signed
        // zeros survive as in the shipped data).
        double xx = cg, xy = sg, xz = 0, yx = -sg, yy = cg, yz = 0, zx = 0, zy = 0, zz = 1;
        double nxx = (float)(cb * xx - sb * zx), nxy = (float)(cb * xy - sb * zy), nxz = (float)(cb * xz - sb * zz);
        double nzx = (float)(sb * xx + cb * zx), nzy = (float)(sb * xy + cb * zy), nzz = (float)(sb * xz + cb * zz);
        xx = nxx; xy = nxy; xz = nxz; zx = nzx; zy = nzy; zz = nzz;
        double nyx = (float)(ca * yx + sa * zx), nyy = (float)(ca * yy + sa * zy), nyz = (float)(ca * yz + sa * zz);
        nzx = (float)(ca * zx - sa * yx); nzy = (float)(ca * zy - sa * yy); nzz = (float)(ca * zz - sa * yz);
        yx = nyx; yy = nyy; yz = nyz; zx = nzx; zy = nzy; zz = nzz;

        // zMathMatExtractEulerAngles
        double yaw = zx == 0 && zz == 0 ? 0 : (float)Math.Atan2(zx, zz);
        double horizontal = (float)Math.Sqrt(zx * zx + zz * zz);
        double pitch = (float)Math.Atan2(-zy, horizontal);
        double s = (float)Math.Sin(-yaw), c = (float)Math.Cos(-yaw);
        double rx0 = (float)(s * xz + c * xx), rx1 = xy, rx2 = (float)(c * xz - s * xx);
        s = (float)Math.Sin(-pitch); c = (float)Math.Cos(-pitch);
        double fx0 = rx0, fx1 = (float)(c * rx1 - s * rx2), fx2 = (float)(s * rx1 + c * rx2);
        double rest = (float)Math.Sqrt(fx0 * fx0 + fx2 * fx2);
        double roll = (float)Math.Atan2(fx1, rest);
        if (yy < 0) roll = (float)(PiF - roll);

        // zMathQuatFromEuler(yaw, pitch, roll)
        double s0 = (float)Math.Sin(yaw * 0.5), c0 = (float)Math.Cos(yaw * 0.5);
        double s1 = (float)Math.Sin(pitch * 0.5), c1 = (float)Math.Cos(pitch * 0.5);
        double s2 = (float)Math.Sin(roll * 0.5), c2 = (float)Math.Cos(roll * 0.5);
        double c1c0 = (float)(c1 * c0), s1c0 = (float)(s1 * c0), c1s0 = (float)(c1 * s0), s1s0 = (float)(s1 * s0);
        return new((float)(s1s0 * s2 + c1c0 * c2), (float)(c1s0 * s2 + s1c0 * c2), (float)(c1s0 * c2 - s1c0 * s2), (float)(c1c0 * s2 - s1s0 * c2));
    }

    /// <summary>Seconds per frame as the compiler stored it: the float of 1 / float(rate).</summary>
    public static double SecondsPerFrame(float frameRate) => (float)(1.0 / frameRate);

    /// <summary>A key's stored time: float(frame) × seconds per frame, stored as a float.</summary>
    public static float KeyTime(int frame, float frameRate) => (float)((double)(float)frame * SecondsPerFrame(frameRate));

    /// <summary>The rate divisor: 1 / (end time − stored start time), with the end time kept wide.</summary>
    public static double InverseDuration(int from, int to, float frameRate)
    {
        double duration = (double)(float)to * SecondsPerFrame(frameRate) - KeyTime(from, frameRate);
        return (float)(1.0 / duration);
    }

    /// <summary>A position or scale component's rate from <paramref name="from"/> to <paramref name="to"/> (floats).</summary>
    public static float ComponentRate(float from, float to, double inverseDuration) => (float)((double)(float)((double)to - from) * inverseDuration);

    /// <summary>zMathQuatMultiplyInverse(a, b) = a · conj(b) in the engine's expression order, stored as floats.</summary>
    public static Quat MultiplyInverse(Quat a, Quat b)
    {
        double aw = a.W, ax = a.X, ay = a.Y, az = a.Z, bw = b.W, bx = b.X, by = b.Y, bz = b.Z;
        return new((float)(bw * aw + bx * ax + ay * by + bz * az), (float)(bw * ax - aw * bx - bz * ay + az * by),
            (float)(bw * ay - aw * by - az * bx + bz * ax), (float)(bw * az - aw * bz - by * ax + ay * bx));
    }

    /// <summary>
    /// The stored spin (half-angle rotation vector per second) turning <paramref name="from"/> into <paramref name="to"/>
    /// over the frames: the log of to · conj(from) with the engine's fast square root, atan(length / w) / length.
    /// </summary>
    public static (float X, float Y, float Z) Spin(Quat from, Quat to, int fromFrame, int toFrame, float frameRate)
    {
        var d = MultiplyInverse(to, from);
        double x = d.X, y = d.Y, z = d.Z, w = d.W;
        float norm2 = (float)(x * x + y * y + z * z);
        if (norm2 == 0) return (0, 0, 0);
        double length = BitConverter.UInt32BitsToSingle((Bits(norm2) >> 1) + 0x1FC00000u);
        double factor = w != 0 ? Math.Atan(length / w) / length : Math.PI / 2 / length;
        double rho = InverseDuration(fromFrame, toFrame, frameRate);
        return ((float)((double)(float)(x * factor) * rho), (float)((double)(float)(y * factor) * rho), (float)((double)(float)(z * factor) * rho));
    }

    /// <summary>The rotation angle (radians) between two stored rotations, as the keying threshold measures it.</summary>
    public static double RotationAngle(Quat from, Quat to)
    {
        var d = MultiplyInverse(to, from);
        double x = d.X, y = d.Y, z = d.Z;
        return 2 * Math.Atan2(Math.Sqrt(x * x + y * y + z * z), Math.Abs((double)d.W));
    }

    /// <summary>Principal Euler angles (α, β, γ) with R = Rz(γ)·Ry(β)·Rx(α), from a quaternion (W X Y Z).</summary>
    public static (double A, double B, double G) Euler(double w, double x, double y, double z)
    {
        double r20 = 2 * (x * z - w * y), r21 = 2 * (y * z + w * x), r22 = 1 - 2 * (x * x + y * y);
        double r10 = 2 * (x * y + w * z), r00 = 1 - 2 * (y * y + z * z);
        return (Math.Atan2(r21, r22), Math.Atan2(-r20, double.Hypot(r00, r10)), Math.Atan2(r10, r00));
    }

    /// <summary>The rotation the angles describe, qz(γ)·qy(β)·qx(α), in double precision (W X Y Z).</summary>
    public static (double W, double X, double Y, double Z) ExactQuaternion(double a, double b, double g)
    {
        double ca = Math.Cos(a / 2), sa = Math.Sin(a / 2), cb = Math.Cos(b / 2), sb = Math.Sin(b / 2), cg = Math.Cos(g / 2), sg = Math.Sin(g / 2);
        return (cg * cb * ca + sg * sb * sa, cg * cb * sa - sg * sb * ca, cg * sb * ca + sg * cb * sa, sg * cb * ca - cg * sb * sa);
    }

    /// <summary>q1 · q2 (W X Y Z), double precision.</summary>
    public static (double W, double X, double Y, double Z) Multiply((double W, double X, double Y, double Z) a, (double W, double X, double Y, double Z) b) =>
        (a.W * b.W - a.X * b.X - a.Y * b.Y - a.Z * b.Z, a.W * b.X + a.X * b.W + a.Y * b.Z - a.Z * b.Y,
         a.W * b.Y - a.X * b.Z + a.Y * b.W + a.Z * b.X, a.W * b.Z + a.X * b.Y - a.Y * b.X + a.Z * b.W);

    /// <summary>The quaternion of a half-angle rotation vector (zMathQuatFromRotationVector), double precision.</summary>
    public static (double W, double X, double Y, double Z) FromRotationVector(double x, double y, double z)
    {
        double n = Math.Sqrt(x * x + y * y + z * z);
        if (n == 0) return (1, 0, 0, 0);
        double s = Math.Sin(n) / n;
        return (Math.Cos(n), x * s, y * s, z * s);
    }
}
