using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TopiaForge.Mods;

namespace TopiaForge.CreatorTools.Shared
{
    internal sealed partial class CreatorWorkbench
    {
        private UiNode BuildContent()
        {
            var selected = SelectedRoster();
            var left = new UiColumn(
                BuildCatalogContent(),
                new UiText("SCENE ROSTER", UiTextStyle.Heading),
                new UiText("Owned objects can be duplicated or removed. Native scene targets are temporary, reversible edits only.", UiTextStyle.Caption),
                BuildRosterList(),
                new UiRow(
                    new UiButton("duplicate-selected", "Duplicate", () => Execute(DuplicateSelected), UiButtonStyle.Secondary, selected != null && CanMutate),
                    new UiButton(
                        "remove-selected",
                        selected?.Owned == true ? "Remove" : "Temporarily hide",
                        () => Execute(RemoveSelected),
                        UiButtonStyle.Danger,
                        selected?.Owned == true || selected?.NativeTarget != null
                            && (selected.NativeTarget.Capabilities & CreatorSceneTargetCapabilities.TemporaryVisibility) != 0),
                    new UiButton("refresh-native", "Discover native targets", () => ExecuteBool(RefreshNativeRoster), UiButtonStyle.Ghost)));
            var center = new UiColumn(BuildProjectContent());
            var right = new UiColumn(
                BuildTransformContent(selected),
                BuildRobotContent(selected));
            return new UiColumn(
                new UiRow(
                    new UiButton("refresh-content", "Refresh catalog", () => Execute(RefreshEverything), UiButtonStyle.Secondary),
                    new UiButton(
                        "enable-mutations",
                        CanMutate ? "Changes isolated" : "Enable isolated changes",
                        RequestMutationAccess,
                        UiButtonStyle.Secondary,
                        options.ProjectScope == CreatorProjectScope.Global && !CanMutate),
                    new UiButton("hide-workbench", "Hide", requestHide, UiButtonStyle.Ghost),
                    new UiButton("end-session", "End Session & Restore", ConfirmEndSession, UiButtonStyle.Danger)),
                new UiSplitPane(
                    new UiScroll(left, 680f),
                    new UiSplitPane(
                        new UiScroll(center, 680f),
                        new UiScroll(right, 680f),
                        UiSplitOrientation.Horizontal,
                        0.64f),
                    UiSplitOrientation.Horizontal,
                    0.28f),
                new UiText(MutationStatusText(), UiTextStyle.Caption, CanMutate ? UiTone.Success : UiTone.Warning),
                new UiText(status, UiTextStyle.Caption, status.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0 ? UiTone.Warning : UiTone.Neutral));
        }

        private UiNode BuildCatalogContent()
        {
            var choices = new[]
            {
                new UiChoice("all", "All content"),
                new UiChoice("robot", "Robots"),
                new UiChoice("character", "Characters"),
                new UiChoice("item", "Items"),
                new UiChoice("prop", "Props"),
                new UiChoice("vehicle", "Vehicles")
            };
            var visible = FilteredCatalog().ToArray();
            var items = visible.Select(entry => new UiListItem(
                entry.Id,
                entry.DisplayName,
                entry.Description,
                entry.Kind.ToString().ToUpperInvariant()));
            var selected = visible.Any(entry => entry.Id == selectedCatalogId) ? selectedCatalogId : null;
            return new UiColumn(
                new UiText("SPAWN CATALOG", UiTextStyle.Heading),
                BuildCatalogSourceStatus(),
                new UiRow(
                    new UiDropdown("catalog-kind", "Kind", choices, kindFilter, value => { kindFilter = value; RefreshUi(); }),
                    new UiTextInput("catalog-search", "Search", search, value => { search = value; RefreshUi(); }, "name or description", 128)),
                new UiVirtualList("catalog-list", items, id => { selectedCatalogId = id; RefreshUi(); }, selected, 6),
                new UiButton("spawn-selected", "Spawn selected at aim point", () => Execute(SpawnSelected), enabled: selected != null && CanMutate));
        }

        private UiNode BuildCatalogSourceStatus()
        {
            var sourceStatus = content.Catalog.Sources
                .Where(source => source.State != CreatorCatalogSourceState.Ready || source.EntryCount == 0)
                .Select(source => new UiText(
                    source.DisplayName + ": "
                        + (string.IsNullOrWhiteSpace(source.Message)
                            ? source.State + " (" + source.EntryCount.ToString(CultureInfo.InvariantCulture) + " entries)"
                            : source.Message),
                    UiTextStyle.Caption,
                    source.State == CreatorCatalogSourceState.Unavailable ? UiTone.Danger : UiTone.Warning))
                .Cast<UiNode>()
                .ToList();
            if (content.Catalog.Sources.Count == 0)
            {
                sourceStatus.Add(new UiText(
                    "No CreatorContent providers reported availability. RobotKit entries may still be used.",
                    UiTextStyle.Caption,
                    UiTone.Warning));
            }
            else if (content.Catalog.Entries.Count == 0)
            {
                sourceStatus.Add(new UiText(
                    "CreatorContent catalog is empty. Enable a compatible content provider to spawn items, props, vehicles, or event characters.",
                    UiTextStyle.Caption,
                    UiTone.Warning));
            }
            return new UiColumn(sourceStatus);
        }

