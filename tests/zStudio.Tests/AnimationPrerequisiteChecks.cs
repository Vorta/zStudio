using zStudio.Core.Animation;
using Xunit;

namespace zStudio.Tests;

/// <summary>Retail prerequisite truth tables, independent of compiler construction and preview activation.</summary>
public static class AnimationPrerequisiteChecks
{
    public static void Verify(string mission, AnimationPackage package)
    {
        AnimationEntry Entry(string name) => Assert.Single(package.Entries, e => e.Name == name);
        if (mission == "m6")
        {
            string[] directions = ["dflct_f", "dflct_v", "dflct_b"];
            int terminals = 0;
            for (int cell = 0; cell < 3; cell++)
                foreach (var (name, minimum, option, threshold) in new[]
                {
                    ($"azcell_0{cell + 1}_warm", 1, 1, 1), ($"azcell_0{cell + 1}_hot", 2, 1, 2),
                    ($"destroy_azcell_0{cell + 1}", 0, 0, 3),
                })
                {
                    var entry = Entry(name);
                    Assert.Equal(minimum, entry.Bytes[268]);
                    Assert.Equal(new byte[] { 3, 2, 3, 2, 3, 2 }, entry.References[6].Select(r => r.Bytes[4]));
                    Assert.All(entry.References[6], r => Assert.Equal(option, r.I32(0)));
                    Assert.Equal(Enumerable.Range(1, 3).SelectMany(i => new[] { $"defspn_{i}", directions[cell] }),
                        entry.References[6].Select(r => r.Text(12, 28)));
                    foreach (var record in entry.References[6].Where(r => r.Bytes[4] == 2))
                    { Assert.Equal(1, record.I32(8)); terminals++; }
                    // Three directions per deflector: all 27 assignments, tested against each of the nine entries.
                    for (int assignment = 0; assignment < 27; assignment++)
                    {
                        int[] turns = [assignment % 3, assignment / 3 % 3, assignment / 9];
                        Dictionary<string, bool> nodes = [];
                        for (int deflector = 0; deflector < 3; deflector++)
                            for (int direction = 0; direction < 3; direction++)
                                nodes[$"defspn_{deflector + 1}/{directions[direction]}"] = turns[deflector] == direction;
                        Assert.Equal(turns.Count(t => t == cell) >= threshold, Passes(entry, nodes, new Dictionary<string, int>()));
                    }
                }
            Assert.Equal(27, terminals);
            var boss = Entry("destroy_a1boss");
            string[] destructions = ["destroy_azcell_01", "destroy_azcell_02", "destroy_azcell_03"];
            Assert.Equal(destructions, boss.References[6].Select(r => r.Text(8, 32)));
            Assert.All(boss.References[6], r => { Assert.Equal(1, r.Bytes[4]); Assert.Equal(0, r.I32(0)); });
            Assert.Equal(0, boss.Bytes[268]);
            for (int mask = 0; mask < 8; mask++)
            {
                var states = destructions.Select((name, i) => (name, state: (mask & (1 << i)) != 0 ? 0 : 1))
                    .ToDictionary(p => p.name, p => p.state);
                Assert.Equal(mask == 7, Passes(boss, new Dictionary<string, bool>(), states));
            }
        }
        if (mission == "m1")
        {
            foreach (var (name, target, minimum) in new[] { ("one_rocket", "rock1_gull", 1),
                ("two_rocket", "rock2_gull", 2), ("warning_sound", "tcp_gull", 1) })
            {
                var entry = Entry(name);
                var terminal = Assert.Single(entry.References[6], r => r.Bytes[4] == 2);
                Assert.Equal(1, terminal.I32(8)); Assert.Equal(target, terminal.Text(12, 28));
                Assert.Equal(minimum, entry.Bytes[268]);
                Assert.All(entry.References[6], r => Assert.Equal(1, r.I32(0)));
                Assert.Equal(minimum, entry.References[6].Count);
                if (minimum == 2)
                { Assert.Equal(1, entry.References[6][0].Bytes[4]); Assert.Equal("one_rocket", entry.References[6][0].Text(8, 32)); }
                for (int state = 0; state < 4; state++)
                {
                    bool active = (state & 1) != 0, previousSatisfied = (state & 2) != 0;
                    Assert.Equal(active && (minimum == 1 || previousSatisfied), Passes(entry,
                        new Dictionary<string, bool> { [target] = active },
                        new Dictionary<string, int> { ["one_rocket"] = previousSatisfied ? 0 : 1 }));
                }
            }
        }
    }

    // CheckActivationPrereqs 0x45DB20: required mismatches fail; optional matches count toward the entry minimum.
    // Mode 3 resolves a path prefix without counting. LoadZbd resets that path after each mode-2 terminal.
    // Known fixtures must resolve every target; missing keys throw rather than accidentally validating a null binding.
    private static bool Passes(AnimationEntry entry, IReadOnlyDictionary<string, bool> active, IReadOnlyDictionary<string, int> animationStates)
    {
        List<string> path = []; int matches = 0;
        foreach (var record in entry.References[6])
        {
            int mode = record.Bytes[4];
            if (mode == 3) { path.Add(record.Text(12, 28)); continue; }
            bool satisfied;
            if (mode == 1) satisfied = animationStates[record.Text(8, 32)] != 1;
            else
            {
                Assert.Equal(2, mode); path.Add(record.Text(12, 28));
                satisfied = (active[string.Join('/', path)] ? 1 : 0) == record.I32(8); path.Clear();
            }
            if (record.I32(0) == 0) { if (!satisfied) return false; }
            else if (satisfied) matches++;
        }
        Assert.Empty(path);
        return matches >= entry.Bytes[268];
    }
}
