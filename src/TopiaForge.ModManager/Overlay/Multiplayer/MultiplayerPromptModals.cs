using System;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    // The Gallery builds the same views with ordinary kit dialogs for its fixtures.
    internal static class MultiplayerPromptModals
    {
        internal static TopiaForgeModalInstance Consent(UiHost host, string title)
        {
#if TOPIAFORGE_LOADER_TRUST_DIALOGS
            return host.Modal.TrustCustom(title, 560f, persistent: true);
#else
            return host.Modal.Custom(title, TopiaForgeScheme.Paper, 560f, persistent: true);
#endif
        }

        internal static TopiaForgeModalInstance Destructive(UiHost host, ScreenPrompt prompt)
        {
            var body = prompt.Paragraphs.Count == 0 ? string.Empty : prompt.Paragraphs[0];
            var cancel = prompt.CancelLabel.Length == 0 ? "CANCEL" : prompt.CancelLabel;
            Action confirmed = () => prompt.Answer(true);
            Action cancelled = () => prompt.Answer(false);
#if TOPIAFORGE_LOADER_TRUST_DIALOGS
            return host.Modal.Destructive(prompt.Title, body, prompt.ConfirmLabel, confirmed, cancelled,
                cancel, plainText: true, persistent: true, trust: prompt.Kind == ScreenPromptKind.TrustIdentity);
#else
            return host.Modal.Destructive(prompt.Title, body, prompt.ConfirmLabel, confirmed, cancelled,
                cancel, plainText: true, persistent: true);
#endif
        }
    }
}
