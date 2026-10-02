namespace Recoil.Zbd.Core.Animation;

/// <summary>
/// Finds the six-decimal Euler angles a script held for a compiled rotation. The exporter printed Softimage's raw
/// angles (they may lie beyond ±π), so both equivalent Euler forms, whole-turn shifts of each angle and neighbouring
/// six-decimal values are tried through the compiler's own arithmetic (<see cref="SiMath.CompileRotation"/>); only
/// triples that reproduce the stored bits count. Near a heading of ±90° the first and third angles trade along a line
/// and the stored bits pin their split only to a band, so that line is walked, at exactly ±90° turn by turn.
/// Every search visits its candidates in a fixed order and returns the first hit in that order, also when it runs in
/// parallel, so the result is deterministic. A <see cref="Budget"/> bounds the work of the searches behind one script.
/// </summary>
internal static class SiRotationSolver
{
    private const long Micro = 1_000_000;
    private static readonly double TwoPi = 2 * Math.PI;
    // Below this |cos β| the first and third angles are poorly determined and the trade-off line is walked; below the
    // second the heading is exactly ±90° as six decimals print it (cos 1.570796 ≈ 3.3e-7).
    private const double NearGimbal = 0.2, AtGimbal = 1e-4;

    /// <summary>Angles in millionths of a radian, as the script prints them.</summary>
    public readonly record struct Triple(long A, long B, long G)
    {
        public SiMath.Quat Compile() => SiMath.CompileRotation(A / 1e6, B / 1e6, G / 1e6);
        public long Distance(Triple other) => Math.Abs(A - other.A) + Math.Abs(B - other.B) + Math.Abs(G - other.G);
    }

    /// <summary>
    /// Candidate evaluations allowed for one script. Searches are charged for what they examined up to their first find
    /// in search order, never for the speculative work of parallel blocks, so whether a script runs out does not depend
    /// on scheduling; no stage starts once it has run out. The heaviest shipped script needs about 131 million (all 77
    /// of 1999 about 550 million); a script whose keyframes need far more (keyframes no script came from, or thousands of
    /// keys at exactly ±90°) is not worth searching further.
    /// </summary>
    public sealed class Budget(long evaluations = 2_000_000_000)
    {
        private long remaining = evaluations;
        public bool Exhausted => Interlocked.Read(ref remaining) < 0;
        public void Charge(long count) => Interlocked.Add(ref remaining, -count);
    }

    /// <summary>The triple compiling to <paramref name="target"/> exactly, nearest <paramref name="prefer"/> (the previous key) among equal finds.</summary>
    public static Triple? Key(SiMath.Quat target, Triple? prefer, Budget budget, CancellationToken token)
    {
        bool Ok(Triple t) => t.Compile().BitEquals(target);
        return Search((target.W, target.X, target.Y, target.Z), prefer, Ok, budget, token, end: false);
    }

