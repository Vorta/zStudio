using System.Globalization;
using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

/// <summary>
/// Edits the header of an AI network's source (<c>data/mN/zrdr/aipath/net_NN.zrd</c>): its name, type, attack strategy and
/// the radii, dwells and flags the game reads (see docs/engine-evidence.md). Only the edited values change: a stored value's
/// token is replaced in place, and a missing key is written before the first node, keeping the file's comments and layout.
/// The network's ID is its file's number, which vehicles name, so the file itself is never renamed here.
/// </summary>
public static class SourceAiNetworkEdits
{
    public enum FieldKind { Text, Number, Pair, Flag }
    /// <summary>A header field: its stored key (and an alias the game also reads), its kind and its plain-word caption.</summary>
    public sealed record Field(string Key, FieldKind Kind, string Caption, string? Alias = null);

    public static IReadOnlyList<Field> Fields { get; } =
    [
        new("name", FieldKind.Text, "name"), new("type", FieldKind.Text, "type"), new("attack_strategy", FieldKind.Text, "attack strategy"),
        new("attack_rad", FieldKind.Number, "attack radius"), new("attack_dwell", FieldKind.Number, "attack dwell"), new("attack_buddy", FieldKind.Flag, "attack buddy"),
        new("path_width", FieldKind.Number, "path width"), new("activate_rad", FieldKind.Number, "activation radius"),
        new("pursuit_params", FieldKind.Pair, "pursuit", "pursuit_range"), new("not_pursuit_dwell", FieldKind.Number, "not-pursuit dwell"),
        new("return_range", FieldKind.Number, "return range"), new("hide_times", FieldKind.Pair, "hide times"),
    ];
    public static Field? FieldOf(string key) => Fields.FirstOrDefault(f => f.Key == key);

    /// <summary>The types the game tells apart (it reads the first two letters) and the strategies it knows (the first three).</summary>
    public static IReadOnlyList<string> Types { get; } = ["standard", "hide", "fixed", "defilade"];
    public static IReadOnlyList<string> Strategies { get; } = ["head_on", "circle", "back_forth", "follow_net", "zigzag", "sit"];
    /// <summary>The engine copies the name unchecked into 20 bytes (a terminator included).</summary>
    public const int MaximumNameLength = 19;

    /// <summary>A stored field: the key it is stored under, its values as written, and whether the key is stored more than once.</summary>
    public sealed record StoredField(string Key, IReadOnlyList<ZrdNode> Values, bool Repeated)
    {
        public string Text(int index = 0) => index < Values.Count ? Values[index].Kind == ZrdKind.String ? Values[index].Text : Values[index].Value : "";
    }

    /// <summary>The header as stored: each known field (by its key; null when not stored), and the other keys before the nodes.</summary>
    public sealed record Header(IReadOnlyDictionary<string, StoredField?> Fields, IReadOnlyList<(string Key, string Value)> Other);

    /// <summary>Reads a network source's header fields, as stored.</summary>
    public static Header Read(ZrdNode root)
    {
        var body = Body(root);
        Dictionary<string, List<ZrdNode>> keys = new(StringComparer.Ordinal);
        List<(string, string)> other = [];
        foreach (var (key, values) in Keys(body))
        {
            if (!keys.TryGetValue(key, out var list)) keys[key] = list = [];
            list.Add(values);
            if (key.StartsWith("node_", StringComparison.Ordinal) || Fields.Any(f => f.Key == key || f.Alias == key)) continue;
            if (other.Count < 16) other.Add((JsonData.ShownText(key, 64), JsonData.ShownText(string.Join(" ", values.Children.Take(4).Select(c => c.Kind == ZrdKind.String ? c.Text : c.Value)), 64)));
        }
        Dictionary<string, StoredField?> fields = [];
        foreach (var field in Fields)
        {
            string? key = keys.ContainsKey(field.Key) ? field.Key : field.Alias != null && keys.ContainsKey(field.Alias) ? field.Alias : null;
            fields[field.Key] = key == null ? null : new(key, keys[key][0].Children, keys[key].Count > 1 || field.Alias != null && keys.ContainsKey(field.Key) && keys.ContainsKey(field.Alias));
        }
        return new(fields, other);
    }