        private UiNode BuildRosterList()
        {
            var items = roster.Select(entry => new UiListItem(
                entry.Id,
                entry.DisplayName,
                entry.Owned ? "Tool-owned" : "Native scene target — changes restore with End Session & Restore",
                entry.Kind.ToString().ToUpperInvariant()));
            var selected = roster.Any(entry => entry.Id == selectedRosterId) ? selectedRosterId : null;
            return new UiVirtualList("roster-list", items, SelectRoster, selected, 6);
        }

        private UiNode BuildTransformContent(CreatorRosterEntry? entry)
        {
            var enabled = entry != null && CanMutate;
            return new UiColumn(
                new UiText("LOCATION", UiTextStyle.Heading),
                new UiRow(
                    new UiTextInput("position-x", "X", xText, value => xText = value, maximumLength: 24, enabled: enabled),
                    new UiTextInput("position-y", "Y", yText, value => yText = value, maximumLength: 24, enabled: enabled),
                    new UiTextInput("position-z", "Z", zText, value => zText = value, maximumLength: 24, enabled: enabled)),
                new UiRow(
                    new UiTextInput("rotation-x", "Rot X", qxText, value => qxText = value, maximumLength: 24, enabled: enabled),
                    new UiTextInput("rotation-y", "Rot Y", qyText, value => qyText = value, maximumLength: 24, enabled: enabled)),
                new UiRow(
                    new UiTextInput("rotation-z", "Rot Z", qzText, value => qzText = value, maximumLength: 24, enabled: enabled),
                    new UiTextInput("rotation-w", "Rot W", qwText, value => qwText = value, maximumLength: 24, enabled: enabled)),
                new UiRow(
                    new UiTextInput("scale-x", "Scale X", sxText, value => sxText = value, maximumLength: 24, enabled: enabled),
                    new UiTextInput("scale-y", "Scale Y", syText, value => syText = value, maximumLength: 24, enabled: enabled),
                    new UiTextInput("scale-z", "Scale Z", szText, value => szText = value, maximumLength: 24, enabled: enabled)),
                new UiRow(
                    new UiButton("apply-transform", "Apply transform", () => Execute(ApplyTransform), enabled: enabled),
                    new UiButton("move-to-aim", "Move to aim", () => Execute(MoveToAim), UiButtonStyle.Secondary, enabled),
                    new UiButton("nudge-up", "+1 m up", () => Execute(() => Nudge(new Vec3(0f, 1f, 0f))), UiButtonStyle.Ghost, enabled)),
                new UiRow(
                    new UiButton("nudge-forward", "+1 m X", () => Execute(() => Nudge(new Vec3(1f, 0f, 0f))), UiButtonStyle.Ghost, enabled),
                    new UiButton(
                        "toggle-native-hidden",
                        entry?.NativeHidden == true ? "Show native" : "Hide native",
                        () => Execute(ToggleNativeHidden),
                        UiButtonStyle.Ghost,
                        CanMutate && entry?.NativeTarget != null
                            && (entry.NativeTarget.Capabilities & CreatorSceneTargetCapabilities.TemporaryVisibility) != 0)));
        }

        private UiNode BuildRobotContent(CreatorRosterEntry? entry)
        {
            var isRobot = entry?.IsRobot == true && CanMutate;
            var canProgram = entry?.Robot != null && CanMutate;
            return new UiColumn(
                new UiText("ROBOT LAB", UiTextStyle.Heading),
                BuildRobotAppearanceContent(entry),
                new UiRow(
                    new UiButton("brain-dormant", "Dormant", () => Execute(() => SetSelectedBrain(RobotBrainMode.Dormant)), UiButtonStyle.Secondary, isRobot),
                    new UiButton("brain-autonomous", "Autonomous", () => Execute(() => SetSelectedBrain(RobotBrainMode.Autonomous)), UiButtonStyle.Secondary, isRobot)),
                new UiRow(
                    new UiButton("objective-idle", "Idle", () => Execute(() => SetObjective(RobotObjective.Idle())), UiButtonStyle.Ghost, canProgram),
                    new UiButton("objective-follow", "Follow player", () => Execute(() => SetObjective(RobotObjective.Follow("PLAYER"))), UiButtonStyle.Ghost, canProgram),
                    new UiButton("robot-emote", "Wave", () => Execute(() => SetEmote(":wave:")), UiButtonStyle.Ghost, canProgram)),
                new UiTextInput("persona-name", "Personality name", personaName, value => personaName = value, maximumLength: 128, enabled: isRobot),
                new UiTextInput("persona-instructions", "Personality instructions", personaInstructions, value => personaInstructions = value, maximumLength: 4096, enabled: isRobot),
                new UiButton("apply-personality", "Preview personality", () => Execute(ApplyPersonality), UiButtonStyle.Secondary, isRobot),
                new UiText("PROGRAM BY CONVERSATION", UiTextStyle.Heading),
                new UiText(options.ConversationEnabled ? "Each sent line uses the configured RobotKit brain service." : "Disabled by config; deterministic controls above remain available.", UiTextStyle.Caption),
                new UiTextInput("program-line", "Tell the robot what to do", chatText, value => chatText = value, maximumLength: 512, enabled: canProgram && options.ConversationEnabled),
                new UiRow(
                    new UiButton("start-program-chat", "Start / reset chat", () => Execute(() => BeginConversation()), UiButtonStyle.Secondary, canProgram && options.ConversationEnabled),
                    new UiButton("send-program-line", "Send", () => Execute(SubmitConversation), enabled: canProgram && options.ConversationEnabled && conversationTask == null)),
                new UiText(chatStatus, UiTextStyle.Caption));
        }

