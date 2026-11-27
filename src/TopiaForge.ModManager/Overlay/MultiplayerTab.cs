using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager
{
    /// <summary>A manager tab that is told each frame while it shows, and when it stops showing.</summary>
    internal interface IManagerLiveTab
    {
        void Tick();

        void OnHidden();
    }

    /// <summary>
    /// The overlay's MULTIPLAYER tab: hosting, browsing, connecting to and leaving sessions, on the Paper card, through the
    /// TopiaForgeUi kit. The presenter lives as long as the overlay; this tab only builds and binds its view while shown.
    /// Building and hiding run through the multiplayer guard, as the overlay's frame does, so a view that fails to build
    /// leaves the tab empty instead of breaking the frame that opened it; its per-frame tick runs in that frame's guard.
    /// </summary>
    internal sealed class MultiplayerTab : IManagerTab, IManagerLiveTab
    {
        private readonly MultiplayerSessionsPresenter screens;
        private readonly MultiplayerFrameGuard guard;
        private readonly IVoiceScreen? voice;
        private MultiplayerTabView? view;

        /// <param name="screens">The sessions presenter.</param>
        /// <param name="guard">Where building and hiding run.</param>
        /// <param name="voice">VOICE's screen; null when voice chat could not start.</param>
        internal MultiplayerTab(MultiplayerSessionsPresenter screens, MultiplayerFrameGuard guard, IVoiceScreen? voice)
        {
            this.screens = screens;
            this.guard = guard;
            this.voice = voice;
        }

        public string Title => ManagerTabLookup.Multiplayer;

        public void Build(TopiaForgeContainer content, ManagerTabContext context)
        {
            view = null;
            guard.Run(MultiplayerOverlayFrame.BuildStage, () =>
            {
                screens.OnShown();
                var built = new MultiplayerTabView(context.Host, screens, voice);
                built.Build(content);
                view = built;
            });
        }

        public void Tick() => view?.Tick();

        public void OnHidden()
        {
            var shown = view;
            view = null;
            if (shown != null) guard.Run(MultiplayerOverlayFrame.HiddenStage, shown.OnHidden);
            guard.Run(MultiplayerOverlayFrame.HiddenStage, screens.OnHidden);
        }
    }
}