    /// <summary>
    /// The triple whose rotation the segment starting at <paramref name="start"/> reaches with the stored
    /// <paramref name="spin"/>: the end value of a channel that the next key does not list.
    /// </summary>
    public static Triple? End(Triple start, (float X, float Y, float Z) spin, int from, int to, float frameRate, Budget budget, CancellationToken token)
    {
        var q0 = start.Compile();
        uint sx = SiMath.Bits(spin.X), sy = SiMath.Bits(spin.Y), sz = SiMath.Bits(spin.Z);
        bool Ok(Triple t)
        {
            var s = SiMath.Spin(q0, t.Compile(), from, to, frameRate);
            return SiMath.Bits(s.X) == sx && SiMath.Bits(s.Y) == sy && SiMath.Bits(s.Z) == sz;
        }
        budget.Charge(1);
        if (Ok(start)) return start;
        // The rate uses the engine's fast square root: invert the actual rate function, correcting the half-angle
        // vector until the computed spin matches the stored one.
        double rho = SiMath.InverseDuration(from, to, frameRate);
        double hx = spin.X / rho, hy = spin.Y / rho, hz = spin.Z / rho;
        var q0d = ((double)q0.W, (double)q0.X, (double)q0.Y, (double)q0.Z);
        var estimate = SiMath.Multiply(SiMath.FromRotationVector(hx, hy, hz), q0d);
        double scale = 1 + Math.Max(Math.Abs((double)spin.X), Math.Max(Math.Abs((double)spin.Y), Math.Abs((double)spin.Z)));
        for (int i = 0; i < 40; i++)
        {
            var got = SiMath.Spin(q0, new((float)estimate.W, (float)estimate.X, (float)estimate.Y, (float)estimate.Z), from, to, frameRate);
            double ex = spin.X - (double)got.X, ey = spin.Y - (double)got.Y, ez = spin.Z - (double)got.Z;
            if (Math.Max(Math.Abs(ex), Math.Max(Math.Abs(ey), Math.Abs(ez))) <= 1e-9 * scale) break;
            hx += ex / rho; hy += ey / rho; hz += ez / rho;
            estimate = SiMath.Multiply(SiMath.FromRotationVector(hx, hy, hz), q0d);
        }
        if (!double.IsFinite(estimate.W + estimate.X + estimate.Y + estimate.Z)) return null;
        return Search(estimate, start, Ok, budget, token, end: true);
    }

