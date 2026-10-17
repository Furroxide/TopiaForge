using System;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>The approved privacy prompts as screen prompts, word for word from <see cref="MultiplayerPrivacyText"/>.</summary>
    internal static class PrivacyPrompts
    {
        /// <summary>The first-time disclosure, with its one button; closing it answers no.</summary>
        internal static ScreenPrompt Disclosure(Action<bool> answer) =>
            new ScreenPrompt(
                ScreenPromptKind.Disclosure,
                MultiplayerPrivacyText.DisclosureTitle,
                MultiplayerPrivacyText.DisclosureParagraphs(),
                MultiplayerPrivacyText.DisclosureButton,
                string.Empty,
                answer);

        /// <summary>The per-session prompt for a session that can only be reached directly.</summary>
        internal static ScreenPrompt Direct(Action<bool> answer) =>
            new ScreenPrompt(
                ScreenPromptKind.DirectConnection,
                MultiplayerPrivacyText.DirectTitle,
                new[] { MultiplayerPrivacyText.DirectBody },
                MultiplayerPrivacyText.DirectAllowButton,
                MultiplayerPrivacyText.DirectCancelButton,
                answer);

        /// <summary>A destructive confirmation, which the view shows through the kit's destructive modal.</summary>
        internal static ScreenPrompt Destructive(string title, string body, string confirmLabel, Action<bool> answer) =>
            new ScreenPrompt(ScreenPromptKind.Destructive, title, new[] { body }, confirmLabel, "CANCEL", answer);
    }
}
