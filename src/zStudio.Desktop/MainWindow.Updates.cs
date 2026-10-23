using System.IO;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using zStudio.Automation;

namespace zStudio.Desktop;

/// <summary>
/// The update check: the status bar's Update available item (directly left of Preview notices), Help → Check for updates and
/// Help → Check for updates automatically, and MCP <c>update_check</c> and <c>state</c>'s <c>update</c>. The user's setting rules
/// automatic checks (at most one a day, after the window shows); an explicit check (menu or MCP) is a request of its own.
/// </summary>
public partial class MainWindow
{
    private UpdateChecker? updates;
    /// <summary>The checker; a private test profile's never contacts GitHub, and tests give one over a fake handler.</summary>
    internal UpdateChecker Updates { get => updates ??= UpdateChecker.ForThisProcess(StudioVersion); set => updates = value; }
    /// <summary>The running check, which later requests share.</summary>
    private Task<UpdateCheckResult>? updateCheckWork;
    /// <summary>The last successful check: this session's, or the saved one.</summary>
    private UpdateRelease? latestRelease;
    /// <summary>An explicit check found <see cref="latestRelease"/> this session, so it shows even with automatic checks off.</summary>
    private bool latestReleaseRequested;
    /// <summary>An explicit check finished this session (whatever its outcome), so its result shows even with automatic checks off.</summary>
    private bool updateRequested;
    private string? updateFailure;
    /// <summary>Explicit checks waiting for their result: the item shows "Checking for updates…".</summary>
    private int updateRequests;
    /// <summary>An explicit check's result other than an update, shown for a few seconds.</summary>
    private (string Glyph, string Text, string ToolTip)? updateNotice;
    private readonly DispatcherTimer updateNoticeTimer = new() { Interval = TimeSpan.FromSeconds(8) };

    private void InitializeUpdates()
    {
        var s = ViewModel.Settings;
        if (s.UpdateCheckedUtc is { } checkedUtc && ReleaseVersion.TryParse(s.UpdateLatestVersion, out var version))
            latestRelease = new(version.ToString(), UpdateChecker.ReleasePage(s.UpdateLatestUrl) ?? UpdateChecker.LatestReleasePage, checkedUtc);
        AutomaticUpdatesMenu.IsChecked = s.CheckForUpdates;
        updateNoticeTimer.Tick += (_, _) => { updateNoticeTimer.Stop(); updateNotice = null; ShowUpdateStatus(); };
        ShowUpdateStatus();
    }