    private static Triple? Search((double W, double X, double Y, double Z) estimate, Triple? prefer, Func<Triple, bool> ok, Budget budget, CancellationToken token, bool end)
    {
        var e = SiMath.Euler(estimate.W, estimate.X, estimate.Y, estimate.Z);
        double conditioning = Math.Abs(Math.Cos(e.B));
        // Equivalent forms: the two Euler branches, each with whole turns added to any angle (fewest turns first).
        List<(double A, double B, double G)> forms = [];
        foreach (var b in Branches(e))
            foreach (var (ka, kb, kg) in Turns())
                forms.Add((b.A + ka * TwoPi, b.B + kb * TwoPi, b.G + kg * TwoPi));
        var centres = forms.Select(f => (Form: f, Centre: new Triple(Round(f.A), Round(f.B), Round(f.G)))).ToList();
        var nearest = prefer is { } preferred ? [.. centres.OrderBy(c => c.Centre.Distance(preferred))] : centres;
        var box = Box(3);
        long examined = 0;
        bool Try(Triple t) { examined++; return ok(t); }

        if (!end)
        {
            // 1. A key: each form's ±1 neighbourhood; the first find of each form, and among them the one nearest the
            //    previous key.
            Triple? best = null;
            foreach (var (_, centre) in centres)
                foreach (var (da, db, dg) in Product)
                {
                    Triple t = new(centre.A + da, centre.B + db, centre.G + dg);
                    if (!Try(t)) continue;
                    if (prefer is not { } p) { budget.Charge(examined); return t; }
                    if (best is not { } b || t.Distance(p) < b.Distance(p)) best = t;
                    break;
                }
            budget.Charge(examined); examined = 0;
            if (best != null) return best;
            // 2. Near ±90° heading: from the eight forms nearest the previous key, step the first angle outward with the
            //    third following it along the trade-off line, the heading and the cross direction within ±3.
            if (conditioning < NearGimbal)
                foreach (var (_, centre) in nearest.Take(8))
                {
                    token.ThrowIfCancellationRequested();
                    if (budget.Exhausted) return null;
                    int s = Math.Sin(centre.B / 1e6) > 0 ? 1 : -1;
                    if (First(2 * 4_000 + 1, 49, k =>
                    {
                        long t = Step(k);
                        for (int db = -3; db <= 3; db++)
                            for (int p = -3; p <= 3; p++)
                            {
                                Triple c = new(centre.A + t, centre.B + db, centre.G + s * t + p);
                                if (ok(c)) return c;
                            }
                        return null;
                    }, budget, token) is { } found) return found;
                }
        }
        // 3. Every form's ±3 neighbourhood, nearest first (for an end value the forms nearest the segment's start).
        if (budget.Exhausted) return null;
        foreach (var (_, centre) in nearest)
        {
            token.ThrowIfCancellationRequested();
            foreach (var (da, db, dg) in box)
            {
                Triple t = new(centre.A + da, centre.B + db, centre.G + dg);
                if (Try(t)) { budget.Charge(examined); return t; }
            }
        }
        budget.Charge(examined);
        if (conditioning >= NearGimbal) return null;

        // 4. Near ±90° heading: walk the trade-off line through angles fitted to the estimate, from the three nearest
        //    forms (an end value), then from each Euler branch with the heading shifted by up to a turn and from the
        //    eight nearest forms, ever further. At exactly ±90° the line has no slope to follow; step 5 handles it.
        if (end)
            foreach (int width in new[] { 3_000, 40_000 })
                foreach (var (form, _) in nearest.Take(3))
                {
                    token.ThrowIfCancellationRequested();
                    if (budget.Exhausted) return null;
                    if (Line(estimate, form, width, 2, 2, ok, budget, token) is { } found) return found;
                }
        if (conditioning >= 1e-6)
        {
            List<(double A, double B, double G)> starts = [.. from b in Branches(e) from kb in new[] { 0, -1, 1 } select (b.A, b.B + kb * TwoPi, b.G)];
            starts.AddRange(nearest.Take(8).Select(f => f.Form));
            foreach (int width in new[] { 4_000, 40_000 })
                foreach (var start in starts)
                {
                    token.ThrowIfCancellationRequested();
                    if (budget.Exhausted) return null;
                    if (Line(estimate, start, width, 3, 3, ok, budget, token) is { } found) return found;
                }
        }
        if (conditioning >= AtGimbal) return null;

        // 5. Exactly ±90°: the angles are small apart from whole turns, so walk each turn of the first and third angle
        //    within a quarter radian of the split points (first angle 0, third angle 0, and for an end value the fitted
        //    first angle).
        HashSet<(long, long)> seen = [];
        foreach (var branch in Branches(e))
            foreach (int kb in new[] { 0, -1, 1 })
            {
                var fit = Fit(estimate, (branch.A, branch.B + kb * TwoPi, branch.G));
                if (!Finite(fit)) continue;
                int s = Math.Sin(fit.B) > 0 ? 1 : -1;
                double combination = fit.A - s * fit.G;
                if (!seen.Add((Round(fit.B), Round(combination) / 10))) continue;
                List<double> splits = [0, combination];
                if (end) splits.Add(fit.A);
                foreach (double centre in splits)
                    foreach (int ka in new[] { 0, -1, 1 })
                        foreach (int kg in new[] { 0, -1, 1 })
                        {
                            token.ThrowIfCancellationRequested();
                            if (budget.Exhausted) return null;
                            if (Turn(fit.B, combination, s, Round(centre + ka * TwoPi), kg * TwoPi, 250_000, ok, budget, token) is { } found) return found;
                        }
            }
        return null;
    }

    /// <summary>Candidates along the trade-off line through the fitted angles: first-angle steps outward from the fit.</summary>
    private static Triple? Line((double W, double X, double Y, double Z) estimate, (double A, double B, double G) start, int width, int betaSpread, int crossSpread, Func<Triple, bool> ok, Budget budget, CancellationToken token)
    {
        var fit = Fit(estimate, start);
        if (!Finite(fit)) return null;
        int s = Math.Sin(fit.B) > 0 ? 1 : -1;
        double combination = fit.A - s * fit.G;
        long a0 = Round(fit.A), b0 = Round(fit.B);
        return First(2L * width + 1, (2 * betaSpread + 1) * (2 * crossSpread + 1), k =>
        {
            long a = a0 + Step(k);
            double gIdeal = (a / 1e6 - combination) * s;
            for (int db = -betaSpread; db <= betaSpread; db++)
                for (int p = -crossSpread; p <= crossSpread; p++)
                {
                    Triple t = new(a, b0 + db, Round(gIdeal) + p);
                    if (ok(t)) return t;
                }
            return null;
        }, budget, token);
    }

