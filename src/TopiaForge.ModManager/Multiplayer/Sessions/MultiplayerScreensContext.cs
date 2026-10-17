using System;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>What the Multiplayer tab's presenters share: the environment, settings, status, content readiness and prompts.</summary>
    internal sealed class MultiplayerScreensContext
    {
        private readonly Action<ScreenPrompt> ask;
        private readonly Action<string, ScreenTone> notify;

        internal MultiplayerScreensContext(
            IMultiplayerSessionEnvironment environment,
            MultiplayerSettingsService settings,
            MultiplayerStatusTracker status,
            ContentReadinessPresenter content,
            Action<ScreenPrompt> ask,
            Action<string, ScreenTone> notify)
        {
            Environment = environment ?? throw new ArgumentNullException(nameof(environment));
            Settings = settings ?? throw new ArgumentNullException(nameof(settings));
            Status = status ?? throw new ArgumentNullException(nameof(status));
            Content = content ?? throw new ArgumentNullException(nameof(content));
            this.ask = ask ?? throw new ArgumentNullException(nameof(ask));
            this.notify = notify ?? throw new ArgumentNullException(nameof(notify));
            Privacy = new PrivacyGate(settings, ask);
        }

        internal IMultiplayerSessionEnvironment Environment { get; }

        internal MultiplayerSettingsService Settings { get; }

        internal MultiplayerStatusTracker Status { get; }

        internal ContentReadinessPresenter Content { get; }

        internal PrivacyGate Privacy { get; }

        /// <summary>Gets whether the player may start anything: live sessions are on and the settings have loaded.</summary>
        internal bool CanAct => Status.IsLiveAvailable && Settings.IsLoaded;

        /// <summary>Gets whether hosting or joining may start: as <see cref="CanAct"/>, with this game's mods identified.</summary>
        internal bool CanStartSession => CanAct && Content.IsReady;

        internal void Ask(ScreenPrompt prompt) => ask(prompt);

        internal void Notify(string text, ScreenTone tone = ScreenTone.Neutral) => notify(text, tone);
    }
}
