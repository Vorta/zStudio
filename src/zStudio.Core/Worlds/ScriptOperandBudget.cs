namespace Recoil.Zbd.Core.Worlds;

/// <summary>One execution operation's argument arrays and authored/expanded text work, before allocation.</summary>
internal sealed class ScriptOperandBudget(long maximumReferences = ScriptOperandBudget.MaximumReferences,
    long maximumCharacters = ScriptOperandBudget.MaximumCharacters)
{
    internal const long MaximumReferences = 4_194_304, MaximumCharacters = 64L * 1024 * 1024;
    private readonly long referenceLimit = maximumReferences is >= 0 and <= MaximumReferences ? maximumReferences : throw new ArgumentOutOfRangeException(nameof(maximumReferences));
    private readonly long characterLimit = maximumCharacters is >= 0 and <= MaximumCharacters ? maximumCharacters : throw new ArgumentOutOfRangeException(nameof(maximumCharacters));
    internal long References { get; private set; }
    internal long Characters { get; private set; }
    internal bool Exhausted { get; private set; }
    private Action<long>? reserveCharacters;

    internal void Inspect(IReadOnlyList<string> line, CancellationToken token)
    {
        // Also bounds conditions and skipped lines, which scan operands without constructing an argument array.
        ReserveCharacters(line.Count);
        foreach (string value in line) { token.ThrowIfCancellationRequested(); ReserveCharacters(value.Length); }
    }

    internal void Inspect(string value, CancellationToken token, int passes)
    {
        token.ThrowIfCancellationRequested();
        if (passes <= 0) throw new ArgumentOutOfRangeException(nameof(passes));
        // Reserve repeated normalization/hash passes before any of them processes the authored operand.
        ReserveCharacters((long)value.Length * passes);
    }

    internal string[] Expand(IReadOnlyList<string> line, IReadOnlyDictionary<string, string> macros, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        long count = Math.Max(0, line.Count - 1);
        // Array overhead plus the instruction/provenance record that may retain it, in reference-sized units.
        long units = count + 12;
        if (Exhausted || units > referenceLimit - References) Refuse();
        References += units;
        string[] args = new string[(int)count];
        for (int i = 0; i < args.Length; i++)
        {
            token.ThrowIfCancellationRequested();
            args[i] = ScriptConditions.Expand(line[i + 1], macros, reserveCharacters ??= ReserveCharacters);
        }
        return args;
    }

    private void ReserveCharacters(long count)
    {
        if (Exhausted || count > characterLimit - Characters) Refuse();
        Characters += count;
    }

    private void Refuse()
    {
        Exhausted = true;
        throw new InvalidDataException("The scripts exceed the aggregate executed operand storage or text work limit. Reduce repeated wide instructions or macro expansions, or split the source operation into fewer missions.");
    }
}
