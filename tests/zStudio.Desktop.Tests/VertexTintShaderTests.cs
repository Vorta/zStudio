using zStudio.Rendering;
using Xunit;

namespace zStudio.Desktop.Tests;

public sealed class VertexTintShaderTests
{
    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    public void TextureTintIsViewIndependentAndOnlyUntexturedLitModeShades(bool textured, bool unlit, bool flat)
    {
        // Exercise the production pixel shader without a window or hardware GPU.
        // The triangle has a +Z surface normal, authored RGBA and variable eye direction.
        (float EyeZ, byte Alpha)[] cases = [.. new[] { 1f, .5f, 0f }.SelectMany(eyeZ => new byte[] { 0, 128, 255 }.Select(alpha => (eyeZ, alpha)))];
        var pixels = ShaderTestBench.RenderVertexTintPixels(textured, unlit, flat, cases);
        for (int i = 0; i < cases.Length; i++)
        {
            var (eyeZ, alpha) = cases[i]; byte[] pixel = pixels[i];
            float light = !textured && !unlit ? .5f + .5f * eyeZ : 1;
            float[] expected = [127.5f * light, (textured ? 32 : 63.75f) * light, (textured ? 64 : 255) * light, textured ? alpha * .5f : 127.5f];
            for (int channel = 0; channel < 4; channel++)
                Assert.True(Math.Abs(pixel[channel] - expected[channel]) <= 1,
                    $"Textured={textured}, unlit={unlit}, flat={flat}, eyeZ={eyeZ}, alpha={alpha}, channel={channel}: expected {expected[channel]}, got {pixel[channel]}.");
        }
    }
}
