namespace zStudio.Core.Formats;

/// <summary>How a texture pack turns a texture's 8-bit colour channels into its stored RGB565 texels or palette entries.</summary>
public enum TextureDithering
{
    /// <summary>Each texel rounds to the nearest stored colour on its own.</summary>
    None,
    /// <summary>A 4×4 Bayer threshold pattern, fixed to texel positions, so tiling textures stay seamless.</summary>
    Ordered,
    /// <summary>Floyd–Steinberg error diffusion: the smoothest gradients, as fine texel noise.</summary>
    Diffusion,
}

/// <summary>
/// Dithered conversion of a texture's texels to RGB565 words or palette indices. A texel whose colour the stored format
/// already holds exactly (the engine's expansion of a 565 colour, as in most textures extracted from the game; a colour-keyed texture holds green at five bits only) keeps it unless
/// diffused error reaches it, so dithering never changes a texture that is all such colours. Only shown texels are dithered
/// and carry error; the rest convert as without dithering. The result is deterministic.
/// </summary>
internal static class TextureDither
{
    private static readonly int[] Bayer = [0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5];
    /// <summary>The pattern's threshold at a texel, strictly between −½ and ½.</summary>
    private static float Threshold(int x, int y) => (Bayer[(y & 3) * 4 + (x & 3)] + 0.5f) / 16f - 0.5f;
    /// <summary>The engine's bit-replicating expansion of a 5- or 6-bit level to 8 bits (see <see cref="TexturePackBuilder.Rgb565"/>).</summary>
    private static int Expand(int level, int levels) => levels == 63 ? (level << 2) | (level >> 4) : (level << 3) | (level >> 2);

