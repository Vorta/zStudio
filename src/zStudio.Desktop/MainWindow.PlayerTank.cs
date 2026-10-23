using System.Windows;
using zStudio.Automation;
using zStudio.Core;
using zStudio.Rendering;

namespace zStudio.Desktop;

/// <summary>
/// The player tank's preview state in a source map (docs/agents/preview.md, player tank): its card sections, the
/// empty pane's Player tank row, the status bar item and the MCP fields share one choice per open map
/// (<see cref="SourceWorldSession.PlayerTank"/>), which the shared mission builder draws. A change rebuilds the view as a
/// difficulty change does; it creates no edit, draft or undo step.
/// </summary>
public partial class MainWindow
{
    private PlayerTankPanel? playerTankPanel;
    private PlayerTankPanel PlayerTankSectionsPanel
    {
        get
        {
            if (playerTankPanel != null) return playerTankPanel;
            playerTankPanel = new();
            playerTankPanel.Chosen += next => _ = ReportUi(() => ChoosePlayerTankAsync(next));
            return playerTankPanel;
        }
    }
    /// <summary>The newest choice the view does not show yet (its rebuild is running), or null.</summary>
    private PlayerTankState? posingTank;
    private long playerTankGeneration;
    /// <summary>The last pose failure and sub-mode weapon switch of the shown map, as the card says them.</summary>
    private string? playerTankFailure, playerTankSubstitution;
    /// <summary>The weapon the game switched to in sub, while the note about it stands (the map keeps that weapon as its choice).</summary>
    private string? substitutedSecondary;

