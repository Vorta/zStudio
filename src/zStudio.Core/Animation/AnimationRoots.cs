using System.Text;
using zStudio.Core.Formats;

namespace zStudio.Core.Animation;

/// <summary>One name index and matching budget shared by all definitions of a compilation or reconstruction.</summary>
internal sealed class AnimationRoots
{
    private readonly HashSet<string>? names;
    private readonly Dictionary<int, string[]> lengths = [];
    private readonly CancellationToken token;
    private readonly long maximumWork;
    private readonly Dictionary<string, int> occurrences = new(StringComparer.Ordinal);
    private readonly GameVariant game;
    private long work;
    internal AnimationRoots(IReadOnlyCollection<string>? worldNodes, CancellationToken token, long maximumWork = 16_000_000, GameVariant game = GameVariant.Recoil)
    {
        this.game = game;
        this.token = token; this.maximumWork = maximumWork;
        if (worldNodes == null) return;
        names = new(StringComparer.Ordinal);
        foreach (string name in worldNodes)
        {
            token.ThrowIfCancellationRequested(); names.Add(name);
            occurrences[name] = occurrences.GetValueOrDefault(name) + 1;
        }
        foreach (var group in names.GroupBy(n => n.Length))
        {
            token.ThrowIfCancellationRequested(); var ordered = group.ToArray(); Array.Sort(ordered, StringComparer.Ordinal); lengths.Add(group.Key, ordered);
        }
    }
    internal IEnumerable<(string Root, string Digits)> Resolve(AnimationItem definition, Action<string> warn)
    {
        token.ThrowIfCancellationRequested();
        var name = definition.Item("NAME") ?? throw definition.Error("an animation definition needs a NAME.");
        var values = name.Scalars.ToArray();
        if (values.Length > 1)
        {
            foreach (var value in values)
            {
                token.ThrowIfCancellationRequested(); string text = value.Kind == ZrdKind.String ? value.Text : value.Value;
                if (names == null || names.Contains(text)) { yield return (text, ""); yield break; }
            }
            yield break;
        }
        foreach (var value in values)
        {
            string pattern = value.Kind == ZrdKind.String ? value.Text : value.Value;
            // MW3's trailing # binds every same-named object. The generated animation name gets the occurrence
            // suffix, while its root and node operands keep the object's actual name.
            bool instances = game == GameVariant.MechWarrior3 && pattern.EndsWith('#');
            if (instances) pattern = pattern[..^1];
            if (!pattern.Contains('*'))
            {
                if (instances) foreach (var binding in Instances(pattern, "")) yield return binding;
                else yield return (pattern, "");
                continue;
            }
            if (names == null) { warn($"{JsonData.ShownText(definition.Source)}: {JsonData.ShownText(pattern)} names world nodes by pattern; without the world it binds to nothing."); continue; }
            if (!lengths.TryGetValue(pattern.Length, out var candidates)) continue;
            foreach (string candidate in candidates)
            {
                token.ThrowIfCancellationRequested();
                if ((work += 1L + pattern.Length) > maximumWork)
                    throw definition.Error("animation wildcard matching exceeds its work budget; reduce definitions or world names.");
                bool matches = true;
                for (int i = 0; i < pattern.Length; i++)
                    if (pattern[i] == '*' ? !char.IsAsciiDigit(candidate[i]) : pattern[i] != candidate[i]) { matches = false; break; }
                if (!matches) continue;
                StringBuilder digits = new();
                for (int i = 0; i < pattern.Length; i++) if (pattern[i] == '*') digits.Append(candidate[i]);
                if (instances) foreach (var binding in Instances(candidate, digits.ToString())) yield return binding;
                else yield return (candidate, digits.ToString());
            }
        }
        IEnumerable<(string Root, string Digits)> Instances(string actual, string digits)
        {
            for (int i = 0; i < occurrences.GetValueOrDefault(actual); i++)
            {
                token.ThrowIfCancellationRequested();
                if (++work > maximumWork) throw definition.Error("animation instance matching exceeds its work budget.");
                yield return (actual, digits + "#" + i.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
    }
}
