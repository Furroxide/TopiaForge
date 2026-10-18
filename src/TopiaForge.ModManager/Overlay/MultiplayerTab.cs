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
        private MultiplayerTabView? view;

        internal MultiplayerTab(MultiplayerSessionsPresenter screens, MultiplayerFrameGuard guard)
        {
            this.screens = screens;
            this.guard = guard;
        }

        public string Title => ManagerTabLookup.Multiplayer;

        public void Build(TopiaForgeContainer content, ManagerTabContext context)
        {
            view = null;
            guard.Run(MultiplayerOverlayFrame.BuildStage, () =>
            {
                screens.OnShown();
                var built = new MultiplayerTabView(context.Host, screens);
                built.Build(content);
                view = built;
            });
        }

        public void Tick() => view?.Tick();

        public void OnHidden()
        {
            view = null;
            guard.Run(MultiplayerOverlayFrame.HiddenStage, screens.OnHidden);
        }
    }
}