    /// <summary>A requested value: the field's key and its values as typed (one, or two for a pair; "0" or "1" for a flag).</summary>
    public sealed record Change(string Key, IReadOnlyList<string> Values);
    public sealed record Edit(string Label, byte[] Content, IReadOnlyList<string> Changed);

    /// <summary>
    /// Plans an edit of the network <paramref name="path"/> (its current source <paramref name="bytes"/>): each change is checked,
    /// a stored value is replaced in place and a missing key is added before the nodes. Refuses binary sources, repeated keys
    /// and invalid values; an edit that changes nothing throws <see cref="InvalidOperationException"/>.
    /// </summary>
    public static Edit Plan(string path, ReadOnlySpan<byte> bytes, IReadOnlyList<Change> changes, CancellationToken token = default)
    {
        if (!ZrdText.LooksLikeText(bytes)) throw new InvalidDataException($"{path} is stored as binary zReader data; its header can be edited only in a text source.");
        var syntax = ZrdTextSyntax.Parse(bytes, token);
        var header = Read(syntax.Root);
        Dictionary<Guid, ZrdNode> replacements = [];
        List<(string Key, ZrdNode[] Values)> added = [];
        List<string> changed = [];
        HashSet<string> named = [];
        foreach (var change in changes)
        {
            token.ThrowIfCancellationRequested();
            var field = FieldOf(change.Key) ?? throw new InvalidDataException($"{JsonData.ShownText(change.Key, 64)} is not a network header field.");
            if (!named.Add(field.Key)) throw new InvalidDataException($"{field.Key} is changed twice in one edit; give each field once.");
            var values = Values(field, change.Values);
            var stored = header.Fields[field.Key];
            if (stored is { Repeated: true }) throw new InvalidDataException($"{field.Key} is stored more than once in {path}; correct the file before editing it here.");
            if (stored != null && (stored.Values.Count != values.Length || stored.Values.Any(v => v.Kind is not (ZrdKind.String or ZrdKind.Int or ZrdKind.Float))))
                throw new InvalidDataException($"{stored.Key} in {path} does not hold {values.Length} plain value{(values.Length == 1 ? "" : "s")}; correct the file before editing it here.");
            if (stored != null && Same(stored.Values, values)) continue;
            changed.Add(field.Caption);
            if (stored == null) { added.Add((field.Key, values)); continue; }
            for (int i = 0; i < values.Length; i++) replacements[stored.Values[i].Id] = values[i];
        }
        if (changed.Count == 0) throw new InvalidOperationException("Nothing changed.");
        string text = syntax.ReplaceScalars(replacements, token);
        if (added.Count > 0)
        {
            // A missing key goes before the first node, written as the file writes its keys.
            var edited = ZrdTextSyntax.Parse(text, token);
            var body = Body(edited.Root);
            List<ZrdNode> children = [.. body.Children];
            int at = children.FindIndex(c => c.Kind == ZrdKind.String && c.Text.StartsWith("node_", StringComparison.Ordinal));
            if (at < 0) at = children.Count;
            foreach (var (key, values) in added)
            {
                children.Insert(at++, ZrdNode.Create(ZrdKind.String, System.Text.Json.JsonSerializer.Serialize(key)));
                children.Insert(at++, ZrdNode.Create(ZrdKind.Array) with { Children = values });
            }
            var root = ReplaceBody(edited.Root, body with { Children = children });
            var (rewritten, lossless) = edited.Rewrite(root, token);
            if (!lossless) throw new InvalidDataException($"Adding {string.Join(", ", added.Select(a => a.Key))} to {path} would rewrite the whole file; add the key in a text editor.");
            text = rewritten;
        }
        string network = Path.GetFileNameWithoutExtension(path);
        string label = $"Edit {network}: " + (changed.Count <= 3 ? string.Join(", ", changed) : string.Join(", ", changed.Take(3)) + $" and {changed.Count - 3} more");
        return new(label, ZrdTextSyntax.Encode(text), changed);
    }

    /// <summary>Checks a requested value as <see cref="Plan"/> would (the message says what is wrong).</summary>
    public static void Check(Field field, IReadOnlyList<string> values) => _ = Values(field, values);

