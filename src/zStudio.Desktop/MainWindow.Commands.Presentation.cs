using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using zStudio.Automation;

namespace zStudio.Desktop;

public partial class MainWindow
{
    private bool automationCloseRequested;
    private void RegisterWorkspacePresentation(StudioCommands r)
    {
        Register(r,"presentation","Read or set independent temporary fullscreen (F11) and Hide UI (F10) modes for the current 3D preview. Hide UI leaves the view alone: menus, command bar, panes, status bar, banners, Properties and editor guides (grid, axes, bounds, handles, outlines) hide, clicks select and edit nothing, and navigation and Fly go on; it leaves the polygon zone tool and the terrain brush (refused during a stroke) and the zone tool cannot be engaged while it lasts. Preserves drafts, layout preferences, camera and playback. Enabling requires a 3D preview; disabling always works. Leaving 3D clears Hide UI and retains fullscreen. Neither mode is saved.",true,
            [P("fullscreen","boolean","Use the full current monitor without window borders."),P("uiHidden","boolean","Temporarily hide workbench chrome and editor guides.")],a=>
        {
            var state = SetPresentation(a["fullscreen"]?.GetValue<bool>(), a["uiHidden"]?.GetValue<bool>());
            return Result(new { fullscreen=state.Fullscreen, uiHidden=state.UiHidden, available=state.Available });
        });
        Register(r,"workspace_view","Read or set presentation preferences and read effective navigatorMode (hidden, tabbed, split). In a source map, navigator/inspector and navigatorWidth/inspectorWidth show, hide and size its World tree (left) and docked Properties pane (right) for the session, without overwriting compiled-explorer preferences, and tools/toolsHeight show, hide and size its Diagnostics pane (the problems alone, no toolsTab) the same way. Supports theme, preset, pane visibility/dimensions and stable section indices, including detached Files. Does not commit drafts.",true,
            [WorkspaceChanges],a=>
        {
            // Validate all tab requests before changing preferences. A rejected
            // request must not leave its earlier theme/layout changes applied.
            foreach (var (key, value) in a["changes"] as JsonObject ?? new())
                if (key == "navigatorTab") ValidateNavigatorSection(value!.GetValue<int>());
                else if (key == "toolsTab" && ViewModel.IsSourceProject) throw new StudioCommandException("unsupported","A source map's Diagnostics pane has no tabs.");
                else if (key is "inspectorTab" or "toolsTab")
                    ValidateTab(key == "inspectorTab" ? InspectorTabs : ToolTabs, value!.GetValue<int>());
            foreach(var (key,value) in a["changes"] as JsonObject ?? new())
            {
                switch(key)
                {
                    case "resetLayout": if(value!.GetValue<bool>()) ResetLayoutClick(this,new()); break;
                    case "theme": string theme=value!.GetValue<string>(); if(theme is not ("System" or "Light" or "Dark")) throw new StudioCommandException("invalid_argument","Unknown theme."); ApplyTheme(theme); ViewModel.Settings.Theme=theme; break;
                    case "preset": string preset=value!.GetValue<string>(); if(preset is not ("Inspect" or "Edit" or "Debug" or "Focus preview")) throw new StudioCommandException("invalid_argument","Unknown preset."); PresetClick(new MenuItem { Header=preset },new()); break;
                    case "navigator": if (ViewModel.IsSourceProject) sourceOutlinerVisible=value!.GetValue<bool>(); else Layout.NavigatorVisible=value!.GetValue<bool>(); break;
                    case "inspector": if (ViewModel.IsSourceProject) sourcePropertiesVisible=value!.GetValue<bool>(); else Layout.InspectorVisible=value!.GetValue<bool>(); break;
                    case "tools": if (ViewModel.IsSourceProject) { sourceToolsVisible=value!.GetValue<bool>(); RememberSourceTools(); } else Layout.ToolsVisible=value!.GetValue<bool>(); break;
                    case "toolsMaximized": toolsMaximized=value!.GetValue<bool>(); break;
                    case "navigatorWidth": if (ViewModel.IsSourceProject) sourceOutlinerWidth = Math.Clamp(value!.GetValue<double>(), 200, 650); else Layout.NavigatorWidth=value!.GetValue<double>(); break;
                    case "filesWidth": Layout.FilesWidth=value!.GetValue<double>(); break;
                    case "inspectorWidth": if (ViewModel.IsSourceProject) sourcePropertiesWidth = Math.Clamp(value!.GetValue<double>(), 260, 650); else Layout.InspectorWidth=value!.GetValue<double>(); break;
                    case "toolsHeight": if (ViewModel.IsSourceProject) { sourceToolsHeight = Math.Clamp(value!.GetValue<double>(), 100, 700); RememberSourceTools(); } else Layout.ToolsHeight=value!.GetValue<double>(); break;
                    case "inspectionPanelHeight": Layout.InspectionPanelHeight=value!.GetValue<double>(); break;
                    case "navigatorTab": SelectNavigatorSection(value!.GetValue<int>()); break;
                    case "inspectorTab": SetTab(InspectorTabs,value!.GetValue<int>()); break;
                    case "toolsTab": SetTab(ToolTabs,value!.GetValue<int>()); break;
                    case "backupOnSave": ViewModel.Settings.CreateBackupOnSave=value!.GetValue<bool>(); break;
                    default: throw new StudioCommandException("unknown_option",key);
                }
            }
            if (a.ContainsKey("changes"))
            {
                Layout.Normalize();
                if (a["changes"] is JsonObject changes && changes.ContainsKey("inspectionPanelHeight"))
                    CurrentInspectionCard?.SetPanelHeight(Layout.InspectionPanelHeight);
                ArrangeWorkspace(); SaveWorkspacePreferences(); ViewModel.Settings.Save();
            }
            return Result(new { theme=ViewModel.Settings.Theme,layout=Layout,navigatorMode=NavigatorMode,sourceMap=ViewModel.IsSourceProject ? new { outliner=sourceOutlinerVisible, outlinerWidth=sourceOutlinerWidth, properties=sourcePropertiesVisible, propertiesWidth=sourcePropertiesWidth, diagnostics=SourceToolsVisible, diagnosticsHeight=sourceToolsHeight ?? Layout.ToolsHeight } : null,ViewModel.Settings.CreateBackupOnSave });
        });
        Register(r,"window","Read or change zStudio window state; close requires clean documents and resolved drafts.",true,
            [P("action","string","Window action.",true,"read","activate","minimize","maximize","restore","close")],a=>
        {
            string action=Text(a,"action");
            switch(action)
            {
                case "activate": Activate(); break;
                case "minimize": WindowState=WindowState.Minimized; break;
                case "maximize":
                    if (PresentationState.Fullscreen) throw new StudioCommandException("busy","Exit fullscreen before maximizing the window.");
                    WindowState=WindowState.Maximized; break;
                case "restore": WindowState=WindowState.Normal; break;
                case "close": RequireNoDrafts(); if(ViewModel.Documents.Any(d=>d.IsDirty)) throw new StudioCommandException("unsaved_changes","Save or explicitly close dirty documents first."); _=CloseAfterResponseAsync(); break;
            }
            return Result(new { state=WindowState.ToString(),Width,Height,closing=action=="close" });
        });
        Register(r,"properties_open","Open the reusable Properties window for an explicit edited asset or animation sequence/event; current pending drafts must be resolved first. Model records remain pinned by identity through replacement and undo/redo. Concurrent edits reject stale loads.",true,
            [..AssetParameters,P("sequence","string","Animation sequence GUID."),P("event","string","Animation event GUID.")],async (a,token)=>
        {
            RequireNoPropertiesDrafts("Resolve Properties drafts before retargeting.");
            var doc=TargetDocument(a); var asset=TargetAsset(doc,a);
            PropertiesWindow? opened;
            if(asset.Kind==Core.AssetKind.Animation)
            {
                Guid sequence = GuidArg(a,"sequence"), ev = GuidArg(a,"event");
                var entry = doc.AnimationEdits?.Package.Entries.ElementAtOrDefault(asset.Index) ?? throw new StudioCommandException("unsupported", "Animation properties are unavailable.");
                if (sequence == Guid.Empty && ev != Guid.Empty || sequence != Guid.Empty && !entry.AllSequences.Any(s => s.Id == sequence && (ev == Guid.Empty || s.Events.Any(e => e.Id == ev))))
                    throw new StudioCommandException("stale_record", "Animation sequence/event is unavailable.");
                opened = OpenAnimationProperties(doc,asset.Index,sequence,ev);
            }
            else opened = await OpenAssetPropertiesAsync(doc,asset,token,automation: true);
            token.ThrowIfCancellationRequested();
            if (opened == null || propertiesWindow != opened || opened.Document != doc || !IsPropertiesPresented(opened))
                throw new StudioCommandException("context_changed", "The requested Properties target was not published.");
            return Result(new { document=doc.SessionId,asset=asset.Id,title=opened.Title,properties=opened.CurrentJson });
        });
        Register(r,"properties_close","Close the Properties window after drafts have been explicitly resolved.",true,[],_=>
        {
            RequireNoPropertiesDrafts("Resolve Properties drafts first.");
            ++propertyRequest; propertiesWindow?.Dismiss();
            if(propertiesWindow != null) throw new StudioCommandException("pending_drafts","Properties stayed open: resolve its drafts first.");
            return Result(new { closed=true });
        });
        Register(r,"properties_state","Read the pinned Properties window identity, content and current editable fields.",false,[],_ =>
            Result(new { open=propertiesWindow != null, document=propertiesWindow?.Document?.SessionId, content=propertiesWindow?.CurrentJson, fields=((FieldEditor?)propertiesWindow?.ScriptFields ?? (FieldEditor?)propertiesWindow?.AnimationFields ?? (FieldEditor?)propertiesWindow?.PickupFields ?? (FieldEditor?)propertiesWindow?.ResourceFields ?? propertiesWindow?.SourceFields)?.DescribeAutomationFields() }));
        Register(r,"scene_properties","Inspect a scene object, focus its source-world object panel, or open the shared Properties window in other viewers, including editable mission pickups. Source-map parts resolve to their complete selection owner; synthetic reference groups return Index=null and a signed selectionOwner with read-only properties. Physical mesh/polygon inspection remains available through inspection commands. Metadata is bounded to 512 JSON nodes/8192 text characters with inspection_truncated. MW3 actor labels use 128-character prefixes; source data and JSON exports remain intact.",true,
            [PreviewParameter,P("node","integer","Scene node index or signed selectionOwner from scene_tree in this preview.",true),P("open","boolean","Focus the source-object panel, or open Properties for other viewers.")],async (a, token)=>
        {
            var viewport=TargetViewport(a); int node=Int(a,"node"); var data=viewport.PreviewScene;
            RefreshSceneTree();
            bool sourceGroup = sceneTree?.Projection?.IsSourceGroup(node) == true && sceneTreeDocument == shownDocument && sceneTree.Hierarchy.Scene == data;
            if(data == null || !sourceGroup && !viewport.CanInspectNode(node)) throw new StudioCommandException("stale_record","Scene node unavailable.");
            if (sceneTree?.Projection != null && sceneTreeDocument == shownDocument && sceneTree.Hierarchy.Scene == data)
            {
                var row = sceneTree.Reveal(node);
                if (row?.Selectable != true || shownDocument is not { } doc) throw new StudioCommandException("stale_record", "Source selection owner unavailable.");
                var metadata = SceneTreeProperties(row);
                if (Flag(a, "open"))
                {
                    RequireNoDrafts();
                    if (doc.SourceWorld != null && !IsSourceTerrain(doc, row.Index))
                    {
                        if (!(row.Index is int sourceIndex && FocusSourceObjectCard(doc, sourceIndex)) && !row.SourceGroup) throw new StudioCommandException("not_ready", "The object panel is not available.");
                        return Result(new { Index = row.Synthetic ? (int?)null : row.Index, selectionOwner = row.SourceGroup ? (int?)null : row.Index, Name = row.DisplayName, Metadata = metadata, presentation = "sceneCard", editor = CurrentInspectionCard?.ObjectEditor?.Describe() });
                    }
                    var opened = await OpenScenePropertiesAsync(doc, row, token, automation: true);
                    token.ThrowIfCancellationRequested();
                    if (opened == null || propertiesWindow != opened || opened.Document != doc || !IsPropertiesPresented(opened))
                        throw new StudioCommandException("context_changed", "The requested Properties target was not published.");
                }
                return Result(new { Index = row.Synthetic ? (int?)null : row.Index, selectionOwner = row.SourceGroup ? (int?)null : row.Index, Name = row.DisplayName, Metadata = metadata });
            }
            // A renderer may recognize a virtual owner; it can never index the physical scene array.
            if (node < 0 || node >= data.Nodes.Count) throw new StudioCommandException("stale_record", "Scene node unavailable.");
            if(Flag(a,"open"))
            {
                RequireNoDrafts(); ++propertyRequest;
                if(shownDocument is { SourceWorld: not null } sourceDoc && viewport.PickupAt(node) == null && SourceObjectNode(node) is int sourceNode && sourceDoc.SourceBuild?.Provenance.ContainsKey(sourceNode) == true)
                { if (!await ShowSourceObjectPropertiesAsync(sourceDoc, sourceNode, token)) throw new StudioCommandException("context_changed", "Properties was superseded while loading."); return Result(new { data.Nodes[node].Index, data.Nodes[node].Name, Metadata = Core.JsonData.PreviewObject(data.Nodes[node].Metadata), source = DescribeSourceObject(sourceDoc, sourceNode).Json() }); }
                var w=GetPropertiesWindow(); var actor=viewport.PickupAt(node);
                bool opened=actor?.Pickup is { } pickup && shownDocument!.PickupEdits?.Find(pickup.Source) != null
                    ? w.SetPickup(shownDocument!,pickup.Source,data.Nodes[node].Name,data.Nodes[node].Metadata,SourcePickupMove(shownDocument!))
                    : w.SetReadOnly(shownDocument!,data.Nodes[node].Name,Core.JsonData.PreviewObject(data.Nodes[node].Metadata));
                PresentProperties(w,opened);
            }
            return Result(new { data.Nodes[node].Index,data.Nodes[node].Name, Metadata = Core.JsonData.PreviewObject(data.Nodes[node].Metadata) });
        });
    }
    private static void SetTab(TabControl tabs,int index)
    {
        ValidateTab(tabs, index);
        tabs.SelectedIndex=index;
    }
    private static void ValidateTab(TabControl tabs, int index)
    {
        if(index<0 || index>=tabs.Items.Count || tabs.Items[index] is not TabItem { Visibility:Visibility.Visible,IsEnabled:true }) throw new StudioCommandException("unavailable_tab","This tab is unavailable in the current workspace.");
    }
    private async Task CloseAfterResponseAsync() { await Task.Delay(250); if (!IsLoaded) return; automationCloseRequested = true; Close(); }
}
