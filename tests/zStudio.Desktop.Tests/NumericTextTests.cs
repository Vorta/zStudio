using System.Globalization;
using Recoil.Zbd.Core.Animation;
using Xunit;

namespace Recoil.Zbd.Desktop.Tests;

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
    public void FormattingFieldsDoesNotMutateRecordsAndExponentInputStillWorks()
    {
        var record = AnimationCatalog.Create(10);
        var field = new AnimationField("Initial velocity", 64, AnimationFieldKind.Vector);
        field.Write(record, "1, -2, 3.874302E-07");
        byte[] before = record.Bytes.ToArray();
        string text = field.FormatForEditor(record);
        Assert.Equal("1, -2, 0.0000003874302", text);
        Assert.Equal(before, record.Bytes);
        field.Write(record, text);
        Assert.Equal(before, record.Bytes);
        Assert.Equal("0.0000003874302", new AnimationField("Z", 72, AnimationFieldKind.Float).FormatForEditor(record));
    }

    private static void AssertRoundTrip(float value)
    {
        string text = value.ToEditorText();
        Assert.DoesNotContain("E", text);
        Assert.Equal(BitConverter.SingleToInt32Bits(value),
            BitConverter.SingleToInt32Bits(float.Parse(text, CultureInfo.InvariantCulture)));
    }
}
