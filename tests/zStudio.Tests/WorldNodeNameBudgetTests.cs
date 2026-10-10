using System.Text;
using Recoil.Zbd.Core.Worlds;
using Xunit;

namespace Recoil.Zbd.Tests;

public sealed class WorldNodeNameBudgetTests
{
    [Fact]
    public void RepeatedLargeNamesDoNotEncodeDiscardedTails()
    {
        WorldNode node = new("initial", WorldNodeClass.Object3D);
        string name = new('x', 1_000_000);
        node.Name = name; // Warm the encoding path outside the measurement.
        node.NameField[34] = (byte)'R';
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) node.Name = name;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - start;
        Assert.True(allocated < 64 * 1024, $"Encoding discarded name tails allocated {allocated:N0} bytes.");
        Assert.Equal(new string('x', 34) + "R", node.Name);
        Assert.Equal(0, node.NameField[35]);
    }

    [Fact]
    public void EncodedPrefixTerminatorAndUntouchedResidueMatchTheOriginalNameContract()
    {
        string[] names = ["", "café", "A\0B", new('x', 35), new('x', 36),
            new string('x', 32) + "\ud83d\ude00yz", new string('x', 33) + "\ud83d\ude00yz",
            new string('x', 33) + "\ud83d", new string('x', 33) + "\ude00yz",
            new string('x', 31) + "\u0100\u2013\uff01rest"];
        foreach (string name in names)
        {
            WorldNode node = new("initial", WorldNodeClass.Object3D);
            for (int i = 0; i < node.NameField.Length; i++) node.NameField[i] = (byte)('A' + i % 26);
            byte[] expected = (byte[])node.NameField.Clone();
            byte[] encoded = Encoding.Latin1.GetBytes(name); // Small independent full-input legacy oracle.
            if (encoded.Length >= expected.Length) { encoded.AsSpan(0, 34).CopyTo(expected); expected[35] = 0; }
            else { encoded.CopyTo(expected, 0); expected[encoded.Length] = 0; }
            node.Name = name;
            Assert.Equal(expected, node.NameField);
        }
    }
}
