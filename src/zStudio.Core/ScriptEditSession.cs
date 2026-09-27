using Recoil.Zbd.Core.Formats;

namespace Recoil.Zbd.Core;

public sealed class ScriptEditSession : ContentEditSession
{
    public PreparedScriptPackage Package => (PreparedScriptPackage)Current.State;
    public ScriptEditSession(ZbdDocument document) : base(document, document.Scripts ?? throw new InvalidDataException("An intact v7 prepared-script pack is required.")) { }
    public PreparedScriptEntry Entry(Guid id) => Package.Entries.SingleOrDefault(e => e.Id == id) ?? throw new InvalidDataException("The script entry no longer exists.");
    public Task<PreparedContentEdit> PrepareEntryAsync(string action, Guid entry = default, string name = "", int position = -1, uint fileTime = 0, CancellationToken token = default)
    {
        var before = Current;
        return Task.Run(() =>
        {
            var package = (PreparedScriptPackage)before.State; var entries = package.Entries.ToList(); int index = entries.FindIndex(e => e.Id == entry);
            if (action != "add" && index < 0) throw new InvalidDataException("The script entry no longer exists.");
            if (action is "add" or "duplicate" or "rename") PreparedScriptWriter.ValidateName(name);
            switch (action)
            {
                case "add": entries.Add(new(Guid.NewGuid(), null, name, fileTime, new byte[128], [], ReadOnlyMemory<byte>.Empty)); break;
                case "duplicate": entries.Insert(index + 1, entries[index].Duplicate(name)); break;
                case "rename": entries[index] = entries[index] with { Name = name }; break;
                case "timestamp": entries[index] = entries[index] with { FileTime = fileTime }; break;
                case "delete": entries.RemoveAt(index); break;
                case "move": Move(entries, index, position); break;
                default: throw new InvalidDataException("Unknown script entry action.");
            }
            return Build(before, package with { Entries = entries }, token);
        }, token);
    }
    public Task<PreparedContentEdit> PrepareInstructionAsync(Guid entry, string action, Guid instruction = default, IReadOnlyList<string>? tokens = null, int position = -1, CancellationToken token = default)
    {
        var before = Current; var frozenTokens = tokens?.ToArray();
        return Task.Run(() =>
        {
            var package = (PreparedScriptPackage)before.State; var entries = package.Entries.ToList(); int e = entries.FindIndex(e => e.Id == entry);
            if (e < 0) throw new InvalidDataException("The script entry no longer exists.");
            var list = entries[e].Instructions.ToList(); int index = list.FindIndex(i => i.Id == instruction);
            if (action != "add" && index < 0) throw new InvalidDataException("The instruction no longer exists.");
            if (action is "add" or "set") PreparedScriptWriter.ValidateTokens(frozenTokens ?? throw new InvalidDataException("Supply the command and argument tokens."));
            switch (action)
            {
                case "add":
                    if (position < -1 || position > list.Count) throw new InvalidDataException("Invalid insertion position.");
                    list.Insert(position == -1 ? list.Count : position, new(Guid.NewGuid(), frozenTokens!, ReadOnlyMemory<byte>.Empty, null)); break;
                case "set": if (!list[index].Tokens.SequenceEqual(frozenTokens!)) list[index] = list[index] with { Tokens = frozenTokens!, Raw = ReadOnlyMemory<byte>.Empty }; break;
                case "duplicate": list.Insert(index + 1, list[index].Duplicate()); break;
                case "delete": list.RemoveAt(index); break;
                case "move": Move(list, index, position); break;
                default: throw new InvalidDataException("Unknown instruction action.");
            }
            entries[e] = entries[e] with { Instructions = list }; return Build(before, package with { Entries = entries }, token);
        }, token);
    }
    private PreparedContentEdit Build(ContentSnapshot before, PreparedScriptPackage package, CancellationToken token)
    {
        byte[] bytes = PreparedScriptWriter.Write(package, token);
        var original = before.Documents[SourcePath]; var doc = FormatRegistry.Default.OpenBytes(SourcePath, bytes, original.Stamp, token);
        if (doc.Scripts == null || doc.Diagnostics.Any(d => d.Severity == "Error")) throw new InvalidDataException("Script edit failed shared-reader verification.");
        // Reparse verifies storage; accepted identities always come from the prepared working model.
        doc.Scripts = package;
        var previous = (PreparedScriptPackage)before.State;
        bool identitiesChanged = !previous.Entries.Select(e => e.Id).SequenceEqual(package.Entries.Select(e => e.Id)) ||
            previous.Entries.Zip(package.Entries).Any(pair => !pair.First.Instructions.Select(i => i.Id).SequenceEqual(pair.Second.Instructions.Select(i => i.Id)));
        return new(before, new(new Dictionary<string, ZbdDocument>(StringComparer.OrdinalIgnoreCase) { [SourcePath] = doc }, package), before.Documents, identitiesChanged);
    }
    private static void Move<T>(List<T> list, int index, int position)
    {
        if (position < 0 || position >= list.Count) throw new InvalidDataException("Destination index is outside the list.");
        var item = list[index]; list.RemoveAt(index); list.Insert(position, item);
    }
}
