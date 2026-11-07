namespace zStudio.Core.Animation;

/// <summary>Bounded deterministic inverse for MW3 SI rotations. The writer rounds the recovered angles to six decimals.</summary>
internal static class Mw3SiRotationSolver
{
    internal readonly record struct Triple(double A, double B, double G)
    {
        public SiMath.Quat Compile() => Mw3SiMath.CompileRotation(A, B, G);
    }

    internal static Triple? Key(SiMath.Quat target, SiRotationSolver.Budget budget, CancellationToken token)
        => Canonical(Search((target.W, target.X, target.Y, target.Z), q => q.BitEquals(target), budget, token));

    private static Triple? Canonical(Triple? value) => value is { } t ? new(Six(t.A), Six(t.B), Six(t.G)) : null;
    private static double Six(double value)
    {
        double six = Math.Round(value, 6);
        return SiMath.Bits((float)six) == SiMath.Bits((float)value) ? six : value;
    }

    internal static Triple? End(Triple start, (float X, float Y, float Z) spin, int from, int to, float rate,
        SiRotationSolver.Budget budget, CancellationToken token, Triple? knownEnd = null)
    {
        var q0 = start.Compile();
        bool Ok(SiMath.Quat q)
        {
            var got = Mw3SiMath.Spin(q0, q, from, to, rate);
            return Mw3SiMath.SpinClose(got, spin, Math.Abs((double)SiMath.KeyTime(to, rate) - SiMath.KeyTime(from, rate)));
        }
        budget.Charge(1);
        if (Ok(start.Compile())) return start;
        budget.Charge(1);
        if (knownEnd is { } known && Ok(known.Compile())) return known;
        double rho = SiMath.InverseDuration(from, to, rate);
        double x = spin.X / rho, y = spin.Y / rho, z = spin.Z / rho;
        var original = ((double)q0.W, (double)q0.X, (double)q0.Y, (double)q0.Z);
        var estimate = SiMath.Multiply(SiMath.FromRotationVector(x, y, z), original);
        for (int i = 0; i < 40; i++)
        {
            token.ThrowIfCancellationRequested();
            var got = Mw3SiMath.Spin(q0, new((float)estimate.W, (float)estimate.X, (float)estimate.Y, (float)estimate.Z), from, to, rate);
            x += (spin.X - (double)got.X) / rho; y += (spin.Y - (double)got.Y) / rho; z += (spin.Z - (double)got.Z) / rho;
            estimate = SiMath.Multiply(SiMath.FromRotationVector(x, y, z), original);
        }
        var precise = Search(estimate, Ok, budget, token);
        if (precise != null || budget.Exhausted) return Canonical(precise);
        var grid = SiRotationSolver.Search(estimate, new(SiRotationSolver.Round(start.A), SiRotationSolver.Round(start.B), SiRotationSolver.Round(start.G)),
            t => Ok(Mw3SiMath.CompileRotation((float)(t.A / 1e6), (float)(t.B / 1e6), (float)(t.G / 1e6))), budget, token, end: true);
        return grid is { } end ? Canonical(new((float)(end.A / 1e6), (float)(end.B / 1e6), (float)(end.G / 1e6))) : null;
    }

