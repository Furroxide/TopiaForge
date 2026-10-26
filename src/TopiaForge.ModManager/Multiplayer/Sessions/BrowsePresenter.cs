using System;
using System.Collections.Generic;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// BROWSE: sessions heard on the local network, favorites and history, and the Internet list, which stays empty until the
    /// TopiaForge service is configured. Listening on the local network starts only when the player asks and stops when the
    /// tab closes. While it runs and this game's mods are identified, each session heard is checked once, over the local
    /// network only, for whether this game's mods fit: Ready, Needs N or Incompatible. JOIN goes through the disclosure and
    /// the realm rules; INSTALL &amp; JOIN IN LAUNCHER hands a server link, which names no address, to the launcher.
    /// </summary>
    internal sealed partial class BrowsePresenter : IBrowseScreen
    {
        internal const int MaximumQueries = 2;

        /// <summary>The most problem lines a server's check keeps for the selection; a host may list 512 packages.</summary>
        internal const int MaximumNeedsLines = 4;
        internal const string LanEmpty = "No sessions heard on your local network yet. Hosts on it appear here while searching.";
        internal const string LanIdle = "Search your local network to find sessions on it.";
        internal const string FavoritesEmpty = "No favorite servers yet. Select a server and add it to your favorites.";
        internal const string HistoryEmpty = "Servers you join appear here.";
        internal const string InternetEmpty = "The Internet server list needs the TopiaForge service, which this game is not set up to reach.";
        internal const string InternetInLauncher = "Public servers are listed in the TopiaForge launcher. Open one there, or paste its server link in CONNECT.";

        private readonly MultiplayerScreensContext context;
        private readonly ConnectingPresenter joins;
        private readonly Dictionary<string, Readiness> checks = new Dictionary<string, Readiness>(StringComparer.Ordinal);
        private readonly List<PendingQuery> queries = new List<PendingQuery>();
        private IReadOnlyList<InternalLanListing> listings = Array.Empty<InternalLanListing>();
        private IReadOnlyList<ServerRow> rows = Array.Empty<ServerRow>();
        private IReadOnlyList<ServerBookmark>? shownFavorites;
        private IReadOnlyList<ServerBookmark>? shownHistory;
        private BrowseList list;
        private bool searching;
        private bool dirty = true;
        private string? selectedKey;
        private string selectionText = string.Empty;

        internal BrowsePresenter(MultiplayerScreensContext context, ConnectingPresenter joins)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            this.joins = joins ?? throw new ArgumentNullException(nameof(joins));
        }

        public BrowseList List => list;

        public IContentReadinessScreen Content => context.Content;

        public bool IsSearching => searching;

        public bool CanSearch => context.CanAct && !searching;

        public IReadOnlyList<ServerRow> Rows => rows;

        public string EmptyText =>
            list == BrowseList.Internet ? (context.HasService ? InternetInLauncher : InternetEmpty)
            : list == BrowseList.Favorites ? FavoritesEmpty
            : list == BrowseList.History ? HistoryEmpty
            : searching ? LanEmpty : LanIdle;

        public string? SelectedKey => selectedKey;

        public string SelectionText => selectionText;

        public bool CanJoin => context.CanStartSession && !joins.IsBusy && SelectedReadiness() == ServerReadiness.Ready && SelectedListing() != null;

        public bool CanInstallInLauncher => SelectedReadiness() == ServerReadiness.Needs && selectedKey != null;

        public bool CanToggleFavorite => context.Settings.IsLoaded && selectedKey != null && (IsFavorite || NameOf(selectedKey) != null);

        public bool IsFavorite => selectedKey != null && context.Settings.Current.IsFavorite(selectedKey);

        /// <summary>Gets how many servers' checks are kept, for tests: never more than the local network lists.</summary>
        internal int CachedChecks => checks.Count;

        public void SelectList(BrowseList next)
        {
            if (!Enum.IsDefined(typeof(BrowseList), next) || next == list) return;
            list = next;
            selectedKey = null;
            dirty = true;
        }

        public void StartSearch()
        {
            if (!CanSearch) return;
            var control = context.Environment.Control;
            if (control == null) return;
            try
            {
                if (!context.LanBrowsing.Start(LanBrowseUse.Browse)) return;
            }
            catch (Exception)
            {
                context.Notify("This game could not listen on the local network. Close other programs that use the multiplayer port, then search again.", ScreenTone.Danger);
                return;
            }

            searching = true;
            dirty = true;
            context.Notify("Searching your local network for sessions.");
        }

        public void StopSearch()
        {
            if (!searching) return;
            searching = false;
            CancelQueries();
            try
            {
                context.LanBrowsing.Stop(LanBrowseUse.Browse);
            }
            catch (Exception)
            {
                // Stopping is best effort; the provider stops browsing with its sessions anyway.
            }

            listings = Array.Empty<InternalLanListing>();
            checks.Clear();
            dirty = true;
        }

        public void Refresh()
        {
            CancelQueries();
            checks.Clear();
            dirty = true;
        }

        public void Select(string? key)
        {
            if (string.Equals(key, selectedKey, StringComparison.Ordinal)) return;
            selectedKey = key;
            selectionText = SelectionFor(key);
        }

        public void Join()
        {
            if (!CanJoin) return;
            var listing = SelectedListing()!;
            var name = HostText.ForDisplay(listing.Name);
            var launcher = LauncherHandoff.ForLan(listing.ServerId);

            // A LAN session is on the local network by nature, so it never needs the direct prompt; the disclosure still
            // comes before this player's first join.
            context.Privacy.EnsureDisclosure(
                () => joins.Begin(new JoinRequest(listing.Target, name.Length == 0 ? "LAN session" : name, allowDirect: false, launcher: launcher)),
                () => context.Notify("Nothing was joined.", ScreenTone.Neutral));
        }

        public void InstallInLauncher()
        {
            if (!CanInstallInLauncher) return;
            var handoff = LauncherHandoff.ForLan(selectedKey);
            if (handoff == null) return;
            context.Handoffs.HandOff(handoff, "Opened the TopiaForge launcher to install this session's mods.");
        }

        public void ToggleFavorite()
        {
            if (!CanToggleFavorite) return;
            var settings = context.Settings;
            var key = selectedKey!;
            if (settings.Current.IsFavorite(key))
            {
                settings.Update(settings.Current.RemoveFavorite(key));
                context.Notify("Removed from your favorites.");
            }
            else
            {
                try
                {
                    settings.Update(settings.Current.AddFavorite(new ServerBookmark(key, NameOf(key) ?? "Server", context.Environment.UnixSeconds)));
                    context.Notify("Added to your favorites.", ScreenTone.Success);
                }
                catch (InvalidOperationException exception)
                {
                    context.Notify(exception.Message, ScreenTone.Warning);
                }
            }

            dirty = true;
        }

        /// <summary>Stops listening when the tab or the overlay closes; the player starts it again.</summary>
        internal void OnHidden() => StopSearch();
    }
}