    /// <summary>
    /// Direct texels: RGB565 words, or for a colour-keyed texture the words <see cref="TexturePackBuilder.KeyedTexel"/>
    /// stores (green at five bits with its low bit clear, black as 0x0001, the key 0 for a texel not shown).
    /// </summary>
    internal static ushort[] Direct(DecodedImage image, Func<int, bool> shown, bool keyed, TextureDithering mode, CancellationToken token)
    {
        int width = image.Width, height = image.Height;
        byte[] rgba = image.Rgba;
        ushort[] result = new ushort[width * height];
        int[] levels = [31, keyed ? 31 : 63, 31];
        // A keyed texel's green is stored at five bits in the six-bit field: it shows as that six-bit level (2·q).
        Func<int, int>[] shows = [q => Expand(q, 31), keyed ? q => Expand(2 * q, 63) : q => Expand(q, 63), q => Expand(q, 31)];
        Diffuser? diffuser = mode == TextureDithering.Diffusion ? new(width) : null;
        Span<int> q = stackalloc int[3];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            diffuser?.NextRow();
            for (int x = 0; x < width; x++)
            {
                int p = y * width + x;
                if (!shown(p))
                {
                    result[p] = keyed ? (ushort)0 : TexturePackBuilder.Rgb565(rgba, p);
                    continue;
                }
                for (int c = 0; c < 3; c++)
                {
                    int value = rgba[p * 4 + c], n = levels[c];
                    if (diffuser != null)
                    {
                        // Clamped to what the levels show (a keyed green shows at most 251): error no level can take does not pile up.
                        float target = Math.Clamp(value + diffuser.Error(x, c), shows[c](0), shows[c](n));
                        int low = Below(target, n, shows[c]);
                        q[c] = low < n && shows[c](low + 1) - target < target - shows[c](low) ? low + 1 : low;
                        diffuser.Spread(x, c, target - shows[c](q[c]));
                    }
                    else q[c] = Ordered(value, n, x, y, shows[c]);
                }
                int word = keyed ? (q[0] << 11) | (q[1] << 6) | q[2] : (q[0] << 11) | (q[1] << 5) | q[2];
                result[p] = (ushort)(keyed && word == 0 ? 0x0001 : word);
            }
        }
        return result;
    }

    /// <summary>
    /// A channel's ordered level: between the two stored levels around the value (as expanded), the upper one at the share of
    /// texels its distance gives, so an area keeps its mean; an exactly stored colour keeps its level.
    /// </summary>
    private static int Ordered(int value, int levels, int x, int y, Func<int, int> shows)
    {
        int low = Below(value, levels, shows);
        if (low == levels) return low;
        int below = shows(low), above = shows(low + 1);
        return (value - below) / (float)(above - below) > Threshold(x, y) + 0.5f ? low + 1 : low;
    }
    /// <summary>The highest level that shows at or below <paramref name="value"/> (level 0 below the first).</summary>
    private static int Below(float value, int levels, Func<int, int> shows)
    {
        int low = Math.Clamp((int)(value * levels / 255), 0, levels);
        while (low > 0 && shows(low) > value) low--;
        while (low < levels && shows(low + 1) <= value) low++;
        return low;
    }

    /// <summary>The largest error one texel carries per channel, and the farthest error may move a texel from its own colour.</summary>
    private const float MaximumError = 32, MaximumSpeck = 48;

    /// <summary>
    /// Paletted texels: indices into <paramref name="palette"/> (entry 0 is the transparency key, never chosen for a shown
    /// texel). <paramref name="nearest"/> finds the closest entry to an RGB565 colour, as the undithered conversion does.
    /// </summary>
    internal static byte[] Indexed(DecodedImage image, Func<int, bool> shown, ushort[] palette, Func<ushort, byte> nearest, TextureDithering mode, CancellationToken token)
    {
        int width = image.Width, height = image.Height;
        byte[] rgba = image.Rgba;
        byte[] result = new byte[width * height];
        Dictionary<ushort, byte> exact = [];
        for (int i = 1; i < palette.Length; i++) exact.TryAdd(palette[i], (byte)i);
        // The pages store black as 0x0020 (0 is the key): black texels hold that entry and carry no error.
        int black = exact.TryGetValue(0x0020, out byte substitute) ? substitute : -1;
        // What the page can show, per channel (its black entry as black): a target beyond it leaves error no entry can take.
        Span<float> lowest = stackalloc float[3], highest = stackalloc float[3];
        for (int c = 0; c < 3; c++) { lowest[c] = 255; highest[c] = 0; }
        for (int i = 1; i < palette.Length; i++)
            for (int c = 0; c < 3; c++)
            {
                int entry = i == black ? 0 : Channel(palette[i], c);
                lowest[c] = Math.Min(lowest[c], entry); highest[c] = Math.Max(highest[c], entry);
            }
        if (palette.Length < 2) for (int c = 0; c < 3; c++) { lowest[c] = 0; highest[c] = 255; }
        Dictionary<ushort, byte> cache = [];
        byte Find(ushort color) { if (!cache.TryGetValue(color, out byte index)) cache[color] = index = nearest(color); return index; }
        float spread = mode == TextureDithering.Ordered ? Spacing(palette) : 0;
        Diffuser? diffuser = mode == TextureDithering.Diffusion ? new(width) : null;
        Span<float> target = stackalloc float[3];
        for (int y = 0; y < height; y++)
        {
            token.ThrowIfCancellationRequested();
            diffuser?.NextRow();
            for (int x = 0; x < width; x++)
            {
                int p = y * width + x;
                if (!shown(p)) continue;
                ushort own = TexturePackBuilder.Rgb565(rgba, p);
                byte kept = 0;
                bool held = Expanded(own, rgba, p) && (own == 0 ? black > 0 : exact.TryGetValue(own, out kept));
                if (held && own == 0) kept = (byte)black;
                // The ordered pattern stays within what the page can show. Diffused error is kept whole up to a bound instead:
                // clamping it at the palette's edge would drop one channel's share and bias the others; the bound keeps it
                // from piling up and smearing onward.
                for (int c = 0; c < 3; c++)
                    target[c] = diffuser != null ? rgba[p * 4 + c] + Math.Clamp(diffuser.Error(x, c), -MaximumError, MaximumError)
                        : Math.Clamp(rgba[p * 4 + c] + (held ? 0 : Threshold(x, y) * spread), lowest[c], highest[c]);
                // Diffusion searches the target itself, which error may carry past a channel's limit: rounded to a colour
                // first, error at a limit could never favour the entries nearest it there (a pure red would turn orange).
                byte index = held && (diffuser == null || diffuser.Quiet(x)) ? kept
                    : diffuser != null ? Closest(target)
                    : Find((ushort)(Level(target[0], 31) << 11 | Level(target[1], 63) << 5 | Level(target[2], 31)));
                // Error never turns a texel into a colour far from its own (a speck): it keeps its exact entry, or takes its
                // own nearest one, then.
                if (diffuser != null && Far(index, p)) index = held ? kept : Find(own);
                result[p] = index;
                if (diffuser != null)
                    // A held black texel that kept the black entry is measured against black (the entry is also the exact
                    // colour (0,4,0)); one that error moved to another entry, against what that entry shows.
                    for (int c = 0; c < 3; c++) diffuser.Spread(x, c, target[c] - (held && own == 0 && index == kept ? 0 : Channel(palette[index], c)));
            }
        }
        return result;

        // The weighted distance the page's nearest-colour search uses, with its black entry as black.
        byte Closest(ReadOnlySpan<float> wanted)
        {
            int best = 1; float bestDistance = float.MaxValue;
            for (int i = 1; i < palette.Length; i++)
            {
                float r = wanted[0] - (i == black ? 0 : Channel(palette[i], 0)), g = wanted[1] - (i == black ? 0 : Channel(palette[i], 1)), b = wanted[2] - (i == black ? 0 : Channel(palette[i], 2));
                float d = 3 * r * r + 4 * g * g + 2 * b * b; if (d < bestDistance) { bestDistance = d; best = i; }
            }
            return (byte)best;
        }
        bool Far(byte index, int texel)
        {
            for (int c = 0; c < 3; c++)
                if (Math.Abs((index == black ? 0 : Channel(palette[index], c)) - rgba[texel * 4 + c]) > MaximumSpeck) return true;
            return false;
        }
        static int Channel(ushort color, int c) => c switch { 0 => Expand(color >> 11, 31), 1 => Expand((color >> 5) & 63, 63), _ => Expand(color & 31, 31) };
        static int Level(float value, int levels) => Math.Clamp((int)MathF.Round(value * levels / 255f), 0, levels);
        static bool Expanded(ushort color, byte[] rgba, int p) => Expand(color >> 11, 31) == rgba[p * 4] && Expand((color >> 5) & 63, 63) == rgba[p * 4 + 1] && Expand(color & 31, 31) == rgba[p * 4 + 2];
    }

    /// <summary>The ordered pattern's strength for a palette: the mean distance from each entry to its nearest other entry, in 8-bit units.</summary>
    private static float Spacing(ushort[] palette)
    {
        if (palette.Length < 3) return 0;
        double total = 0;
        for (int i = 1; i < palette.Length; i++)
        {
            double best = double.MaxValue;
            for (int j = 1; j < palette.Length; j++)
            {
                if (i == j) continue;
                double d = 0;
                for (int c = 0; c < 3; c++) { double e = TexturePackWriter.Channel(palette[i], c) - TexturePackWriter.Channel(palette[j], c); d += e * e; }
                best = Math.Min(best, d);
            }
            total += Math.Sqrt(best);
        }
        return (float)Math.Min(64, total / (palette.Length - 1));
    }

    /// <summary>Floyd–Steinberg error rows: 7/16 right, 3/16 below left, 5/16 below, 1/16 below right; left to right.</summary>
    private sealed class Diffuser(int width)
    {
        private float[] current = new float[(width + 2) * 3], next = new float[(width + 2) * 3];
        public void NextRow() { (current, next) = (next, current); Array.Clear(next); }
        public float Error(int x, int channel) => current[(x + 1) * 3 + channel];
        /// <summary>Whether no error has reached the texel.</summary>
        public bool Quiet(int x) => current[(x + 1) * 3] == 0 && current[(x + 1) * 3 + 1] == 0 && current[(x + 1) * 3 + 2] == 0;
        public void Spread(int x, int channel, float error)
        {
            if (error == 0) return;
            current[(x + 2) * 3 + channel] += error * 7 / 16;
            next[x * 3 + channel] += error * 3 / 16;
            next[(x + 1) * 3 + channel] += error * 5 / 16;
            next[(x + 2) * 3 + channel] += error / 16;
        }
    }
}
