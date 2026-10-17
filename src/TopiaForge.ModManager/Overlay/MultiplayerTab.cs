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
    /// </summary>
    internal sealed class MultiplayerTab : IManagerTab, IManagerLiveTab
    {
        private readonly MultiplayerSessionsPresenter screens;
        private MultiplayerTabView? view;

        internal MultiplayerTab(MultiplayerSessionsPresenter screens)
        {
            this.screens = screens;
        }

        public string Title => ManagerTabLookup.Multiplayer;

        public void Build(TopiaForgeContainer content, ManagerTabContext context)
        {
            screens.OnShown();
            view = new MultiplayerTabView(context.Host, screens);
            view.Build(content);
        }

        public void Tick() => view?.Tick();

        public void OnHidden()
        {
            view = null;
            screens.OnHidden();
        }
    }
}
