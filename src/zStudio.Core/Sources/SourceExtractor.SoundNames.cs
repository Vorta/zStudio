using zStudio.Core.Formats;

namespace zStudio.Core.Sources;

public static partial class SourceExtractor
{
    private sealed partial class Context
    {
        /// <summary>Native initialization names archive WAVs after their first sound ID. Loose filenames are preserved. All aliases keep sharing
        /// that one file; only filename operands change, never IDs or playback settings.</summary>
        private async Task<Dictionary<string, string>> RenameSoundDefinitionsAsync(IEnumerable<string> files, IReadOnlySet<string> looseNames)
        {
            HashSet<string> available = new(files, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);
            HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, string> missingIds = new(StringComparer.OrdinalIgnoreCase);
            var definitions = soundDefinitions.OrderBy(p => !p.Key.Equals(SourceBuilder.SoundDefinitions, StringComparison.OrdinalIgnoreCase))
                .ThenBy(p => p.Key, StringComparer.Ordinal).ToArray();
            foreach (var (path, definition) in definitions)
                foreach (var row in Rows(Resources[definition.Resource].Tree))
                {
                    token.ThrowIfCancellationRequested();
                    string file = FileName(row.Children[1].Text);
                    if (!available.Contains(file)) { missing.Add(file); missingIds.TryAdd(file, row.Children[0].Text); continue; }
                    if (looseNames.Contains(file)) continue;
                    if (names.ContainsKey(file)) continue;
                    string id = row.Children[0].Text;
                    // Bound the candidate before concatenation or diagnostic escaping. ZAR member names hold 63 Latin-1 chars.
                    if (id.Length is < 1 or > 59 || id.Any(c => c > 255 || c is '/' or '\\') || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                        throw new InvalidDataException($"{path}: sound ID {JsonData.ShownText(id)} cannot name a WAV (1–59 Latin-1 filename characters are required).");
                    string name = id + ".wav";
                    SourceProject.CheckSavablePath(root, $"{SourceBuilder.SoundsFolder}/{name}");
                    names.Add(file, name);
                }
            // Loose files own their original names. Only archived sources may be displaced by sound-ID naming.
            Dictionary<string, string> requested = new(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in names)
                if (!requested.TryAdd(pair.Value, pair.Key))
                    throw new InvalidDataException($"MW3 sound sources {JsonData.ShownText(requested[pair.Value])} and {JsonData.ShownText(pair.Key)} would both become {JsonData.ShownText(pair.Value)}; sound IDs must produce distinct filenames.");
            HashSet<string> targets = new(names.Values, StringComparer.OrdinalIgnoreCase);
            HashSet<string> reserved = new(available.Concat(targets).Concat(missing), StringComparer.OrdinalIgnoreCase);
            BoundedDiagnostics diagnostics = new(Notes);
            foreach (string file in names.Keys.Order(StringComparer.Ordinal).ToArray())
                if (looseNames.Contains(names[file]))
                {
                    string target = names[file];
                    names[file] = ArchivedName(target);
                    diagnostics.Add($"Archive audio {file} was named {names[file]} to preserve the loose filename {target}.");
                }
            targets = new(names.Values, StringComparer.OrdinalIgnoreCase);
            foreach (string file in available.Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                if (looseNames.Contains(file) || names.ContainsKey(file) || !targets.Contains(file)) continue;
                string archived = ArchivedName(file);
                names.Add(file, archived);
                diagnostics.Add($"Unreferenced audio {file} was preserved as {archived}; the sound-ID filename is used by its declared audio.");
            }
            // Validate final identities together, so an old name that another renamed file vacates is available.
            Dictionary<string, string> owners = new(StringComparer.OrdinalIgnoreCase);
            foreach (string file in available)
            {
                token.ThrowIfCancellationRequested();
                string name = names.GetValueOrDefault(file, file);
                if (missing.Contains(name))
                    throw new InvalidDataException($"MW3 sound source {JsonData.ShownText(file)} would become {JsonData.ShownText(name)}, which would incorrectly resolve a missing WAV reference; choose distinct sound IDs.");
                if (owners.TryGetValue(name, out string? other))
                    throw new InvalidDataException($"MW3 sound sources {JsonData.ShownText(other)} and {JsonData.ShownText(file)} would both become {JsonData.ShownText(name)}; sound IDs must produce distinct filenames.");
                owners.Add(name, file);
            }
            foreach (var (path, definition) in definitions)
            {
                token.ThrowIfCancellationRequested();
                var resource = Resources[definition.Resource];
                var tree = Rewrite(resource.Tree);
                if (ReferenceEquals(tree, resource.Tree)) continue;
                byte[] bytes = definition.Text ? ZrdText.Encode(tree, token, SourceProject.MaximumSourceTextBytes) : ZrdWriter.Write(tree, token);
                await ReplaceAsync(path, bytes);
                Resources[definition.Resource] = (resource.Archive, resource.Member, tree);
            }
            if (missing.Count > 0)
            {
                diagnostics.Add($"{missing.Count} WAV filenames declared in sounds.zrd are absent from the supplied audio; those references were kept unchanged.");
                foreach (string file in missing.Order(StringComparer.Ordinal)) diagnostics.Add($"Missing audio {file}, declared by sound ID {missingIds[file]}.");
            }
            return names;

            string ArchivedName(string original)
            {
                string stem = Path.GetFileNameWithoutExtension(original);
                for (int suffix = 1; suffix <= available.Count + names.Count + missing.Count + 1; suffix++)
                {
                    token.ThrowIfCancellationRequested();
                    string candidate = stem + "_archived" + (suffix == 1 ? "" : suffix.ToString(System.Globalization.CultureInfo.InvariantCulture)) + ".wav";
                    if (candidate.Length > 63) throw new InvalidDataException($"MW3 archive sound {JsonData.ShownText(original)} cannot fit an archived filename.");
                    if (!reserved.Add(candidate)) continue;
                    SourceProject.CheckSavablePath(root, $"{SourceBuilder.SoundsFolder}/{candidate}"); return candidate;
                }
                throw new InvalidDataException("No free filename for an archived MW3 sound.");
            }

            IEnumerable<ZrdNode> Rows(ZrdNode tree)
            {
                Stack<ZrdNode> pending = new(); pending.Push(tree);
                while (pending.TryPop(out var node))
                {
                    token.ThrowIfCancellationRequested();
                    if (node.Kind != ZrdKind.Array) continue;
                    if (IsRow(node)) yield return node;
                    for (int i = node.Children.Count - 1; i >= 0; i--)
                        if (node.Children[i].Kind == ZrdKind.Array) pending.Push(node.Children[i]);
                }
            }
            ZrdNode Rewrite(ZrdNode node)
            {
                token.ThrowIfCancellationRequested();
                if (node.Kind != ZrdKind.Array) return node;
                ZrdNode[]? edited = null;
                for (int i = 0; i < node.Children.Count; i++)
                {
                    var child = node.Children[i]; var changed = Rewrite(child);
                    if (i == 1 && IsRow(node) && names.TryGetValue(FileName(child.Text), out string? name) && child.Text != name)
                        changed = child with { Text = name };
                    if (!ReferenceEquals(child, changed)) (edited ??= node.Children.ToArray())[i] = changed;
                }
                return edited == null ? node : node with { Children = edited };
            }
        }

        private static bool IsRow(ZrdNode node) => node.Children is [{ Kind: ZrdKind.String }, { Kind: ZrdKind.String } file, ..]
            && file.Text.EndsWith(".wav", StringComparison.OrdinalIgnoreCase);
        private static string FileName(string value)
        {
            int start = Math.Max(value.LastIndexOf('/'), value.LastIndexOf('\\')) + 1;
            if (value.Length - start > 63) throw new InvalidDataException("A WAV filename in sounds.zrd exceeds the archive's 63-character member-name limit.");
            return value[start..];
        }
    }
}