    private static Triple? Search((double W, double X, double Y, double Z) q, Func<SiMath.Quat, bool> ok,
        SiRotationSolver.Budget budget, CancellationToken token)
    {
        long batch = 0;
        bool Try(Triple t)
        {
            if ((batch++ & 1023) == 0)
            {
                token.ThrowIfCancellationRequested();
                budget.Charge(1024);
            }
            return !budget.Exhausted && ok(t.Compile());
        }
        // The serialized precision is part of the search, rather than a rounding step after finding a quaternion.
        static Triple Text(Triple t) => new(Math.Round(t.A, 12), Math.Round(t.B, 12), Math.Round(t.G, 12));
        var e = SiMath.Euler(q.W, q.X, q.Y, q.Z);
        if (!double.IsFinite(e.A + e.B + e.G)) return null;
        List<Triple> forms = [];
        foreach (var b in new[] { new Triple(e.A, e.B, e.G), new Triple(e.A > 0 ? e.A - Math.PI : e.A + Math.PI,
            (e.B > 0 ? Math.PI : -Math.PI) - e.B, e.G > 0 ? e.G - Math.PI : e.G + Math.PI) })
            foreach (var turns in (from a in new[] { 0, -1, 1 } from beta in new[] { 0, -1, 1 } from g in new[] { 0, -1, 1 }
                                  orderby Math.Abs(a) + Math.Abs(beta) + Math.Abs(g) select (a, beta, g)))
                forms.Add(new(b.A + turns.a * 2 * Math.PI, b.B + turns.beta * 2 * Math.PI, b.G + turns.g * 2 * Math.PI));

        foreach (var f in forms)
        {
            token.ThrowIfCancellationRequested();
            var centre = new Triple(Math.Round(f.A, 6), Math.Round(f.B, 6), Math.Round(f.G, 6));
            foreach (int a in Steps(3)) foreach (int b in Steps(3)) foreach (int g in Steps(3))
            {
                if (budget.Exhausted) return null;
                var candidate = Text(new(centre.A + a / 1e6, centre.B + b / 1e6, centre.G + g / 1e6));
                if (Try(candidate)) return candidate;
                var floats = new Triple((float)candidate.A, (float)candidate.B, (float)candidate.G);
                if (Try(floats)) return floats;
            }
        }
        // Float cells near the inverse; all components are serialized with enough digits to retain that cell.
        foreach (var f in forms)
        {
            int[] bits = [BitConverter.SingleToInt32Bits((float)f.A), BitConverter.SingleToInt32Bits((float)f.B), BitConverter.SingleToInt32Bits((float)f.G)];
            foreach (int a in Steps(8)) foreach (int b in Steps(8)) foreach (int g in Steps(8))
            {
                if (budget.Exhausted) return null;
                var candidate = new Triple(BitConverter.Int32BitsToSingle(bits[0] + a), BitConverter.Int32BitsToSingle(bits[1] + b), BitConverter.Int32BitsToSingle(bits[2] + g));
                if (Try(candidate)) return candidate;
            }
        }
        if (Math.Abs(Math.Cos(e.B)) < 0.2)
        {
            var legacyGrid = SiRotationSolver.Search(q, null, t => ok(Mw3SiMath.CompileRotation((float)(t.A / 1e6), (float)(t.B / 1e6), (float)(t.G / 1e6))), budget, token, end: false);
            if (legacyGrid is { } found) return new((float)(found.A / 1e6), (float)(found.B / 1e6), (float)(found.G / 1e6));
        }
        // A spinning source object can wind through many complete turns. Reducing it to a nearby Euler branch
        // changes the input float's sine/cosine rounding cells, so recover those turns before widening tiny angles.
        foreach (int winding in Steps(128))
        foreach (var branch in new[] { forms[0], forms[27] })
        foreach (int turnA in Steps(1)) foreach (int turnB in Steps(1))
        {
            double ca = Math.Round(branch.A + turnA * 2 * Math.PI, 6);
            double cb = Math.Round(branch.B + turnB * 2 * Math.PI, 6);
            double cg = Math.Round(branch.G + winding * 2 * Math.PI, 6);
            var aa = MicroCells(ca); var bb = MicroCells(cb); var gg = MicroCells(cg);
            foreach (var a in aa) foreach (var b in bb) foreach (var g in gg)
            {
                if ((batch++ & 1023) == 0) { token.ThrowIfCancellationRequested(); budget.Charge(1024); }
                if (budget.Exhausted) return null;
                if (ok(Mw3SiMath.CompileRotation(a.Trig, b.Trig, g.Trig))) return new(a.Angle, b.Angle, g.Angle);
            }
        }
        // Matrix cancellation can move a small angle by many ULPs. Widen the finest cell dimension separately.
        foreach (var f in new[] { forms[0], forms[27] })
        {
            double[] angles = [f.A, f.B, f.G];
            int fine = Enumerable.Range(0, 3).MinBy(i => Math.Abs(angles[i]));
            int second = (fine + 1) % 3, third = (fine + 2) % 3;
            int[] bits = [.. angles.Select(a => BitConverter.SingleToInt32Bits((float)a))];
            foreach (int a in Steps(4096)) foreach (int b in Steps(8)) foreach (int g in Steps(8))
            {
                if (budget.Exhausted) return null;
                double va = BitConverter.Int32BitsToSingle(bits[fine] + a);
                double vb = BitConverter.Int32BitsToSingle(bits[second] + b);
                double vg = BitConverter.Int32BitsToSingle(bits[third] + g);
                var candidate = fine == 0 ? new Triple(va, vb, vg) : fine == 1 ? new(vg, va, vb) : new(vb, vg, va);
                if (Try(candidate)) return candidate;
            }
        }
        return null;
    }

    private readonly record struct Cell(double Angle, Mw3SiMath.Trig Trig);
    private static Cell[] MicroCells(double centre) => [.. Steps(3).Select(i => centre + i / 1e6)
        .Select(angle => new Cell(angle, Mw3SiMath.Trig.At((float)angle)))];

    private static IEnumerable<int> Steps(int radius)
    {
        yield return 0;
        for (int i = 1; i <= radius; i++) { yield return -i; yield return i; }
    }
}
