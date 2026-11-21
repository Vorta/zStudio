using System.IO;
using System.Windows;
using Microsoft.Win32;
using zStudio.Automation;
using zStudio.Core.Formats;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private async Task OpenZmapUiAsync()
    {
        if (zmapBusy) return;
        OpenFileDialog dialog = new() { Filter = "RECOIL HUD maps (*.zmap)|*.zmap", CheckFileExists = true };
        if (dialog.ShowDialog(this) == true) await ChangeZmapUiAsync(dialog.FileName, false);
    }

    private async Task ChangeZmapUiAsync(string? path, bool create)
    {
        if (zmap is not { } editor || zmapBusy) return;
        long revision = editor.Revision;
        var newMap = create ? NewZmap() : null;
        var candidate = create ? null : await ReadZmapAsync(path!, shutdownToken);
        if (!await ResolveZmapChangesAsync() || zmap != editor) return;
        if (candidate != null && editor.Revision != revision) candidate = await ReadZmapAsync(candidate.Path, shutdownToken);
        ZmapMessage("");
        if (candidate != null) editor.Replace(candidate);
        else editor.Replace(newMap!);
    }

    private Zmap NewZmap()
    {
        var (minimum, maximum) = ZmapLayer.Viewport!.ZmapWorldBounds;
        minimum.Y = maximum.Y = 0;
        Zmap map = new(0, minimum, maximum, []);
        ZmapCodec.Validate(map);
        return map;
    }

    private async Task<ZmapFile.Snapshot> ReadZmapAsync(string path, CancellationToken token)
    {
        zmapBusy = true;
        RefreshZmap();
        try { return await Task.Run(() => ZmapFile.OpenSnapshot(path, token), token); }
        finally { zmapBusy = false; RefreshZmap(); }
    }

    private async Task<bool> SaveZmapUiAsync(bool saveAs)
    {
        if (zmap is not { Map: not null } editor || zmapBusy) return false;
        if (HasZmapPathDraft)
        {
            ZmapMessage("Finish or cancel the outline draft before saving.");
            return false;
        }
        if (HasZmapDraft && !ResolveZmapDraftUi()) return false;
        string? destination = null;
        if (saveAs || editor.SavePath == null)
        {
            SaveFileDialog dialog = new()
            {
                Filter = "RECOIL HUD maps (*.zmap)|*.zmap", DefaultExt = ".zmap", AddExtension = true,
                FileName = editor.Document.SourceWorld!.Mission + ".zmap",
                CheckPathExists = true, OverwritePrompt = false
            };
            if (dialog.ShowDialog(this) != true) return false;
            destination = dialog.FileName;
        }
        await SaveZmapAsync(editor, destination, shutdownToken);
        return true;
    }

    private async Task SaveZmapAsync(ZmapEditorSession editor, string? destination, CancellationToken token)
    {
        RequireZmapIdle();
        var (path, snapshot, expected) = editor.PrepareSave(destination);
        zmapBusy = true;
        RefreshZmap();
        try
        {
            var (bytes, backup) = await Task.Run(() =>
            {
                byte[] encoded = ZmapCodec.Write(snapshot, token);
                string? retained = ZmapFile.Save(path, encoded, expected, token);
                return (encoded, retained);
            }, token);
            editor.Saved(path, snapshot, bytes);
            if (automationRequest.Value) CommitRunningJob();
            ZmapMessage(backup == null ? $"Saved {Path.GetFileName(path)}" : $"Saved {Path.GetFileName(path)}. Previous copy retained at {backup}.", problem: false);
        }
        finally { zmapBusy = false; RefreshZmap(); }
    }

    private bool ResolveZmapDraftUi()
    {
        if (!HasZmapDraft) return true;
        if (HasZmapPathDraft)
        {
            if (MessageBox.Show(this, "Discard the unfinished outline draft?", "ZMAP input",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return false;
            CancelZmapDraft();
            RefreshZmap();
            return true;
        }
        var answer = MessageBox.Show(this, "Apply the unfinished ZMAP input? Choose No to discard it.", "ZMAP input",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) CancelZmapDraft();
        else if (zmapFieldsDirty) ApplyZmapFields();
        else { ZmapMessage("Finish or cancel the ZMAP gesture first."); return false; }
        RefreshZmap();
        return true;
    }

    private async Task<bool> ResolveZmapChangesAsync()
    {
        if (zmapBusy) { ZmapMessage("Wait for the ZMAP file operation to finish."); return false; }
        if (zmap == null) return true;
        try
        {
            if (!ResolveZmapDraftUi()) return false;
            if (!zmap.Dirty) return true;
            string name = zmap.SavePath ?? "the untitled ZMAP";
            var answer = MessageBox.Show(this, $"Save changes to {name}? Choose No to discard them.", "Unsaved ZMAP",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            return answer == MessageBoxResult.No || answer == MessageBoxResult.Yes && await SaveZmapUiAsync(false);
        }
        catch (Exception error) when (error is StudioCommandException or IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            MessageBox.Show(this, error.Message, "ZMAP", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
    }

    private async Task<bool> ResolveZmapExitAsync()
    {
        if (zmap == null) return true;
        if (!await ResolveZmapChangesAsync()) return false;
        LeaveZmap();
        return true;
    }
}