        private UiNode BuildProjectContent()
        {
            var projectItems = projectSummaries.Select(project => new UiListItem(
                project.Id,
                project.DisplayName,
                project.ModifiedAtUtc.ToString("u", CultureInfo.InvariantCulture),
                project.Scope.ToString().ToUpperInvariant()));
            var selectedProject = projectSummaries.Any(project => project.Id == selectedProjectId) ? selectedProjectId : null;
            var nodes = new List<UiNode>
            {
                new UiText("EVENT PROJECTS", UiTextStyle.Heading),
                new UiText("Local projects are validated before save or run. Runtime flow is capped at 64 actions per frame and 10,000 per session.", UiTextStyle.Caption),
                new UiVirtualList("project-list", projectItems, id => { selectedProjectId = id; RefreshUi(); }, selectedProject, 4),
                new UiRow(
                    new UiButton("load-project", "Load", () => Execute(LoadSelectedProject), enabled: selectedProject != null),
                    new UiButton("delete-project", "Delete", ConfirmDeleteSelectedProject, UiButtonStyle.Danger, selectedProject != null && projectDeleteTask == null),
                    new UiButton("new-project", "New project", () => Execute(CreateProject), UiButtonStyle.Secondary),
                    new UiButton("save-project", "Save", () => Execute(SaveProject), UiButtonStyle.Secondary, activeProject != null),
                    new UiButton(
                        "confirm-native-bindings",
                        "Confirm native bindings",
                        ConfirmNativeBindings,
                        UiButtonStyle.Secondary,
                        activeProject?.NativeBindings.Count > 0),
                    new UiButton("run-project", "Run", () => Execute(RunProject), enabled: activeProject != null && CanMutate),
                    new UiButton("stop-project", "Stop", () => Execute(() => StopProject(true)), UiButtonStyle.Danger, runner != null))
            };
            if (activeProject != null)
            {
                nodes.Add(new UiText(activeProject.DisplayName + "  •  " + activeProject.Nodes.Count + " nodes", UiTextStyle.Caption));
                nodes.Add(BuildGraphCanvas(activeProject));
                nodes.Add(BuildProjectAuthoringControls());
            }
            return new UiColumn(nodes);
        }

        private IEnumerable<CreatorCatalogEntry> FilteredCatalog()
        {
            foreach (var entry in catalog)
            {
                if (kindFilter != "all" && !string.Equals(kindFilter, entry.Kind.ToString(), StringComparison.OrdinalIgnoreCase)) continue;
                if (search.Length > 0 && entry.DisplayName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0
                    && entry.Description.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0) continue;
                yield return entry;
            }
        }

        private OperationResult<string> RefreshEverything()
        {
            RefreshCatalog();
            RefreshNativeRoster();
            BeginProjectList();
            return OperationResult<string>.Success("Catalog, roster, and project library refreshed.");
        }

        private void Execute(Func<OperationResult<string>> action)
        {
            var result = action();
            status = result.Succeeded ? result.Value ?? "Done." : result.ErrorMessage;
            if (!result.Succeeded) context.Ui.ShowToast(status, UiTone.Danger);
            RefreshUi();
        }

        private void ExecuteBool(Func<OperationResult<bool>> action)
        {
            var result = action();
            if (!result.Succeeded) context.Ui.ShowToast(result.ErrorMessage, UiTone.Danger);
            RefreshUi();
        }

        private void RefreshUi()
        {
            if (window != null) window.SetContent(BuildContent());
            RefreshHud(force: true);
        }

        private void ConfirmEndSession()
        {
            if (confirmation?.IsOpen == true) return;
            var result = context.Ui.ShowModal(
                new UiModalRequest(
                    "END SESSION & RESTORE?",
                    "Owned content will be removed and every native transform, brain, and personality preview will be restored.",
                    "END SESSION & RESTORE",
                    destructive: true),
                confirmed =>
                {
                    confirmation = null;
                    if (confirmed) requestEnd();
                });
            result.TryGetValue(out confirmation);
        }
    }
}