    /// <summary>Once the window shows (App startup only, never tests): the automatic check, when it is on and due.</summary>
    internal void StartAutomaticUpdateCheck()
    {
        if (!ViewModel.Settings.CheckForUpdates || !AutomaticUpdateCheckDue()) return;
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (ViewModel.Settings.CheckForUpdates && AutomaticUpdateCheckDue() && !shutdownToken.IsCancellationRequested) _ = CheckForUpdatesAsync(requested: false, CancellationToken.None);
        });
    }
    /// <summary>No check in the last 24 hours, successful or not (a check time in the future, after a clock change, counts as none).</summary>
    private bool AutomaticUpdateCheckDue()
    {
        if (ViewModel.Settings.UpdateAttemptedUtc is not { } attempted) return true;
        var age = DateTimeOffset.UtcNow - attempted;
        return age >= UpdateChecker.Interval || age < -TimeSpan.FromMinutes(5);
    }

    /// <summary>
    /// A check, joining the running one. <paramref name="requested"/>: an explicit request (menu or MCP), whose progress and
    /// result the status bar shows; an automatic check shows only an update it finds. Only cancellation throws.
    /// </summary>
    private async Task<UpdateCheckResult> CheckForUpdatesAsync(bool requested, CancellationToken token)
    {
        if (requested) { updateRequests++; updateNotice = null; updateNoticeTimer.Stop(); ShowUpdateStatus(); }
        try
        {
            var work = updateCheckWork;
            if (work == null)
            {
                work = RunUpdateCheckAsync();
                // A check that cannot contact GitHub finishes at once; only a running one is shared.
                if (!work.IsCompleted) updateCheckWork = work;
            }
            var result = await work.WaitAsync(token);
            if (requested && !shutdownToken.IsCancellationRequested)
            {
                updateRequested = true;
                if (result.Release != null) latestReleaseRequested = true;
                ShowUpdateNotice(result);
            }
            return result;
        }
        finally { if (requested) { updateRequests--; ShowUpdateStatus(); } }
    }
    private async Task<UpdateCheckResult> RunUpdateCheckAsync()
    {
        try
        {
            var s = ViewModel.Settings;
            // Every check that asks GitHub counts, so a failing one (offline, refused, rate limited) waits a day too.
            if (Updates.CanCheck) s.UpdateAttemptedUtc = DateTimeOffset.UtcNow;
            UpdateCheckResult result;
            try { result = await Updates.CheckAsync(shutdownToken); }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not StackOverflowException)
            { result = new(null, "Couldn't check for updates: " + ex.Message); }
            if (result.Release is { } release)
            {
                latestRelease = release; updateFailure = null;
                s.UpdateCheckedUtc = release.CheckedUtc; s.UpdateLatestVersion = release.Version; s.UpdateLatestUrl = release.Url;
            }
            else updateFailure = result.Failure;
            // Closing saves the settings itself.
            if (Updates.CanCheck && !shutdownToken.IsCancellationRequested)
                try { s.Save(); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            return result;
        }
        finally { updateCheckWork = null; ShowUpdateStatus(); }
    }

    /// <summary>
    /// What the update item knows: checking; available or current from the last successful check that counts (with automatic
    /// checks off, only one asked for this session); disabled when automatic checks are off and no check was asked for; otherwise
    /// unknown, with the reason (an explicit check that failed, offline, refused, a private test profile, not checked yet).
    /// </summary>
    private (string Status, string? Reason) UpdateStatus()
    {
        if (updateCheckWork != null) return ("checking", null);
        bool automatic = ViewModel.Settings.CheckForUpdates;
        if (latestRelease is { } release && (automatic || latestReleaseRequested))
            return Updates.IsNewer(release.Version) switch
            {
                true => ("available", null), false => ("current", null),
                null => ("unknown", $"This build's version ({Updates.CurrentVersion}) can't be compared with releases.")
            };
        if (!automatic && !updateRequested) return ("disabled", "Automatic update checks are off (Help → Check for updates automatically); update_check check asks GitHub now.");
        return ("unknown", updateFailure ?? (Updates.CanCheck ? "Not checked yet." : UpdateChecker.PrivateProfileReason));
    }
    /// <summary>MCP: what the status bar's update item knows (<c>state</c>'s <c>update</c>, <c>update_check</c>).</summary>
    private JsonObject UpdateState()
    {
        var (status, reason) = UpdateStatus();
        return new()
        {
            ["status"] = status, ["automatic"] = ViewModel.Settings.CheckForUpdates, ["current"] = Updates.CurrentVersion,
            ["latest"] = latestRelease?.Version, ["url"] = latestRelease?.Url, ["checkedUtc"] = latestRelease?.CheckedUtc, ["reason"] = reason
        };
    }

    private void ShowUpdateNotice(UpdateCheckResult result)
    {
        const string warning = "";
        updateNotice = result.Release is { } release
            ? Updates.IsNewer(release.Version) switch
            {
                true => null, // The update itself shows.
                false => ("", "zStudio is up to date", $"The latest release is {release.Version}; this is {Updates.CurrentVersion} (checked {release.CheckedUtc.ToLocalTime():t})."),
                null => (warning, "Couldn't check for updates", $"This build's version ({Updates.CurrentVersion}) can't be compared with {release.Version}.")
            }
            : (warning, "Couldn't check for updates", result.Failure ?? "The check failed.");
        updateNoticeTimer.Stop();
        if (updateNotice != null) updateNoticeTimer.Start();
        ShowUpdateStatus();
    }
    /// <summary>The status bar's update item: an explicit check's progress, then its result for a few seconds, else a newer release.</summary>
    private void ShowUpdateStatus()
    {
        var release = latestRelease;
        (string Glyph, string Text, string ToolTip, string? Version)? shown =
            updateRequests > 0 ? ("", "Checking for updates…", "Asking GitHub for the latest zStudio release", null)
            : updateNotice is { } notice ? (notice.Glyph, notice.Text, notice.ToolTip, null)
            : UpdateStatus().Status == "available" && release != null ? ("", "Update available", $"zStudio {release.Version} is available; this is {Updates.CurrentVersion}. Open its release page on GitHub.", release.Version)
            : null;
        StatusUpdate.Visibility = shown == null ? Visibility.Collapsed : Visibility.Visible;
        if (shown is not { } item) return;
        StatusUpdateIcon.Text = item.Glyph; StatusUpdateText.Text = item.Text; StatusUpdate.ToolTip = item.ToolTip;
        StatusUpdateIcon.SetResourceReference(TextElement.ForegroundProperty, item.Version != null ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        StatusUpdateVersion.Visibility = item.Version != null ? Visibility.Visible : Visibility.Collapsed;
        StatusUpdateVersionText.Text = item.Version ?? "";
        System.Windows.Automation.AutomationProperties.SetName(StatusUpdate, item.Text);
        System.Windows.Automation.AutomationProperties.SetHelpText(StatusUpdate, item.ToolTip);
    }

    /// <summary>Update available opens the release page; a check's result is dismissed.</summary>
    private void UpdateStatusClick(object sender, RoutedEventArgs e)
    {
        if (updateRequests > 0) return;
        if (updateNotice != null) { updateNoticeTimer.Stop(); updateNotice = null; ShowUpdateStatus(); return; }
        if (UpdateStatus().Status != "available" || latestRelease is not { } release) return;
        // Only a page of zStudio's releases on GitHub opens.
        string page = UpdateChecker.ReleasePage(release.Url) ?? UpdateChecker.LatestReleasePage;
        OpenLink(page);
    }
    private async void CheckForUpdatesClick(object sender, RoutedEventArgs e)
    {
        try { await CheckForUpdatesAsync(requested: true, CancellationToken.None); }
        catch (OperationCanceledException) { }
    }
    private void AutomaticUpdatesClick(object sender, RoutedEventArgs e) => SetAutomaticUpdateChecks(AutomaticUpdatesMenu.IsChecked);
    private void SetAutomaticUpdateChecks(bool enabled)
    {
        var s = ViewModel.Settings;
        s.CheckForUpdates = enabled; AutomaticUpdatesMenu.IsChecked = enabled;
        try { s.Save(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notice("Couldn't save the update check setting: " + ex.Message); }
        // Turned on, it checks at once when the last check is a day old.
        if (enabled && updateCheckWork == null && AutomaticUpdateCheckDue() && !shutdownToken.IsCancellationRequested) _ = CheckForUpdatesAsync(requested: false, CancellationToken.None);
        ShowUpdateStatus();
    }

    private void RegisterUpdateCommands(StudioCommands registry)
    {
        Register(registry, "update_check", $"Read or run the update check, as the status bar's Update available item (directly left of Preview notices) and Help → Check for updates / Check for updates automatically do. It asks GitHub for zStudio's latest published release ({UpdateChecker.LatestReleaseApi}; drafts and prereleases excluded) and compares its tag with this build's version (SemVer; a prerelease build sees its release as an update). action state (default) reads the same update object as zstudio_state: status available (a newer release; the GUI shows Update available), current, unknown (reason: not checked yet, a check asked for that failed, offline, GitHub refused or its rate limit is used up, or a private test profile, which never contacts GitHub), disabled (automatic checks are off and no check was asked for this session) or checking; automatic; current and latest versions; url, the release page on github.com/Vorta/zStudio/releases (the GUI's click opens it in the browser; MCP returns it and opens nothing); checkedUtc of the last successful check. check asks GitHub now (an explicit request, also when automatic checks are off; the request times out after {UpdateChecker.Timeout.TotalSeconds:0} seconds; system proxy discovery, which the timeout cannot interrupt, can take longer) and adds failure when it failed. automatic with enabled turns automatic checks on or off (saved; after zStudio starts, at most one a day: a check, successful or not, makes the next wait a day); turning them on checks at once when the last check is a day old. zstudio_state reports the same update object without waiting for changes in progress.", true,
            [P("action", "string", "state (default) reads; check asks GitHub now; automatic sets automatic checks.", false, "state", "check", "automatic"),
             P("enabled", "boolean", "For automatic (required there): whether zStudio checks for updates by itself after it starts.")], async (a, token) =>
        {
            string action = Text(a, "action", "state");
            if (action != "automatic" && a.ContainsKey("enabled")) throw new StudioCommandException("invalid_argument", "enabled applies to action automatic only.");
            string? failure = null;
            switch (action)
            {
                case "check": failure = (await CheckForUpdatesAsync(requested: true, token)).Failure; break;
                case "automatic":
                    if (a["enabled"] is null) throw new StudioCommandException("invalid_argument", "Required argument for automatic: enabled");
                    SetAutomaticUpdateChecks(Flag(a, "enabled")); break;
            }
            var state = UpdateState();
            if (action == "check") state["failure"] = failure;
            return new StudioResult(state);
        });
    }
}
