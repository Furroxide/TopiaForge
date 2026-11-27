using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>BROWSE, the address sweep over every screen, and the settled frame's allocations.</summary>
    internal static partial class MultiplayerSessionsTests
    {
        private static void BrowseListsChecksAndFavorites()
        {
            var rig = new SessionsRig();
            var browse = rig.Presenter.Browse;
            That(browse.Rows.Count == 0 && browse.EmptyText == BrowsePresenter.LanIdle && !browse.IsSearching, "the LAN list starts empty until the player searches");
            browse.SelectList(BrowseList.Internet);
            rig.Frame();
            That(browse.Rows.Count == 0 && browse.EmptyText == BrowsePresenter.InternetEmpty, "the Internet list says the service is not configured");
            browse.SelectList(BrowseList.Lan);

            rig.Environment.ContentReadiness = new InternalContentReadiness(InternalContentState.Pending, 0, 2, 0, 0);
            rig.Control.Lan = new[] { Listing('1', "Ready game", players: 3), Listing('2', "Strict game", password: true), Listing('3', "Old game", incompatible: true) };
            browse.StartSearch();
            rig.Frame(3);
            That(rig.Control.Calls.Contains("StartBrowsing") && browse.Rows.Count == 3 && !rig.Control.Calls.Any(call => call.StartsWith("Query", StringComparison.Ordinal)),
                "searching lists what the local network hears, and checks nothing while this game's mods are being identified");
            That(browse.Rows[0].ReadinessText == "Waiting for your mods" && browse.Rows[2].Readiness == ServerReadiness.Incompatible, "the mods column says why it waits");
            rig.Environment.ContentReadiness = InternalContentReadiness.Ready;
            rig.Frame();
            That(rig.Control.Queries.Count == BrowsePresenter.MaximumQueries && rig.Control.Queries.All(query => !query.AllowDirect),
                "then at most two checks run at once, over the local network only");
            rig.Control.Queries[0].Complete(InternalLiveFailure.None, Server('1', "Ready game"));
            rig.Control.Queries[1].Complete(InternalLiveFailure.ContentMismatch, Server('2', "Strict game"), new[] { "Zombies 0.2.0 is missing.", "Lobby 1.0.0 is missing." });
            rig.Frame(2);
            var rows = browse.Rows;
            That(rows[0].Readiness == ServerReadiness.Ready && rows[0].ReadinessText == "Ready" && rows[0].Players == "3/8" && rows[0].PingMilliseconds == 20 && !rows[0].Locked,
                "a server that fits reads Ready, with its players and ping");
            That(rows[1].Readiness == ServerReadiness.Needs && rows[1].ReadinessText == "Needs 2" && rows[1].Locked && rows[2].ReadinessText == "Incompatible",
                "one that needs mods reads Needs 2 and shows its lock; another version reads Incompatible");
            That(rig.Control.Queries[0].Disposed && rig.Control.Queries[1].Disposed && rig.Control.Queries.Count == 2, "finished checks are released, and the incompatible one is never checked");

            browse.Select(new string('2', 64));
            That(!browse.CanJoin && browse.CanInstallInLauncher && browse.SelectionText.Contains("Zombies 0.2.0 is missing.", StringComparison.Ordinal),
                "a server that needs mods cannot be joined here, says what it needs, and hands over to the launcher");
            browse.InstallInLauncher();
            That(rig.Environment.Opened.Last() == "topiaforge://join/server/" + new string('2', 64), "with a server link that names no address");
            browse.Select(new string('1', 64));
            That(browse.CanJoin && !browse.CanInstallInLauncher, "a Ready server can be joined");

            browse.ToggleFavorite();
            rig.Frame();
            That(browse.IsFavorite && rig.Store.Stored!.Favorites.Single().ServerId == new string('1', 64) && rig.Store.Stored.Favorites[0].Name == "Ready game",
                "a favorite is kept by server id and name, never an address");
            browse.SelectList(BrowseList.Favorites);
            rig.Frame();
            That(browse.Rows.Single().ReadinessText == "Ready" && browse.SelectedKey == null, "the favorite shows the live session while the local network hears it");
            browse.Select(new string('1', 64));
            browse.ToggleFavorite();
            rig.Frame();
            That(browse.Rows.Count == 0 && browse.EmptyText == BrowsePresenter.FavoritesEmpty && rig.Store.Stored!.Favorites.Count == 0, "and can be removed");
            browse.SelectList(BrowseList.History);
            rig.Frame();
            That(browse.Rows.Count == 0 && browse.EmptyText == BrowsePresenter.HistoryEmpty, "history is empty until a join succeeds");

            browse.SelectList(BrowseList.Lan);
            browse.Refresh();
            rig.Frame();
            That(rig.Control.Queries.Count == 4, "refreshing checks the servers again");
            rig.Presenter.OnHidden();
            rig.Frame();
            That(rig.Control.Calls.Contains("StopBrowsing") && !browse.IsSearching && rig.Control.Queries.All(query => query.Disposed),
                "closing the tab stops listening and drops the running checks");
            var calls = rig.Control.Calls.Count;
            rig.Frame(20);
            That(rig.Control.Calls.Skip(calls).All(call => call == "GetStatus"), "and nothing is checked after that");
        }

        /// <summary>Every host-authored or player-chosen text carries an address here; no screen may show one.</summary>
        private static void NoScreenShowsAnAddress()
        {
            var rig = new SessionsRig();
            rig.Control.Lan = new[] { Listing('1', "join 203.0.113.7:27250"), Listing('2', "play.example.org") };
            rig.Control.Lan[0].Gamemode = "gm at 198.51.100.4";
            rig.Control.Lan[1].World = "[2001:db8::5]:27250";
            rig.Presenter.Browse.StartSearch();
            rig.Frame(2);
            foreach (var row in rig.Presenter.Browse.Rows)
            {
                That(!Shows(row.Name) && !Shows(row.Gamemode) && !Shows(row.World), "a LAN row's host-authored texts are redacted (" + row.Name + ")");
            }

            rig.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            rig.Control.Status.Server = Server('9', "server at 192.0.2.9");
            rig.Environment.SessionSnapshot = SessionsRig.HostSnapshot(MultiplayerSessionState.Ready, "s-sweep",
                SessionsRig.Player("p1", "203.0.113.7:27250", "key-a"), SessionsRig.Player("p2", "play.example.org", "key-b"));
            rig.Frame();
            var texts = new List<string> { rig.Presenter.StatusText, rig.Presenter.Session.Title, rig.Presenter.Session.Subtitle };
            texts.AddRange(rig.Presenter.Session.Players.Select(row => row.Name));
            rig.Presenter.Session.Select("p1");
            rig.Presenter.Session.Kick();
            rig.Frame();
            texts.Add(rig.Prompt!.Title);
            texts.AddRange(rig.Prompt.Paragraphs);
            rig.Answer(ScreenPromptKind.Destructive, true);
            texts.AddRange(rig.Notices.Select(notice => notice.Text));
            foreach (var text in texts)
            {
                That(!Shows(text), "no screen, prompt or toast shows an address (" + text + ")");
            }
        }

        private static void SettledFramesAllocateNothing()
        {
            Settled("solo, the tab open", new SessionsRig());
            var hosting = HostingWithAda();
            hosting.Presenter.SelectTab(MultiplayerSubTab.Session);
            Settled("hosting with a player", hosting);
            var joined = new SessionsRig();
            joined.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            joined.Environment.SessionSnapshot = SessionsRig.ClientSnapshot(MultiplayerSessionState.Ready, "s-joined", "p1", SessionsRig.Player("p2", "Cy", "key-c"));
            Settled("joined, in session", joined);
            var browsing = BrowsingRig(ready: true);
            Settled("browsing a checked LAN", browsing);
            var hidden = new SessionsRig();
            hidden.Presenter.OnHidden();
            Settled("the overlay closed", hidden);

            // A question waits while what it asks about holds, and the screens check that every frame.
            var ending = HostingWithAda();
            ending.Presenter.Session.EndForEveryone();
            Settled("a question about the session waiting", ending);
            var opening = new SessionsRig(acknowledged: false);
            opening.Environment.Phase = TopiaForge.ModManager.Core.SessionPhase.Running;
            opening.Presenter.Host.Host();
            Settled("a question about the running game waiting", opening);
            That(ending.Prompt != null && opening.Prompt?.Kind == ScreenPromptKind.Disclosure, "and both questions still wait");
        }

        /// <summary>
        /// Runs settled frames and reads every value a view binds each frame, as the kit views do, and requires that nothing
        /// is allocated across ten thousand of them.
        /// </summary>
        private static void Settled(string state, SessionsRig rig)
        {
            // The game always runs some mods, and the realm rules look through them for one that refuses multiplayer.
            rig.Environment.Load("io.example.sandbox");
            rig.Environment.Load("io.example.cosmetics", "client-local");
            rig.Frame(60);
            var presenter = rig.Presenter;
            void Frame()
            {
                rig.Work.RunAll();
                presenter.Tick(0.016f);
                while (presenter.TryTakeNotice(out _))
                {
                }

                _ = presenter.StatusText;
                _ = presenter.HideMyIp;
                _ = presenter.SettingsNote;
                _ = presenter.VoiceNote;
                _ = presenter.PendingPrompt;
                var browse = presenter.Browse;
                _ = browse.Rows;
                _ = browse.EmptyText;
                _ = browse.CanJoin;
                _ = browse.CanInstallInLauncher;
                _ = browse.IsFavorite;
                _ = browse.Content.Text;
                var host = presenter.Host;
                _ = host.ModeText;
                _ = host.CanHost;
                _ = host.HostLabel;
                _ = host.CurrentGameText;
                _ = host.VisibilityNote;
                _ = host.ProgressText;
                var session = presenter.Session;
                _ = session.Title;
                _ = session.InviteText;
                _ = session.PlayersVisible;
                _ = session.PlayersNote;
                _ = session.Players;
                _ = session.CanKick;
                var connecting = presenter.Connecting;
                _ = connecting.IsVisible;
                _ = connecting.Steps;
                for (var step = 0; step < ConnectingSteps.Count; step++) _ = connecting.DetailOf(step);
                _ = presenter.Connect.CanJoinAddress;
                _ = presenter.Connect.ResultText;
            }

            for (var warm = 0; warm < 100; warm++) Frame();
            var reads = presenter.Context.Status.Reads;

            // Measured as the other zero-allocation checks are, outside what the runtime does as code first runs.
            var allocated = Seam.Allocated(Frame, 10_000);
            That(allocated == 0, "a settled frame allocates nothing while " + state + " (" + allocated + " bytes over 10,000 frames)");
            That(presenter.Context.Status.Reads == reads, "and reads the control's status not once (" + state + ")");
        }
    }
}