    /// <summary>The shown world's mission scene (a source map's or a compiled world's Whole world), or null.</summary>
    private MissionSceneContext? ShownWorldMission => animation == null && motion == null && SceneHost.Visibility == Visibility.Visible && shownAsset?.Kind == AssetKind.World
        ? scene?.Mission : null;
    /// <summary>The shown world's player tank, or null.</summary>
    private MissionPlayerTank? ShownPlayerTank => ShownWorldMission?.PlayerTank;
    /// <summary>Whether the shown view takes a player tank choice: a source map's Whole world.</summary>
    private bool PlayerTankAdjustable => sourceChromeActive && shownDocument?.SourceWorld != null && ShownPlayerTank != null;
    /// <summary>A rebuild or another refresh of the view runs: a choice now is refused (busy), so the radios wait.</summary>
    private bool PlayerTankRebuilding => sourceWorkspaceBusy || shownDocument?.SourceWorld?.IsRebuilding == true || retiringScene != null || staticRefresh != null && posingTank == null;
    /// <summary>The mission folder of the shown world (m1 …), as the notes and reasons name it.</summary>
    private string ShownMissionName => shownDocument?.SourceWorld?.Mission
        ?? System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(shownDocument?.Path ?? "")) ?? "";
    /// <summary>The choice the radios show: a posing one, else the map's, else the mission's start.</summary>
    private PlayerTankState ChosenTank(MissionPlayerTank tank) => posingTank ?? shownDocument?.SourceWorld?.PlayerTank ?? tank.Defaults;
    /// <summary>What the map keeps as its choice for a drawn tank: null at the mission's start, so a changed start is never kept as a choice.</summary>
    private static PlayerTankState? KeptChoice(MissionPlayerTank tank) => tank.Shown == tank.Defaults ? null : tank.Shown;
    /// <summary>Why the map's player tank could not be prepared, as the card, the empty pane and MCP refusals say it; null when it was.</summary>
    private static string? PlayerTankFailureText(MissionSceneContext mission) => mission.PlayerTankFailure is { } failure
        ? $"The player tank ({JsonData.ShownText(mission.LocalPlayer?.Name ?? "", 64)}) can't be prepared: {failure}" : null;

    /// <summary>
    /// A published scene's tank: the map's choice becomes what the view draws (Tracked after a failed or withdrawn type, the
    /// weapon the game switches to in sub), with the failure listed in Problems (Diagnostics opens).
    /// </summary>
    private void AdoptPlayerTank(DocumentModel doc, MissionSceneContext? mission)
    {
        if (doc.SourceWorld is { } session && mission?.PlayerTank is { } tank)
        {
            session.PlayerTank = KeptChoice(tank);
            if (publishedStaticOptions != null && ReferenceEquals(doc, shownDocument)) publishedStaticOptions = publishedStaticOptions with { Tank = session.PlayerTank };
            playerTankFailure = tank.Failure == null ? null
                : $"Couldn't show {PlayerTankPresentation.Label(tank.Requested.Type)}: {tank.Failure} (see Diagnostics). The view shows {PlayerTankPresentation.Label(tank.Shown.Type)}.";
            // The map keeps the weapon the game switched to, so later rebuilds draw it without switching: the note stays while
            // the tank is in sub with that weapon.
            if (tank.SecondarySubstituted)
            {
                playerTankSubstitution = $"In sub the game switches {PlayerTankPresentation.ShortName(tank.Requested.Secondary)} to {PlayerTankPresentation.ShortName(tank.Shown.Secondary)}.";
                substitutedSecondary = tank.Shown.Secondary;
            }
            else if (tank.Shown.Type != PlayerTankType.Sub || tank.Shown.Secondary != substitutedSecondary) playerTankSubstitution = substitutedSecondary = null;
            if (tank.Failure != null && sourceChromeActive) SetToolsVisible(true, problems: true);
        }
        RefreshPlayerTankViews();
    }

    /// <summary>The card's sections, the empty pane's row and the status bar follow the choice.</summary>
    private void RefreshPlayerTankViews()
    {
        if (!sourceChromeActive) return;
        // Only a card that shows the tank (its sections, or the empty pane's row) has anything to update.
        if (CurrentInspectionCard is { } card && (card.Selection == null || playerTankPanel?.Parent != null)) card.Refresh();
        RefreshSourceStatus();
    }

    /// <summary>
    /// The player tank's sections for a selection of its card, or null for any other selection. The selection belongs to the
    /// tank when its node is the tank's root or below it: two AIV records can place one root (retail m2 Easy), which leaves
    /// that root without a unique actor, but it stays the player's tank.
    /// </summary>
    private FrameworkElement? PlayerTankSections(SceneViewport viewport, SceneInspection selected)
    {
        // While a structural rebuild retires the view, its card keeps the tank's sections (and Details as collapsed as it was)
        // until the rebuilt view's card replaces it.
        if (retiringScene != null && viewport == retiringScene && playerTankPanel != null
            && viewport.InspectionContent is SceneInspectionCard retiringCard && retiringCard.HostsExtraSections(playerTankPanel)) return playerTankPanel;
        if (!sourceChromeActive || viewport != scene || shownDocument?.SourceWorld is not { } session || !PlayerTankAdjustable || ShownPlayerTank is not { } tank) return null;
        if (selected.AiNode != null || viewport.PreviewScene is not { } data || !WithinTank(data, selected.SelectionNode >= 0 ? selected.SelectionNode : selected.Node, tank.Actor.Root)) return null;
        var state = ChosenTank(tank);
        var panel = PlayerTankSectionsPanel;
        panel.Show(new(tank, state, session.Mission, posingTank != null && posingTank.Type != tank.Shown.Type,
            posingTank != null && (posingTank.Primary != tank.Shown.Primary || posingTank.Secondary != tank.Shown.Secondary),
            PlayerTankRebuilding, playerTankFailure, playerTankSubstitution));
        return panel;

        static bool WithinTank(GameScene data, int node, int root)
        {
            for (int depth = 0; depth < 256 && node >= 0 && node < data.Nodes.Count; depth++)
            {
                if (node == root) return true;
                node = data.Nodes[node].Parents.FirstOrDefault(-1);
            }
            return false;
        }
    }

    /// <summary>The empty pane's Player tank row: the tank and the choice, or why the map has none on this difficulty.</summary>
    private SceneInspectionCard.PlayerTankRow? PlayerTankRowSummary()
    {
        if (!sourceChromeActive || shownDocument?.SourceWorld == null || shownAsset?.Kind != AssetKind.World || scene?.Mission is not { } mission) return null;
        if (mission.PlayerTank is not { } tank)
            return new("", null, PlayerTankFailureText(mission) ?? $"No vehicle spawns on {mission.Layout.Difficulty}, so this map has no player tank there.");
        return new(tank.Actor.Name, PlayerTankPresentation.Summary(ChosenTank(tank)), null);
    }
    /// <summary>Selects the player tank as its World tree row does, which opens its card.</summary>
    private void PickPlayerTank()
    {
        if (ShownPlayerTank is not { } tank) return;
        try
        {
            RefreshSceneTree();
            if (sceneTree?.Reveal(tank.Actor.Root) is not { Selectable: true } row) throw new StudioCommandException("stale_record", "The player tank is not in the World tree.");
            if (!ResolveSelectionDrafts(sceneTreeDocument)) return;
            SelectSceneTreeRow(row);
        }
        catch (StudioCommandException ex) { Report(ex); }
    }
    /// <summary>The status bar's item while the choice is not the mission's start.</summary>
    private void RefreshPlayerTankStatus()
    {
        var state = PlayerTankAdjustable && ShownPlayerTank is { } tank && ChosenTank(tank) is var chosen && chosen != tank.Defaults ? chosen : null;
        StatusPlayerTankGroup.Visibility = state != null ? Visibility.Visible : Visibility.Collapsed;
        StatusPlayerTank.Text = state != null ? PlayerTankPresentation.StatusText(state) : "";
    }

    /// <summary>
    /// Checks a requested choice against the shown map: a source map's Whole world with a player tank, the type its data
    /// offers, a secondary sub mode allows (when <paramref name="secondaryChanged"/>), and no rebuild in progress.
    /// </summary>
    private (DocumentModel Document, AssetRecord Asset, MissionPlayerTank Tank) RequirePlayerTankChange(PlayerTankState next, bool secondaryChanged)
    {
        if (sourceMapOpening != null || sourceWorldsOpening > 0)
            throw new StudioCommandException("not_ready", "A map is opening; change the player tank once it is shown.");
        if (!sourceChromeActive || shownDocument?.SourceWorld is not { } session || shownAsset is not { Kind: AssetKind.World } asset || scene?.Mission is not { } mission)
            throw new StudioCommandException("unsupported", "The player tank's type and weapons are a source map preview.");
        if (mission.PlayerTank is not { } tank)
            throw new StudioCommandException("unsupported", PlayerTankFailureText(mission) ?? $"{session.Mission} has no player tank on {mission.Layout.Difficulty}.");
        if (PlayerTankRebuilding)
            throw new StudioCommandException("busy", "The source scene is being evaluated; change the player tank when it is ready.");
        if (!next.IsValid) throw new StudioCommandException("invalid_argument", "Use a known type, a primary weapon (RFPG, ERFPG) and a secondary weapon.");
        if (PlayerTankPresentation.Reason(session.Mission, tank, next.Type) is { } reason) throw new StudioCommandException("unsupported", reason);
        if (secondaryChanged && next.Type == PlayerTankType.Sub && !tank.Model(next.Secondary).AllowedInSub)
            throw new StudioCommandException("unsupported", $"{PlayerTankPresentation.ManualName(next.Secondary)} is not available in sub mode: the game allows no beam or fire weapon there.");
        return (shownDocument, asset, tank);
    }

    /// <summary>
    /// A radio's choice: a pending edit is resolved first (a Yes applies it and its rebuild runs), then the view rebuilds with
    /// the choice; a newer choice supersedes an older one still posing. A refused choice puts the radios back.
    /// </summary>
    private async Task ChoosePlayerTankAsync(PlayerTankState next)
    {
        try
        {
            var doc = shownDocument;
            if (!await ResolveInspectionDraftsAsync(doc))
            {
                RestorePlayerTankRadios();
                Notice("The player tank was not changed: the pending edit was kept.");
                return;
            }
            await SourceWorldsIdleAsync();
            var current = ShownPlayerTank is { } shown ? ChosenTank(shown) : null;
            var (target, asset, _) = RequirePlayerTankChange(next, next.Secondary != current?.Secondary);
            await (previewWork = StartPlayerTankRefresh(target, asset, next));
        }
        catch (StudioCommandException ex)
        {
            RestorePlayerTankRadios();
            if (ex.Code != "context_changed") Notice(ex.Message);
        }
    }
    /// <summary>The radios return to the choice the map holds (the panel draws again even though its inputs are unchanged).</summary>
    private void RestorePlayerTankRadios() { playerTankPanel?.Invalidate(); RefreshPlayerTankViews(); }

    /// <summary>Takes <paramref name="next"/> as the map's choice and rebuilds the view; a refused or failed rebuild restores the shown one.</summary>
    private Task<Guid?> StartPlayerTankRefresh(DocumentModel doc, AssetRecord asset, PlayerTankState next)
    {
        doc.SourceWorld!.PlayerTank = next;
        posingTank = next; long generation = ++playerTankGeneration;
        RefreshPlayerTankViews();
        var refresh = RefreshStaticSceneAsync(doc, asset);
        _ = EndPosingAsync();
        return refresh;

        async Task EndPosingAsync()
        {
            // A refresh that supersedes this one (a newer choice, LOD, difficulty) draws the choice too: posing lasts until
            // the last of them ends.
            Task awaited = refresh;
            while (true)
            {
                try { await awaited; }
                catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException)) { }
                if (generation != playerTankGeneration) return;
                if (staticRefreshWork is { IsCompleted: false } newer && !ReferenceEquals(newer, awaited)) { awaited = newer; continue; }
                break;
            }
            posingTank = null;
            // A rebuild that did not publish leaves the map's choice at what the view shows.
            if (doc.SourceWorld is { } session && ReferenceEquals(doc, shownDocument) && ShownPlayerTank is { } tank && staticRefresh == null) session.PlayerTank = KeptChoice(tank);
            RestorePlayerTankRadios();
        }
    }

    /// <summary>preview_state.bft: what the view draws on the player tank, the choices and why some are unavailable; null without a tank.</summary>
    private object? PlayerTankPreviewState()
    {
        if (ShownWorldMission is not { } mission) return null;
        if (mission.PlayerTank is not { } tank)
            return PlayerTankFailureText(mission) is { } failure ? new { adjustable = false, vehicle = mission.LocalPlayer?.Name, failure } : null;
        bool adjustable = PlayerTankAdjustable;
        var state = adjustable ? ChosenTank(tank) : tank.Shown;
        string missionName = ShownMissionName;
        var secondary = tank.Model(state.Secondary);
        object Describe(PlayerTankState s) => new { type = PlayerTankPresentation.Id(s.Type), primary = s.Primary, secondary = s.Secondary };
        return new
        {
            vehicle = tank.Actor.Name, node = tank.Actor.Root, adjustable,
            type = PlayerTankPresentation.Id(state.Type), primary = state.Primary, secondary = state.Secondary,
            shown = Describe(tank.Shown), defaults = Describe(tank.Defaults),
            types = tank.Types.Select(t => new { id = PlayerTankPresentation.Id(t.Type), available = t.Available, transitions = t.Transitions.Count == 0 ? null : t.Transitions,
                reason = PlayerTankPresentation.Reason(missionName, tank, t.Type) }).ToArray(),
            secondaries = tank.Weapons.Where(w => !w.Weapon.IsPrimary).Select(w => new { id = w.Weapon.Id, key = w.Weapon.Key, model = w.Model, present = w.Present,
                allowedInSub = w.AllowedInSub, available = !(state.Type == PlayerTankType.Sub && !w.AllowedInSub) }).ToArray(),
            gun = new { drawn = tank.Drawn, model = secondary.Model, missing = secondary.Listed && secondary.Model != null && !secondary.Present ? secondary.Model : null,
                sharedWith = PlayerTankPresentation.SharedWith(tank, state.Secondary), line = PlayerTankPresentation.ModelLine(tank, state.Secondary) },
            posing = adjustable && posingTank != null, rebuilding = adjustable && PlayerTankRebuilding,
            failure = adjustable ? playerTankFailure : tank.Failure, substitution = adjustable ? playerTankSubstitution : null,
            hiddenAtStart = tank.HiddenAtStart, note = PlayerTankPresentation.Note(missionName, tank),
        };
    }
}