    /// <summary>One turn of the line: first angles within <paramref name="half"/> millionths of <paramref name="centre"/>, nearest first.</summary>
    private static Triple? Turn(double beta, double combination, int s, long centre, double thirdTurn, long half, Func<Triple, bool> ok, Budget budget, CancellationToken token)
    {
        long b0 = Round(beta);
        return First(2 * half + 1, 9, k =>
        {
            long a = centre + Step(k);
            long g0 = Round((a / 1e6 - combination) * s + thirdTurn);
            for (int db = -1; db <= 1; db++)
                for (int p = -1; p <= 1; p++)
                {
                    Triple t = new(a, b0 + db, g0 + p);
                    if (ok(t)) return t;
                }
            return null;
        }, budget, token);
    }

    /// <summary>
    /// The find with the lowest ordinal in [0, <paramref name="count"/>), searched in parallel blocks. The budget is
    /// charged for the ordinals up to the find (or all of them), each worth <paramref name="cost"/> candidates, which
    /// does not depend on how the blocks were scheduled.
    /// </summary>
    private static Triple? First(long count, int cost, Func<long, Triple?> probe, Budget budget, CancellationToken token)
    {
        const long Block = 4096;
        long bestOrdinal = long.MaxValue;
        Triple? best = null;
        if (count <= Block)
        {
            // Small searches run inline.
            for (long k = 0; k < count; k++) if (probe(k) is { } t) { bestOrdinal = k; best = t; break; }
        }
        else
        {
            long blocks = (count + Block - 1) / Block;
            object gate = new();
            Parallel.For(0L, blocks, new ParallelOptions { CancellationToken = token }, block =>
            {
                long from = block * Block, to = Math.Min(count, from + Block);
                for (long k = from; k < to; k++)
                {
                    if (k >= Interlocked.Read(ref bestOrdinal)) return;
                    if (probe(k) is { } t)
                    {
                        lock (gate) if (k < Interlocked.Read(ref bestOrdinal)) { Interlocked.Exchange(ref bestOrdinal, k); best = t; }
                        return;
                    }
                }
            });
        }
        budget.Charge((best == null ? count : bestOrdinal + 1) * cost);
        return best;
    }

    private static bool Finite((double A, double B, double G) v) =>
        double.IsFinite(v.A) && double.IsFinite(v.B) && double.IsFinite(v.G) && Math.Abs(v.A) < 1e6 && Math.Abs(v.B) < 1e6 && Math.Abs(v.G) < 1e6;

