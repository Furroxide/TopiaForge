using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>Policy-permitted target choices, durable repair, and live resolver admission.</summary>
    internal sealed class GamemodesTab : IManagerTab
    {
        private readonly Dictionary<string, LaunchRequest?> drafts = new Dictionary<string, LaunchRequest?>(StringComparer.OrdinalIgnoreCase);
        public string Title => "GAMEMODES";
        public void Build(TopiaForgeContainer content, ManagerTabContext context)
        {
            content.Label("LAUNCH TARGETS", TopiaForgeTextStyle.Display).FixedHeight(34f);
            content.Label("Choose a target and a permitted world. Launch completes when gameplay is ready.",
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).FixedHeight(30f);
            var session = context.Plugin.GetSessionService()?.Current;
            if (session != null) content.Label("Session: " + session.Phase, TopiaForgeTextStyle.Caption).FixedHeight(24f);
            var saved = context.Plugin.ReadLaunchSelection();
            var remembered = context.Plugin.ResolveRememberedSelection();
            var scroll = content.Scroll(TopiaForgeGap.Sm);
            if (!context.Plugin.CanSaveState) scroll.Content.Label(context.Plugin.StatePersistenceError,
                TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Warning);
            if (!remembered.Available)
            {
                scroll.Content.Label("Saved selection unavailable. " + remembered.RepairMessage + " Choose and save a target below, or save Main Menu.",
                    TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Warning);
                foreach (var reason in remembered.Blocks) scroll.Content.Label(Reason(reason), TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Warning);
            }
            else scroll.Content.Label(remembered.IsMainMenu ? "Saved: Main Menu" : "Saved: " + remembered.Request!.TargetId,
                TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted);
            if (saved.Kind == "unresolved-legacy" && remembered.Available)
                scroll.Content.Label("The legacy choice has one valid target. Save it explicitly to finish migration; its original values are retained until then.",
                    TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Warning);
            var automatic = scroll.Content.Toggle("Use saved selection when starting Robotopia directly", context.Plugin.State.AutoLoadOnStart,
                value => { context.Plugin.SaveLaunchSelection(saved, value); context.SetStatus("Direct-start preference saved."); });
            automatic.SetEnabled(context.Plugin.CanSaveState);
            var entries = context.Plugin.GetLaunchPreviews();
            if (entries.Count == 0) scroll.Content.Label("No targets are declared by enabled packages. Enable the required packages in Mods and restart.",
                TopiaForgeTextStyle.Body).Tone(TopiaForgeTone.Warning);
            foreach (var entry in entries) BuildTarget(scroll.Content, context, entry, saved, remembered);
            var menu = scroll.Content.Row(TopiaForgeGap.Md, TopiaForgeGap.Md, expandChildWidth: true);
            var saveMenu = menu.Button("SAVE MAIN MENU", () =>
            {
                context.Plugin.SaveLaunchSelection(LaunchSelection.MainMenu(), context.Plugin.State.AutoLoadOnStart);
                drafts.Clear(); context.SetStatus("Main Menu saved."); context.Refresh();
            }).FixedHeight(TopiaForgeTokens.ControlHeight);
            saveMenu.SetEnabled(context.Plugin.CanSaveState);
            menu.Button("MAIN MENU", async () =>
            {
                var (ok, message) = await context.Plugin.ReturnToMainMenu();
                context.SetStatus(message);
                if (ok) context.Close(); else TopiaForgeToasts.Show(message, TopiaForgeTone.Danger, 5f);
            }).FixedHeight(TopiaForgeTokens.ControlHeight);
        }
        private void BuildTarget(TopiaForgeContainer content, ManagerTabContext context, LaunchTargetPreview target,
            LaunchSelection saved, LaunchSelectionResolution remembered)
        {
            var card = content.Panel(TopiaForgePanelStyle.Plain);
            card.Label(target.Title, TopiaForgeTextStyle.Heading).FixedHeight(28f);
            card.Label(target.Description ?? target.Id, TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Muted).FixedHeight(40f);
            if (!drafts.TryGetValue(target.Id, out var chosen))
            {
                var prior = saved.Request ?? remembered.Request;
                chosen = prior != null && Same(prior.TargetId, target.Id) ? prior
                    : target.Declared.Resolved ? new LaunchRequest(target.Id) : null;
            }
            var selected = target.Choices.ToList().FindIndex(choice => Matches(choice.Request, chosen));
            var options = new[] { selected < 0 && chosen != null ? "Saved choice unavailable — select a permitted option" : "Select a world and transition" }
                .Concat(target.Choices.Select(choice => (choice.IsDeclaredDefault ? "Declared default: " : "") + choice.WorldId + " · " + choice.Transition)).ToArray();
            var dropdown = card.Dropdown(options, selected + 1, index =>
            {
                drafts[target.Id] = index > 0 && index <= target.Choices.Count ? target.Choices[index - 1].Request : null;
                context.Refresh();
            });
            dropdown.SetEnabled(target.Choices.Count != 0);
            if (selected < 0)
                foreach (var block in target.Declared.Blocks) card.Label(Reason(block), TopiaForgeTextStyle.Caption).Tone(TopiaForgeTone.Warning);
            var selectedRequest = selected < 0 ? null : target.Choices[selected].Request;
            var actions = card.Row(TopiaForgeGap.Md, TopiaForgeGap.Md, expandChildWidth: true);
            var save = actions.Button("SAVE SELECTION", () =>
            {
                if (selectedRequest == null) return;
                context.Plugin.SaveLaunchSelection(LaunchSelection.Target(selectedRequest), context.Plugin.State.AutoLoadOnStart);
                drafts.Clear(); context.SetStatus("Launch selection saved."); context.Refresh();
            });
            save.SetEnabled(selectedRequest != null && context.Plugin.CanSaveState);
            var play = actions.Button("PLAY", async () =>
            {
                if (selectedRequest == null) return;
                var (ok, message) = await context.Plugin.LaunchTarget(selectedRequest.TargetId, selectedRequest.WorldOverride, selectedRequest.TransitionOverride);
                context.SetStatus(message);
                if (ok) context.Close(); else { TopiaForgeToasts.Show(message, TopiaForgeTone.Danger, 5f); context.Refresh(); }
            });
            play.SetEnabled(selectedRequest != null);
        }
        private static bool Same(string? left, string? right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        private static bool Matches(LaunchRequest left, LaunchRequest? right) => right != null && Same(left.TargetId, right.TargetId)
            && Same(left.WorldOverride, right.WorldOverride) && left.TransitionOverride == right.TransitionOverride;
        private static string Reason(LaunchBlock block)
        {
            string repair;
            switch (block.Code)
            {
                case LaunchBlockCode.TargetPackageDisabled:
                case LaunchBlockCode.GamemodePackageDisabled:
                case LaunchBlockCode.WorldPackageDisabled:
                    repair = "Enable the required package and restart"; break;
                case LaunchBlockCode.TargetNotDeclared:
                case LaunchBlockCode.GamemodeNotDeclared:
                case LaunchBlockCode.WorldNotDeclared:
                    repair = "Install the required package or choose another target"; break;
                case LaunchBlockCode.GamemodeUnbound:
                case LaunchBlockCode.WorldUnbound:
                case LaunchBlockCode.WorldUnavailable:
                case LaunchBlockCode.NoAvailableTarget:
                    repair = "Required content is unavailable; check package errors in Mods"; break;
                case LaunchBlockCode.WorldConsentMissing:
                case LaunchBlockCode.WorldNotAdmittedByPolicy:
                case LaunchBlockCode.WorldNotStaticallyDeclared:
                    repair = "Choose a world permitted by this target"; break;
                case LaunchBlockCode.TransitionNotOffered:
                case LaunchBlockCode.TransitionUnsatisfiable:
                case LaunchBlockCode.SpawnRequirementUnsatisfied:
                    repair = "Choose a compatible world and transition"; break;
                case LaunchBlockCode.TargetPlatformUnsupported:
                case LaunchBlockCode.GamemodePlatformUnsupported:
                case LaunchBlockCode.WorldPlatformUnsupported:
                    repair = "This package does not support the current installation"; break;
                case LaunchBlockCode.PlanPackageSetMismatch:
                case LaunchBlockCode.PlanResolutionMismatch:
                    repair = "The package selection changed; restart and choose again"; break;
                default: repair = "Repair package dependencies or select a compatible package version"; break;
            }
            return repair + ": " + block.Subject + (block.SubjectVersion.Length == 0 ? "" : " " + block.SubjectVersion) + ".";
        }
    }
}