    /// <summary>The checked scalars of a requested value.</summary>
    private static ZrdNode[] Values(Field field, IReadOnlyList<string> values)
    {
        int count = field.Kind == FieldKind.Pair ? 2 : 1;
        if (values.Count != count) throw new InvalidDataException($"{field.Caption} takes {count} value{(count == 1 ? "" : "s")}.");
        return field.Kind switch
        {
            FieldKind.Text => [Text(field, values[0].Trim())],
            FieldKind.Flag => values[0].Trim() is "0" or "1" ? [Integer(values[0].Trim())] : throw new InvalidDataException($"{field.Caption} is 0 or 1."),
            _ => [.. values.Select(v => Number(field, v))],
        };
    }
    private static ZrdNode Text(Field field, string value)
    {
        switch (field.Key)
        {
            case "name":
                if (value.Length == 0) throw new InvalidDataException("Enter a name.");
                if (value.Length > MaximumNameLength) throw new InvalidDataException($"A network name has at most {MaximumNameLength} characters (the game copies it into 20 bytes).");
                if (!value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.')) throw new InvalidDataException("Use letters, digits, _ - . only in a network name.");
                // The game finds a key as the first matching word anywhere in the file: a name must not read as one.
                if (value.Equals("version", StringComparison.OrdinalIgnoreCase) || value.StartsWith("node_", StringComparison.OrdinalIgnoreCase)
                    || Fields.Any(f => value.Equals(f.Key, StringComparison.OrdinalIgnoreCase) || value.Equals(f.Alias, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException($"{value} is a key of the network file; choose another name.");
                break;
            case "type":
                if (!Types.Contains(value)) throw new InvalidDataException($"The type is one of {string.Join(", ", Types)}.");
                break;
            case "attack_strategy":
                if (!Strategies.Contains(value)) throw new InvalidDataException($"The attack strategy is one of {string.Join(", ", Strategies)}.");
                break;
        }
        return ZrdNode.Create(ZrdKind.String, System.Text.Json.JsonSerializer.Serialize(value));
    }
    private static ZrdNode Number(Field field, string text)
    {
        if (!float.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float value) || !float.IsFinite(value))
            throw new InvalidDataException($"Enter a number for {field.Caption} (use a decimal point).");
        if (value < 0) throw new InvalidDataException($"Enter 0 or more for {field.Caption}.");
        if (field.Key == "path_width" && value <= 0) throw new InvalidDataException("Enter a path width above 0.");
        return ZrdNode.Create(ZrdKind.Float, value.ToString("R", CultureInfo.InvariantCulture));
    }
    private static ZrdNode Integer(string text) => ZrdNode.Create(ZrdKind.Int, text);
    /// <summary>
    /// Equal values of the same kind: the game reads a value's bits as the field's type, so an integer stored in a float field
    /// (or the reverse) differs from the same number written as the field takes it.
    /// </summary>
    private static bool Same(IReadOnlyList<ZrdNode> stored, ZrdNode[] values)
    {
        for (int i = 0; i < values.Length; i++)
        {
            var (a, b) = (stored[i], values[i]);
            if (a.Kind != b.Kind) return false;
            if (a.Kind == ZrdKind.String) { if (a.Text != b.Text) return false; continue; }
            if (Scalar(a) != Scalar(b)) return false;
        }
        return true;
        static double Scalar(ZrdNode node) => node.Kind == ZrdKind.Int ? unchecked((int)node.Bits) : node.Kind == ZrdKind.Float ? BitConverter.UInt32BitsToSingle(node.Bits) : double.NaN;
    }

    /// <summary>The keyword list: a source file stores one top-level array holding it.</summary>
    private static ZrdNode Body(ZrdNode root)
    {
        while (root.Children.Count == 1 && root.Children[0].Kind == ZrdKind.Array) root = root.Children[0];
        return root;
    }
    private static ZrdNode ReplaceBody(ZrdNode root, ZrdNode body)
    {
        if (root.Id == body.Id) return body;
        return root with { Children = [ReplaceBody(root.Children[0], body)] };
    }
    private static IEnumerable<(string Key, ZrdNode Values)> Keys(ZrdNode body)
    {
        var c = body.Children;
        for (int i = 0; i + 1 < c.Count; i++)
            if (c[i].Kind == ZrdKind.String && c[i + 1].Kind == ZrdKind.Array) { yield return (c[i].Text, c[i + 1]); i++; }
    }
}
