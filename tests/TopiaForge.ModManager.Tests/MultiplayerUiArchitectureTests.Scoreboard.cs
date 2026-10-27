using System;
using System.IO;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class MultiplayerUiArchitectureTests
    {
        private static void ScoreboardUsesCanonicalInputsAndKit(string root)
        {
            string Read(string name) => File.ReadAllText(Path.Combine(root, name));
            var adapter = Read("src/TopiaForge.ModManager/Overlay/ScoreboardOverlay.cs");
            var view = Read("src/TopiaForge.ModManager/Overlay/Multiplayer/ScoreboardView.cs");
            That(adapter.Contains("Inputs.Register", StringComparison.Ordinal) && adapter.Contains("InputBinding.Key(\"Tab\")", StringComparison.Ordinal), "scoreboard holds a registered input action");
            That(!adapter.Contains("Keyboard.current", StringComparison.Ordinal) && !adapter.Contains("Input.GetKey", StringComparison.Ordinal) &&
                !adapter.Contains("suppressWhileUiFocused: false", StringComparison.Ordinal), "scoreboard preserves the merged binding ownership gate");
            That(adapter.Contains("TopiaForgeGameTextFocus.Current.HasKeyboard", StringComparison.Ordinal) && adapter.Contains("KitTextFieldHasKeyboard", StringComparison.Ordinal) && adapter.Contains("PushToTalkSuspended", StringComparison.Ordinal) &&
                adapter.Contains("NavigationWhileSelected", StringComparison.Ordinal) && adapter.Contains("root.ReleaseFocus()", StringComparison.Ordinal), "typing, capture and released selection are accounted for");
            That(adapter.Contains("Nametags(anchors.Length)", StringComparison.Ordinal) && adapter.Contains("plugin.Nametags", StringComparison.Ordinal) &&
                adapter.Contains("verticalOffset: 0f", StringComparison.Ordinal), "pooled kit tags use the existing avatar head anchors without adding a second head height");
            That(view.Contains("parent.Scroll", StringComparison.Ordinal) && view.Contains("SignalBars()", StringComparison.Ordinal) &&
                view.Contains(".PlainText()", StringComparison.Ordinal), "rows scroll, quality uses kit bars and names render literally");
            var prompts = Read("src/TopiaForge.ModManager/Multiplayer/Sessions/ScoreboardPresenter.Actions.cs");
            That(prompts.Contains("PrivacyPrompts.Destructive", StringComparison.Ordinal), "scoreboard destructive actions use the existing kit modal bridge");
            That(Read("mods/TopiaForge.UiGallery/Pages/SessionsPage.cs").Contains("SessionsPageScoreboard.Build", StringComparison.Ordinal), "the real scoreboard view is in the gallery");
        }
    }
}
