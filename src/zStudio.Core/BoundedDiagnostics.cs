using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;

namespace Recoil.Zbd.Core;

/// <summary>Operation-owned warning storage and formatting budget. Formatting is reserved before operands are evaluated.</summary>
internal sealed class BoundedDiagnostics
{
    internal const int MaximumMessageCharacters = 1024, MaximumMessages = 1024,
        MaximumRetainedCharacters = 256 * 1024, MaximumFormattingCharacters = 1024 * 1024;
    internal const string OmissionNotice = "Additional warnings were omitted because the operation's diagnostic budget was exhausted.";
    private sealed class Storage(List<string>? output)
    {
        internal readonly List<string> Messages = output ?? [];
        internal readonly HashSet<string> Seen = new(StringComparer.Ordinal);
        internal int Formatting, Retained, Count;
        internal bool Exhausted;
    }
    private readonly Storage storage;
    private readonly string? source, kind, name;
    internal BoundedDiagnostics(List<string>? output = null)
    {
        storage = new(output);
        // Public trace callers can supply existing notes. Account for and bound them without first cloning
        // the caller's list, so attaching a collector cannot silently reset an operation's retained budget.
        int originalCount = storage.Messages.Count, kept = 0;
        for (int i = 0; i < originalCount; i++)
        {
            if (!HasRoom()) break;
            storage.Formatting += MaximumMessageCharacters;
            string value = JsonData.ShownText(storage.Messages[i], MaximumMessageCharacters - 1);
            if (!storage.Seen.Add(value)) continue;
            storage.Messages[kept++] = value; storage.Retained += value.Length; storage.Count++;
        }
        if (kept < originalCount)
        {
            storage.Messages.RemoveRange(kept, originalCount - kept);
            // Deduplication alone loses no diagnostics; an exhausted budget does.
            if (!HasRoom()) Omit();
        }
    }
    private BoundedDiagnostics(Storage storage, string source, string? kind, string? name)
    { this.storage = storage; this.source = source; this.kind = kind; this.name = name; }
    // Context retains original references; it does not format a prefix for every otherwise silent model.
    internal BoundedDiagnostics WithContext(string source, string? kind = null, string? name = null) => new(storage, source, kind, name);
    internal IReadOnlyList<string> Messages => storage.Messages;
    internal List<string> Snapshot() => [.. storage.Messages];

    private bool Reserve()
    {
        if (storage.Exhausted) return false;
        // Charge a full bounded message, including duplicates. This conservative charge bounds all formatting
        // work even when a displayed operand is shortened or a message is subsequently deduplicated.
        if (!HasRoom())
        { Omit(); return false; }
        storage.Formatting += MaximumMessageCharacters;
        return true;
    }
    private bool HasRoom() => storage.Formatting <= MaximumFormattingCharacters - MaximumMessageCharacters &&
        storage.Count < MaximumMessages - 1 &&
        storage.Retained <= MaximumRetainedCharacters - MaximumMessageCharacters - OmissionNotice.Length;
    private void Omit()
    {
        if (storage.Exhausted) return;
        storage.Exhausted = true;
        if (storage.Seen.Add(OmissionNotice)) storage.Messages.Add(OmissionNotice);
    }
    internal void Add([InterpolatedStringHandlerArgument("")] ref Message message)
    {
        if (message.Text is not { } text) return;
        string value = text.ToString();
        if (!storage.Seen.Add(value)) return;
        storage.Messages.Add(value); storage.Retained += value.Length; storage.Count++;
    }

    /// <summary>At most eight paths, individually shortened before joining. Called inside the gated handler.</summary>
    internal readonly record struct DirectorySummary(IReadOnlyList<string> Paths);
    internal static DirectorySummary DirectoryList(IReadOnlyList<string> paths) => new(paths);
    private static string FormatDirectories(IReadOnlyList<string> paths)
    {
        StringBuilder text = new();
        for (int i = 0; i < Math.Min(8, paths.Count); i++)
        { if (i > 0) text.Append(", "); text.Append(JsonData.ShownText(paths[i], 48)); }
        if (paths.Count > 8) text.Append(CultureInfo.InvariantCulture, $", … ({paths.Count - 8} more)");
        return text.ToString();
    }

    [InterpolatedStringHandler]
    internal ref struct Message
    {
        internal StringBuilder? Text;
        public Message(int literalLength, int formattedCount, BoundedDiagnostics owner, out bool enabled)
        {
            enabled = owner.Reserve(); Text = enabled ? new StringBuilder(MaximumMessageCharacters) : null;
            if (!enabled) return;
            if (owner.source != null) { AppendFormatted(owner.source); AppendLiteral(": "); }
            if (owner.kind != null) { AppendFormatted(owner.kind); AppendLiteral(" "); }
            if (owner.name != null) { AppendFormatted(owner.name); AppendLiteral(": "); }
        }
        public void AppendLiteral(string text) => Append(text);
        // Bound original authored strings before the builder sees them; lookup identities are never modified.
        public void AppendFormatted(string? value) => Append(JsonData.ShownText(value ?? "", 192));
        public void AppendFormatted(DirectorySummary value) => Append(FormatDirectories(value.Paths));
        public void AppendFormatted(int value) => Append(value.ToString(CultureInfo.InvariantCulture));
        private void Append(string text)
        {
            if (Text == null) return;
            int room = MaximumMessageCharacters - Text.Length;
            if (room <= 0)
            {
                if (text.Length > 0)
                {
                    Text.Length--;
                    if (Text.Length > 0 && char.IsHighSurrogate(Text[^1])) Text.Length--;
                    Text.Append('…');
                }
                return;
            }
            if (text.Length <= room) Text.Append(text);
            else Text.Append(JsonData.ShownText(text, room - 1));
        }
    }
}
