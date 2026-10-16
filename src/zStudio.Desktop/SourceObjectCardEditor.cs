using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using System.Windows.Controls;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Core.Sources;
using zStudio.Core.Worlds;

namespace zStudio.Desktop;

/// <summary>One explicitly accepted object draft. Field focus never writes sources.</summary>
internal sealed class SourceObjectCardEditor : FieldEditor
{
    private readonly SourceObjectState state;
    private readonly Dictionary<string, DraftInput> inputs = [];
    private readonly Dictionary<string, string> acceptedFields = [];
    internal event Action? Changed;
    internal SourceObjectState State => state;
    internal SourceObjectCardEditor(SourceObjectState state)
    {
        this.state = state; ManualDraftMode = true; CompactRows = true;
        StackPanel form = new(); Content = form;
        bool whole = state.Object == null;
        // The card's groups. Its header names the object and its type; Name and Model are edited only in edit mode, and
        // only where they can be.
        CardSection objectPart = new("Object"), transformPart = new("Transform"), zonePart = new("Zone"), flagPart = new("Flags"), propertyPart = new("Properties");
        TransformSection = state.Transform != null ? transformPart : null;
        StackPanel target = objectPart.Body;
        EditOnly("name", "Name", state.Name, whole && state.Origin.Database && state.NodeClass is WorldNodeClass.Object3D or WorldNodeClass.Lod,
            "Object name. Scripts, animations and resources can refer to it by name.");
        if (state.Model != null) EditOnly("model", "Model", state.Model, whole, "Project-relative glTF model path.");
        if (inputs.TryGetValue("model", out var modelInput)) AddBrowse(modelInput);
        target = transformPart.Body;
        if (state.Transform is { } transform)
        {
            Vector("position", "Position", transform.Position, "Stored local position relative to the parent, in game units.");
            Vector("rotationDegrees", "Rotation (degrees)", transform.RotationDegrees, "Local Euler rotation in degrees.");
            Vector("scale", "Scale", transform.Scale, "Local scale. Unchanged components retain their authored values.");
        }
        // Zone: Inherit (the parent's), Any or a number, and the altitude zone check; both are kept as hidden drafts like the flags.
        if (state.ZoneEditable)
        {
            target = new StackPanel { Visibility = System.Windows.Visibility.Collapsed }; zonePart.Body.Children.Add(target);
            Add("zone", "Zone", state.ZoneText, true, "Inherit (the parent's zone), Any, or a zone number from 0 to 254.");
            Add("altitudeZoneCheck", "Altitude zone check", (state.Flags & WorldGltf.ZoneGate) != 0 ? "on" : "off", true,
                "On or off. When on, the object is only standable from inside its zone.");
            zonePart.Body.Children.Add(ZoneRow());
            zonePart.Body.Children.Add(FlagBox("altitudeZoneCheck", "Altitude zone check (only standable inside its zone)", true));
            zoneSection = zonePart;
        }
        else
        {
            acceptedFields["Zone"] = ZoneReadout.Zone(state.Zone);
            zonePart.Summary = ZoneReadout.Zone(state.Zone);
            zonePart.Body.Children.Add(InfoRow("Zone", ZoneReadout.Zone(state.Zone) + (state.Origin.Terrain != null ? " · set in Terrain properties" : "")));
        }
        // Flags are check boxes; each keeps its "on"/"off" draft field (hidden) for drafts, MCP fields and Apply.
        target = new StackPanel { Visibility = System.Windows.Visibility.Collapsed }; flagPart.Body.Children.Add(target);
        foreach (var (group, bits) in FlagGroups)
        {
            if (state.NodeClass is not (WorldNodeClass.Object3D or WorldNodeClass.Lod)) break;
            flagPart.Body.Children.Add(new TextBlock { Text = group, FontSize = 12, FontWeight = System.Windows.FontWeights.SemiBold, Opacity = .7, Margin = new(0, 6, 0, 2) });
            foreach (uint bit in bits)
            {
                string label = SourceObjectFields.EditableFlags.First(f => f.Bit == bit).Label, key = "flag_" + bit.ToString("X");
                bool settable = SourceObjectEdits.FlagSettable(state.NodeClass, state.Origin, bit);
                Add(key, label, (state.Flags & bit) != 0 ? "on" : "off", settable,
                    (bit & 0x30000) != 0 ? "On or off. Enabling this mode clears the other crater mode." : "On or off.");
                flagPart.Body.Children.Add(FlagBox(key, label, settable));
            }
        }
        target = propertyPart.Body;
        foreach (var (command, hint) in SourceObjectEdits.PropertyCommands)
        {
            if (!SourceObjectFields.CommandFits(state.Class, command)) continue;
            var writer = state.Origin.Writers.GetValueOrDefault(command);
            if (writer != null && (writer.Tokens.Count > 17 || writer.Tokens.Skip(1).Sum(t => (long)t.Length + 1) > 4096)) continue;
            Add(command, command, writer == null ? "" : string.Join(" ", writer.Tokens.Skip(1)), true, hint);
        }
        foreach (var part in new[] { objectPart, transformPart, zonePart, flagPart, propertyPart })
            if (part.Body.Children.Cast<System.Windows.UIElement>().Any(c => c.Visibility == System.Windows.Visibility.Visible)) form.Children.Add(part);
        if (objectPart.Body.Children.Count > 0) editOnly.Add(objectPart);
        foreach (string note in state.Notes.Where(n => n.StartsWith("A node of ", StringComparison.Ordinal)).Take(2)) ReadOnlyText(form, JsonData.ShownText(note, 512));
        foreach (var (key, input) in inputs) lastTexts[key] = input.Draft.Text;
        ManualDraftChanged += Notify;
        SetEditing(false);

        void Add(string key, string label, string value, bool editable, string hint, string[]? components = null)
        {
            acceptedFields[label] = value;
            Input(target, label, value, _ => throw new InvalidOperationException("Apply the complete object draft."), !editable, hint, components: components);
            if (editable) inputs.Add(key, draftInputs[^1]);
        }
        void EditOnly(string key, string label, string value, bool editable, string hint)
        {
            if (!editable) { acceptedFields[label] = value; return; }
            Add(key, label, value, true, hint);
        }
        System.Windows.FrameworkElement FlagBox(string key, string label, bool settable)
        {
            // "Standable (altitude surface)" reads as Standable over a smaller hint.
            int open = label.IndexOf(" (", StringComparison.Ordinal);
            string main = open < 0 ? label : label[..open], hint = open < 0 ? "" : label[(open + 2)..].TrimEnd(')');
            if (hint.Length > 0) hint = char.ToUpperInvariant(hint[0]) + hint[1..];
            StackPanel text = new();
            text.Children.Add(new TextBlock { Text = main, FontSize = 14 });
            if (hint.Length > 0)
            {
                TextBlock note = new() { Text = hint, FontSize = 12 };
                note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                text.Children.Add(note);
            }
            CheckBox box = new() { Content = text, Margin = new(0, 2, 0, 2), Tag = settable };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            box.Click += (_, _) =>
            {
                if (!inputs.TryGetValue(key, out var input)) return;
                input.Draft.Text = box.IsChecked == true ? "on" : "off"; input.Display(); Notify();
            };
            flagBoxes[key] = box;
            return box;
        }
        static System.Windows.FrameworkElement InfoRow(string label, string value)
        {
            Grid row = new() { Margin = new(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new() { Width = new(76) }); row.ColumnDefinitions.Add(new());
            TextBlock caption = new() { Text = label, FontSize = 13, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new(0, 0, 6, 0) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            TextBox shown = new() { Text = value, IsReadOnly = true, BorderThickness = new(0), Background = System.Windows.Media.Brushes.Transparent, Padding = new(0), FontSize = 13, TextWrapping = System.Windows.TextWrapping.Wrap };
            Grid.SetColumn(shown, 1); row.Children.Add(caption); row.Children.Add(shown);
            return row;
        }
        System.Windows.FrameworkElement ZoneRow()
        {
            // An editable combo box: pick Inherit or Any, or type a zone number. A hint (or the input's problem) sits below it.
            Grid row = new() { Margin = new(0, 2, 0, 2) };
            row.ColumnDefinitions.Add(new() { Width = new(76) }); row.ColumnDefinitions.Add(new());
            TextBlock caption = new() { Text = "Zone", FontSize = 13, VerticalAlignment = System.Windows.VerticalAlignment.Top, Margin = new(0, 7, 6, 0) };
            caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            if (state.ZoneCanInherit) zoneBox.Items.Add(ZoneOption("Inherit", "From parent"));
            zoneBox.Items.Add(ZoneOption("Any", "No zone limit"));
            // The list's foot: a number is typed rather than picked.
            TextBlock foot = new() { Text = "Or type a zone number", FontSize = 11.5 };
            foot.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
            zoneBox.Items.Add(new ComboBoxItem { Content = foot, IsEnabled = false, Focusable = false });
            zoneBox.Text = state.ZoneText;
            System.Windows.Automation.AutomationProperties.SetName(zoneBox, "Zone");
            zoneBox.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ZoneTyped()));
            zoneBox.SelectionChanged += (_, _) => Dispatcher.BeginInvoke(ZoneTyped);
            zoneHelp.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            StackPanel field = new(); field.Children.Add(zoneBox); field.Children.Add(zoneHelp);
            Grid.SetColumn(field, 1); row.Children.Add(caption); row.Children.Add(field);
            return row;
        }
        static ComboBoxItem ZoneOption(string label, string description)
        {
            StackPanel content = new() { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock { Text = label, FontSize = 14 });
            TextBlock note = new() { Text = description, FontSize = 12, Margin = new(8, 0, 0, 0), VerticalAlignment = System.Windows.VerticalAlignment.Center };
            note.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            content.Children.Add(note);
            ComboBoxItem item = new() { Content = content };
            TextSearch.SetText(item, label);
            return item;
        }
        void Vector(string key, string label, Vector3 value, string hint) => Add(key, label,
            string.Join(", ", new[] { value.X, value.Y, value.Z }.Select(v => v.ToString("R", CultureInfo.InvariantCulture))), true, hint, ["X", "Y", "Z"]);
    }
    private readonly ComboBox zoneBox = new() { IsEditable = true, HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch, MinWidth = 0 };
    private readonly TextBlock zoneHelp = new() { FontSize = 11.5, TextWrapping = System.Windows.TextWrapping.Wrap, Margin = new(0, 3, 0, 2) };
    private CardSection? zoneSection;
    /// <summary>Opens a model file for the Model field; returns its project path, or null when nothing was chosen.</summary>
    internal Func<string?>? BrowseModel { get; set; }
    private bool showingZone;
    /// <summary>The Zone combo box's text becomes the zone draft.</summary>
    private void ZoneTyped()
    {
        if (showingZone || !inputs.TryGetValue("zone", out var input)) return;
        string text = zoneBox.Text ?? "";
        if (input.Draft.Text == text) { RefreshZone(); return; }
        input.Draft.Text = text; input.Display(); Notify();
    }
    /// <summary>Shows the zone draft: the combo box's text and its hint or problem; the section header repeats the value.</summary>
    private void RefreshZone()
    {
        if (!inputs.TryGetValue("zone", out var input)) return;
        string text = input.Draft.Text;
        if (zoneBox.Text != text) { showingZone = true; try { zoneBox.Text = text; } finally { showingZone = false; } }
        var parsed = ParseZone(text);
        bool valid = parsed.Valid;
        zoneHelp.Text = valid ? state.ZoneCanInherit ? "Zone 0–254, Any or Inherit" : "Zone 0–254 or Any" :
            state.ZoneCanInherit ? "Enter 0–254, Any or Inherit" : "Enter 0–254 or Any";
        if (zoneSection != null) zoneSection.Summary = text.Trim();
        if (valid) zoneHelp.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        else zoneHelp.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
    }
    /// <summary>Inherit, Any (255) or a zone number from 0 to 254, in any letter case.</summary>
    private (bool Valid, bool Inherit, byte? Zone) ParseZone(string text)
    {
        text = text.Trim();
        if (text.Equals("inherit", StringComparison.OrdinalIgnoreCase)) return (state.ZoneCanInherit, state.ZoneCanInherit, null);
        if (text.Equals("any", StringComparison.OrdinalIgnoreCase)) return (true, false, 255);
        return text.Length is > 0 and <= 3 && text.All(char.IsAsciiDigit) && int.Parse(text, CultureInfo.InvariantCulture) is var id and <= 254
            ? (true, false, (byte)id) : (false, false, null);
    }
    /// <summary>The zone and altitude zone check changes of the draft, or null when neither changed.</summary>
    internal SourceZoneAssignment? ZoneAssignment()
    {
        string? zone = Text("zone"), gate = Text("altitudeZoneCheck");
        if (zone != null && zone.Equals(state.ZoneText, StringComparison.OrdinalIgnoreCase)) zone = null;
        bool? on = gate == null ? null : gate.ToLowerInvariant() is "on" or "true" or "1" ? true : gate.ToLowerInvariant() is "off" or "false" or "0" ? false
            : throw new StudioCommandException("invalid_draft", "Altitude zone check needs on or off.");
        if (on == ((state.Flags & WorldGltf.ZoneGate) != 0)) on = null;
        if (zone == null && on == null) return null;
        var parsed = zone == null ? default : ParseZone(zone);
        if (zone != null && !parsed.Valid) throw new StudioCommandException("invalid_draft", state.ZoneCanInherit ? "Enter a zone number from 0 to 254, Any or Inherit." : "Enter a zone number from 0 to 254 or Any.");
        return new(NodeZone: parsed.Zone, Gate: on, Inherit: parsed.Inherit);
    }
    private readonly Dictionary<string, string> lastTexts = [];
    private readonly List<System.Windows.FrameworkElement> editOnly = [];
    private readonly Dictionary<string, CheckBox> flagBoxes = [];
    private bool editing;
    /// <summary>The Transform group, whose header the card gives the gizmo's Move/Rotate mode; null for objects without a transform.</summary>
    internal CardSection? TransformSection { get; }
    private static readonly (string Group, uint[] Bits)[] FlagGroups = [("Surfaces & collision", [0x08, 0x10, 0x20, 0x40]), ("Rendering & terrain", [0x80, 0x10000, 0x20000, 0x800000])];
    /// <summary>Shows each flag's draft in its check box, which can change only in edit mode and for flags the source can set.</summary>
    private void RefreshFlags()
    {
        int on = 0;
        foreach (var (key, box) in flagBoxes)
        {
            string text = inputs.TryGetValue(key, out var input) ? input.Draft.Text : acceptedFields.GetValueOrDefault(SourceObjectFields.EditableFlags.First(f => "flag_" + f.Bit.ToString("X") == key).Label, "off");
            box.IsChecked = IsOn(text); if (IsOn(text)) on++;
            box.IsEnabled = editing && box.Tag is true;
        }
        int zoneGates = flagBoxes.ContainsKey("altitudeZoneCheck") && flagBoxes["altitudeZoneCheck"].IsChecked == true ? 1 : 0;
        if (Content is StackPanel form && form.Children.OfType<CardSection>().FirstOrDefault(c => c.Title == "Flags") is { } flags)
            flags.Summary = $"{on - zoneGates} of {flagBoxes.Count - (flagBoxes.ContainsKey("altitudeZoneCheck") ? 1 : 0)} on";
        zoneBox.IsEnabled = editing; RefreshZone();
    }
    /// <summary>Whether the object has any field to edit; the card offers Edit only then.</summary>
    internal bool CanEdit => inputs.Count > 0;
    /// <summary>Fields are read-only until the card's Edit begins a draft, and again after it is applied or discarded.</summary>
    internal void SetEditing(bool editing)
    {
        foreach (var input in inputs.Values) foreach (var box in Boxes(input.Control)) box.IsReadOnly = !editing;
        foreach (var row in editOnly) row.Visibility = editing ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        this.editing = editing; RefreshFlags();
        static IEnumerable<ValueTextBox> Boxes(object element)
        {
            if (element is ValueTextBox box) yield return box;
            else if (element is Panel panel) foreach (var child in panel.Children) foreach (var nested in Boxes(child)) yield return nested;
        }
    }
    /// <summary>The draft's local transform, or null when the object has none or a transform field holds invalid input.</summary>
    internal ObjectTransform? DraftTransform()
    {
        if (state.Transform is not { } accepted) return null;
        try
        {
            return accepted with { Position = Vector("position") ?? accepted.Position, RotationDegrees = Vector("rotationDegrees") ?? accepted.RotationDegrees, Scale = Vector("scale") ?? accepted.Scale };
        }
        catch (Exception ex) when (ex is StudioCommandException or System.IO.InvalidDataException) { return null; }
    }
    /// <summary>The Transform header's reset: rotation 0, 0, 0 and scale 1, 1, 1 in the draft (position stays).</summary>
    internal void ResetRotationAndScale()
    {
        if (inputs.TryGetValue("rotationDegrees", out var turn) && turn.Draft.Text != "0, 0, 0") { turn.Draft.Text = "0, 0, 0"; turn.Display(); }
        if (inputs.TryGetValue("scale", out var size) && size.Draft.Text != "1, 1, 1") { size.Draft.Text = "1, 1, 1"; size.Display(); }
        Notify();
    }
    /// <summary>A folder button inside the Model box: choosing a file writes its project path into the draft.</summary>
    private void AddBrowse(DraftInput input)
    {
        var box = FindBox(input.Control);
        if (box?.Parent is not Panel holder) return;
        int at = holder.Children.IndexOf(box); holder.Children.Remove(box);
        Grid inline = new(); inline.Children.Add(box);
        box.Padding = new(box.Padding.Left, box.Padding.Top, 34, box.Padding.Bottom);
        Button browse = new() { Content = new PreviewIcon { Kind = "Folder", Width = 15, Height = 15 }, Width = 28, Height = 26, Margin = new(0, 0, 2, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right, VerticalAlignment = System.Windows.VerticalAlignment.Center, ToolTip = "Browse for a model" };
        browse.SetResourceReference(StyleProperty, "CommandButton");
        browse.Width = 28; browse.Height = 26;
        System.Windows.Automation.AutomationProperties.SetName(browse, "Browse for model");
        browse.Click += (_, _) =>
        {
            if (box.IsReadOnly || BrowseModel?.Invoke() is not { } chosen) return;
            input.Draft.Text = chosen; input.Display(); Notify();
        };
        inline.Children.Add(browse);
        holder.Children.Insert(at, inline);
        static ValueTextBox? FindBox(System.Windows.DependencyObject element)
        {
            if (element is ValueTextBox found) return found;
            for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(element); i++)
                if (FindBox(System.Windows.Media.VisualTreeHelper.GetChild(element, i)) is { } nested) return nested;
            if (element is Panel panel) foreach (System.Windows.UIElement child in panel.Children) if (FindBox(child) is { } logical) return logical;
            return null;
        }
    }
    /// <summary>Writes a handle drag into the Position or Rotation field.</summary>
    internal void SetTransformText(Vector3? position, Vector3? rotationDegrees)
    {
        static string Text(Vector3 v) => string.Join(", ", new[] { v.X, v.Y, v.Z }.Select(c => c.ToString("R", CultureInfo.InvariantCulture)));
        SetTransformTexts(position is { } at ? Text(at) : null, rotationDegrees is { } turn ? Text(turn) : null);
    }
    /// <summary>The Position and Rotation inputs as typed, for restoring them exactly after a canceled handle drag.</summary>
    internal (string? Position, string? Rotation) TransformTexts() => (inputs.GetValueOrDefault("position")?.Draft.Text, inputs.GetValueOrDefault("rotationDegrees")?.Draft.Text);
    internal void SetTransformTexts(string? position, string? rotation)
    {
        if (position != null && inputs.TryGetValue("position", out var at) && at.Draft.Text != position) { at.Draft.Text = position; at.Display(); }
        if (rotation != null && inputs.TryGetValue("rotationDegrees", out var turn) && turn.Draft.Text != rotation) { turn.Draft.Text = rotation; turn.Display(); }
        Notify();
    }
    private bool notifying;
    private void Notify()
    {
        if (notifying) return;
        notifying = true;
        try
        {
            string? enabled = inputs.Where(p => p.Key is "flag_10000" or "flag_20000" && lastTexts.GetValueOrDefault(p.Key) != p.Value.Draft.Text && IsOn(p.Value.Draft.Text)).Select(p => p.Key).LastOrDefault();
            if (enabled != null && inputs.TryGetValue(enabled == "flag_10000" ? "flag_20000" : "flag_10000", out var other) && IsOn(other.Draft.Text))
            { other.Draft.Text = "off"; other.Display(); }
            foreach (var (key, input) in inputs) lastTexts[key] = input.Draft.Text;
            RefreshFlags();
            Changed?.Invoke();
        }
        finally { notifying = false; }
    }
    private static bool IsOn(string text) => text.Trim().ToLowerInvariant() is "on" or "true" or "1";
    internal override string DraftToken
    {
        get
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var (key, input) in inputs)
            {
                Append(key); Append(input.Draft.Committed); Append(input.Draft.Text);
            }
            return Convert.ToHexString(hash.GetHashAndReset());
            void Append(string text)
            {
                Span<byte> length = stackalloc byte[4];
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, text.Length);
                hash.AppendData(length); hash.AppendData(MemoryMarshal.AsBytes(text.AsSpan()));
            }
        }
    }
    internal JsonObject Describe() => new()
    {
        ["node"] = state.Node,
        ["fields"] = new JsonArray(inputs.Select(p => (JsonNode)new JsonObject
        { ["field"] = p.Key, ["value"] = JsonData.ShownText(p.Value.Draft.Text, 4096), ["original"] = JsonData.ShownText(p.Value.Draft.Committed, 4096), ["pending"] = p.Value.Draft.IsPending, ["valueLength"] = p.Value.Draft.Text.Length, ["originalLength"] = p.Value.Draft.Committed.Length, ["truncated"] = p.Value.Draft.Text.Length > 4096 || p.Value.Draft.Committed.Length > 4096 }).ToArray())
    };
    internal string Copy(string? field)
    {
        if (field == null) return string.Join(Environment.NewLine, acceptedFields.Select(p => p.Key + ": " + JsonData.ShownText(p.Value, 4096)));
        return acceptedFields.TryGetValue(field, out var value) ? field + ": " + JsonData.ShownText(value, 4096)
            : throw new StudioCommandException("invalid_argument", "Unknown object field label.");
    }
    internal void SetFields(JsonArray fields)
    {
        if (!IsEnabled) throw new StudioCommandException("busy", "Wait for the world to finish its current edit.");
        if (fields.Count is < 1 or > 32) throw new StudioCommandException("invalid_argument", "Give 1–32 fields.");
        List<(DraftInput Input, string Value)> changes = [];
        HashSet<string> seen = [];
        foreach (var field in fields)
        {
            string key = field?["field"]?.GetValue<string>() ?? "";
            string value = field?["value"]?.GetValue<string>() ?? throw new StudioCommandException("invalid_argument", "Each field requires a text value.");
            if (!seen.Add(key) || !inputs.TryGetValue(key, out var input) || value.Length > 4096) throw new StudioCommandException("invalid_argument", "Unknown, repeated, read-only or oversized field.");
            changes.Add((input, value));
        }
        if (changes.Count(p => (p.Input == inputs.GetValueOrDefault("flag_10000") || p.Input == inputs.GetValueOrDefault("flag_20000")) && IsOn(p.Value)) > 1)
            throw new StudioCommandException("invalid_argument", "Choose one crater mode.");
        foreach (var (input, value) in changes) { input.Draft.Text = value; input.Display(); }
        Notify();
    }
    internal void Discard()
    {
        foreach (var input in inputs.Values) { input.Draft.Discard(); input.Display(); }
        foreach (var (key, input) in inputs) lastTexts[key] = input.Draft.Text;
        RefreshFlags();
        Changed?.Invoke();
    }
    internal SourceObjectPatch Patch(Func<string, WorldNode?> parent)
    {
        if (inputs.Values.Any(i => i.Draft.Text.Length > 4096)) throw new StudioCommandException("invalid_draft", "Object field input is limited to 4096 characters.");
        string? Required(string key)
        {
            string? value = Text(key);
            if (value != null && (value.Length == 0 || value.Length > 4096)) throw new StudioCommandException("invalid_draft", $"Enter a valid {key}.");
            return value;
        }
        Dictionary<uint, bool> flags = [];
        foreach (var (key, input) in inputs.Where(p => p.Key.StartsWith("flag_", StringComparison.Ordinal) && p.Value.Draft.IsPending))
        {
            string text = input.Draft.Text.Trim().ToLowerInvariant();
            if (text is not ("on" or "off" or "true" or "false" or "1" or "0")) throw new StudioCommandException("invalid_draft", "Flags need on or off.");
            flags.Add(uint.Parse(key[5..], NumberStyles.HexNumber, CultureInfo.InvariantCulture), IsOn(text));
        }
        Dictionary<string, IReadOnlyList<string>> commands = [];
        foreach (string command in SourceObjectEdits.PropertyCommands.Keys)
            if (Text(command) is { } text) commands.Add(command, ComponentText.BoundedTokens(text, " ,\t", 1, 8, 64, "Give 1–8 arguments of up to 64 characters."));
        var into = Required("parent");
        return new() { Name = Required("name"), Model = Required("model")?.Replace('\\', '/'), Reparent = into != null, Parent = into == null ? null : parent(into),
            Position = Vector("position"), RotationDegrees = Vector("rotationDegrees"), Scale = Vector("scale"), Flags = flags, Commands = commands };
    }
    private string? Text(string key) => inputs.TryGetValue(key, out var input) && input.Draft.IsPending ? input.Draft.Text.Trim() : null;
    private Vector3? Vector(string key)
    {
        if (Text(key) is not { } text) return null;
        ComponentText.CheckNumeric(text);
        var parts = text.Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 3) throw new StudioCommandException("invalid_draft", $"{key} needs X, Y and Z.");
        float[] values = parts.Select(p => float.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) && float.IsFinite(f)
            ? f : throw new StudioCommandException("invalid_draft", $"{key} needs finite numbers.")).ToArray();
        return new(values[0], values[1], values[2]);
    }
}
