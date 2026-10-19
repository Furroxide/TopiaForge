using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The runtime's sink for the TopiaForgeUi kit's intent hook (WS3.4). The kit opens the scope of a player's action on
    /// one of its controls, named after the control's owner, and carries the intent over what it defers. ModRuntime
    /// installs one for its intent tracking and removes it when it shuts down. It never throws.
    /// </summary>
    internal sealed class MultiplayerIntentKitHook : ITopiaForgeIntentSink
    {
        private readonly MultiplayerIntents intents;

        internal MultiplayerIntentKitHook(MultiplayerIntents intents)
        {
            this.intents = intents ?? throw new ArgumentNullException(nameof(intents));
        }

        public object? BeginPlayerAction(string ownerId) => intents.EnterUiActivation(ownerId).Restore;

        public object? Capture() => intents.CaptureDeferred();

        public object? Resume(object? captured) => intents.EnterDeferred(captured as IntentRecord).Restore;

        public void End(object? restore) => intents.Scope.Restore(restore);
    }
}