    /// <summary>Least-squares Euler angles for a quaternion (sign-free), by Gauss-Newton from <paramref name="start"/>.</summary>
    private static (double A, double B, double G) Fit((double W, double X, double Y, double Z) q, (double A, double B, double G) start)
    {
        double[] x = [start.A, start.B, start.G];
        double[] target = [q.W, q.X, q.Y, q.Z];
        for (int iteration = 0; iteration < 30; iteration++)
        {
            double[] m = Exact(x);
            double sign = m[0] * target[0] + m[1] * target[1] + m[2] * target[2] + m[3] * target[3] >= 0 ? 1 : -1;
            double[] r = new double[4];
            for (int k = 0; k < 4; k++) r[k] = m[k] - sign * target[k];
            double[,] jacobian = new double[3, 4];
            for (int i = 0; i < 3; i++)
            {
                double[] xp = [x[0], x[1], x[2]]; xp[i] += 1e-7;
                double[] mp = Exact(xp);
                for (int k = 0; k < 4; k++) jacobian[i, k] = (mp[k] - m[k]) / 1e-7;
            }
            double[,] a = new double[3, 3]; double[] g = new double[3];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    double sum = i == j ? 1e-12 : 0;
                    for (int k = 0; k < 4; k++) sum += jacobian[i, k] * jacobian[j, k];
                    a[i, j] = sum;
                }
                for (int k = 0; k < 4; k++) g[i] += jacobian[i, k] * r[k];
            }
            for (int i = 0; i < 3; i++)
            {
                int pivot = i;
                for (int k = i + 1; k < 3; k++) if (Math.Abs(a[k, i]) > Math.Abs(a[pivot, i])) pivot = k;
                if (pivot != i)
                {
                    for (int j = 0; j < 3; j++) (a[i, j], a[pivot, j]) = (a[pivot, j], a[i, j]);
                    (g[i], g[pivot]) = (g[pivot], g[i]);
                }
                for (int k = i + 1; k < 3; k++)
                {
                    double f = a[k, i] / a[i, i];
                    for (int j = i; j < 3; j++) a[k, j] -= f * a[i, j];
                    g[k] -= f * g[i];
                }
            }
            double[] d = new double[3];
            for (int i = 2; i >= 0; i--)
            {
                double sum = g[i];
                for (int j = i + 1; j < 3; j++) sum -= a[i, j] * d[j];
                d[i] = sum / a[i, i];
            }
            if (!double.IsFinite(d[0]) || !double.IsFinite(d[1]) || !double.IsFinite(d[2])) break;
            for (int i = 0; i < 3; i++) x[i] -= d[i];
            if (Math.Max(Math.Abs(d[0]), Math.Max(Math.Abs(d[1]), Math.Abs(d[2]))) < 1e-12) break;
        }
        return (x[0], x[1], x[2]);

        static double[] Exact(double[] v) { var q = SiMath.ExactQuaternion(v[0], v[1], v[2]); return [q.W, q.X, q.Y, q.Z]; }
    }

    private static IEnumerable<(double A, double B, double G)> Branches((double A, double B, double G) e)
    {
        yield return e;
        yield return (e.A > 0 ? e.A - Math.PI : e.A + Math.PI, (e.B > 0 ? Math.PI : -Math.PI) - e.B, e.G > 0 ? e.G - Math.PI : e.G + Math.PI);
    }

    /// <summary>Whole-turn shifts of the three angles within one turn, fewest turns first.</summary>
    private static IEnumerable<(int, int, int)> Turns() =>
        from a in new[] { -1, 0, 1 } from b in new[] { -1, 0, 1 } from c in new[] { -1, 0, 1 }
        orderby Math.Abs(a) + Math.Abs(b) + Math.Abs(c), a, b, c
        select (a, b, c);

    /// <summary>The offset an outward walk visits at ordinal <paramref name="k"/>: 0, -1, 1, -2, 2, ...</summary>
    private static long Step(long k) => k == 0 ? 0 : (k + 1) / 2 * (k % 2 == 1 ? -1 : 1);

    /// <summary>Offsets within ±1 millionth per angle, in lexicographic order.</summary>
    private static readonly (int, int, int)[] Product = [.. from a in new[] { -1, 0, 1 } from b in new[] { -1, 0, 1 } from c in new[] { -1, 0, 1 } select (a, b, c)];

    /// <summary>Offsets within ±<paramref name="spread"/> millionths per angle, nearest first.</summary>
    private static List<(int, int, int)> Box(int spread) =>
        [.. from a in Enumerable.Range(-spread, 2 * spread + 1) from b in Enumerable.Range(-spread, 2 * spread + 1) from c in Enumerable.Range(-spread, 2 * spread + 1)
            orderby Math.Abs(a) + Math.Abs(b) + Math.Abs(c), a, b, c
            select (a, b, c)];

    public static long Round(double radians) => (long)Math.Round(radians * Micro, MidpointRounding.ToEven);
}
