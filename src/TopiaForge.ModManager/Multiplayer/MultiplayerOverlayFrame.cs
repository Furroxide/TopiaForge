using System;
using TopiaForge.ModManager.Sessions;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The multiplayer part of the manager overlay's frame, in the order the overlay runs it: the sessions presenter, then
    /// voice chat and player nametags, then what shows outside the tab (the prompts, the toasts and the connecting overlay), then the MULTIPLAYER
    /// tab while it shows. Each stage runs through the <see cref="MultiplayerFrameGuard"/>, so a stage that fails is logged
    /// once, redacted, and the stages after it, and the rest of the plugin's frame, run on. A frame that does not fail
    /// allocates nothing.
    /// </summary>
    internal sealed class MultiplayerOverlayFrame
    {
        internal const string PresenterStage = "the sessions presenter";
        internal const string VoiceStage = "voice chat";
        internal const string VoiceStartStage = "starting voice chat";
        internal const string VoiceTeardownStage = "closing voice chat";
        internal const string OutsideStage = "the prompts, toasts and connecting overlay";
        internal const string TabStage = "the MULTIPLAYER tab";
        internal const string StartStage = "loading the multiplayer settings";
        internal const string BuildStage = "building the MULTIPLAYER tab";
        internal const string HiddenStage = "hiding the MULTIPLAYER tab";
        internal const string TeardownStage = "closing the multiplayer interface";
        internal const string LaunchStage = "the answer to a game started for hosting";
        internal const string MenuStage = "the answer to a return to the main menu";

        private readonly MultiplayerFrameGuard guard;
        private readonly Action presenterTick;
        private readonly Action outside;
        private readonly Action tab;
        private readonly Action? voiceTick;
        private readonly Action? nametags;
        private float deltaTime;

        /// <param name="guard">Where a stage's failure is caught and logged.</param>
        /// <param name="presenter">The sessions presenter, which ticks every frame, overlay open or not.</param>
        /// <param name="outside">Shows the prompts, the toasts and the connecting overlay.</param>
        /// <param name="tab">Ticks the MULTIPLAYER tab while it shows.</param>
        /// <param name="voice">Voice chat, which ticks every frame with the frame's seconds, overlay open or not; null for none.</param>
        /// <param name="nametags">Updates the presence labels independently of interactive overlays; null for none.</param>
        internal MultiplayerOverlayFrame(MultiplayerFrameGuard guard, MultiplayerSessionsPresenter presenter, Action outside, Action tab, Action<float>? voice = null, Action? nametags = null)
        {
            this.nametags = nametags;
            this.guard = guard ?? throw new ArgumentNullException(nameof(guard));
            if (presenter == null) throw new ArgumentNullException(nameof(presenter));
            this.outside = outside ?? throw new ArgumentNullException(nameof(outside));
            this.tab = tab ?? throw new ArgumentNullException(nameof(tab));
            presenterTick = () => presenter.Tick(deltaTime);
            if (voice != null) voiceTick = () => voice(deltaTime);
        }

        /// <summary>Main thread, every frame.</summary>
        internal void Tick(float frameSeconds)
        {
            deltaTime = frameSeconds;
            guard.Run(PresenterStage, presenterTick);
            if (voiceTick != null) guard.Run(VoiceStage, voiceTick);
            if (nametags != null) guard.Run("player nametags", nametags);
            guard.Run(OutsideStage, outside);
            guard.Run(TabStage, tab);
        }
    }
}
