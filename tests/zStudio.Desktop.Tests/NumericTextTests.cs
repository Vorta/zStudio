using System.Globalization;
using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Desktop.Tests;

public sealed class NumericTextTests
{
    [Fact]
    public void PlainDecimalsPreserveSmallLargeAndSignedValues()
    {
        Assert.Equal("0.0000003874302", 3.874302E-07f.ToEditorText());
        Assert.Equal("-0.0000003874302", (-3.874302E-07f).ToEditorText());
        Assert.Equal("10000000000", 1E10f.ToEditorText());
        Assert.Equal("-123.5", (-123.5f).ToEditorText());
        Assert.Equal("-0", (-0.0f).ToEditorText());
        foreach (float value in new[] { 0f, -0.0f, float.Epsilon, -float.Epsilon,
                     float.MaxValue, float.MinValue, 3.874302E-07f, -3.874302E-07f, 1E10f })
            AssertRoundTrip(value);
    }

    [Fact]
    public void DecimalExpansionRoundTripsAcrossTheFloatExponentRange()
    {
        var random = new Random(7301);
        for (int i = 0; i < 10000; i++)
        {
            float value = BitConverter.Int32BitsToSingle((int)random.NextInt64(int.MinValue, (long)int.MaxValue + 1));
            if (float.IsFinite(value)) AssertRoundTrip(value);
        }
    }

    [Fact]
    public void ExponentInputWritesTheStoredValueAndItsDecimalTextWritesItBack()
    {
        var record = AnimationCatalog.Create(10);
        var field = new AnimationField("Initial velocity", 64, AnimationFieldKind.Vector);
        field.Write(record, "1, -2, 3.874302E-07");
        byte[] before = record.Bytes.ToArray();
        string text = string.Join(", ", Enumerable.Range(0, 3).Select(axis => record.F32(64 + axis * 4).ToEditorText()));
        Assert.Equal("1, -2, 0.0000003874302", text);
        field.Write(record, text);
        Assert.Equal(before, record.Bytes);
    }

    private static void AssertRoundTrip(float value)
    {
        string text = value.ToEditorText();
        Assert.DoesNotContain("E", text);
        Assert.Equal(BitConverter.SingleToInt32Bits(value),
            BitConverter.SingleToInt32Bits(float.Parse(text, CultureInfo.InvariantCulture)));
    }
}
