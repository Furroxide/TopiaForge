using System;
using System.Collections.Generic;
using System.Globalization;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>BROWSE's rows and the readiness checks behind its mods column.</summary>
    internal sealed partial class BrowsePresenter
    {
        /// <summary>Main thread, every frame the tab shows: follows the listings, runs the checks and rebuilds rows on change.</summary>
        internal void Tick()
        {
            var control = context.Environment.Control;
            if (searching && (control == null || !context.Status.IsLiveAvailable)) StopSearch();
            if (searching && control != null)
            {
                IReadOnlyList<InternalLanListing> heard;
                try
                {
                    heard = control.LanSessions;
                }
                catch (Exception)
                {
                    heard = Array.Empty<InternalLanListing>();
                }

                if (!ReferenceEquals(heard, listings))
                {
                    listings = heard;
                    ForgetUnheard();
                    dirty = true;
                }

                RunChecks(control);
            }

            var settings = context.Settings.Current;
            if (!ReferenceEquals(settings.Favorites, shownFavorites) || !ReferenceEquals(settings.History, shownHistory)) dirty = true;
            if (!dirty) return;
            dirty = false;
            shownFavorites = settings.Favorites;
            shownHistory = settings.History;
            rows = BuildRows(settings);
            selectionText = SelectionFor(selectedKey);
        }

        private void RunChecks(IInternalMultiplayerControl control)
        {
            for (var index = queries.Count - 1; index >= 0; index--)
            {
                var pending = queries[index];
                bool completed;
                try
                {
                    completed = pending.Query.IsCompleted;
                }
                catch (Exception)
                {
                    completed = true;
                }

                if (!completed) continue;
                checks[pending.ServerId] = Interpret(pending.Query);
                pending.Query.Dispose();
                queries.RemoveAt(index);
                dirty = true;
            }

            // Checks wait for this game's own mods: until they are identified there is nothing to compare.
            if (!context.Content.IsReady || queries.Count >= MaximumQueries) return;
            for (var index = 0; index < listings.Count; index++)
            {
                var listing = listings[index];
                if (queries.Count >= MaximumQueries) break;
                if (!listing.HasInfo || listing.Incompatible || checks.ContainsKey(listing.ServerId) || IsQuerying(listing.ServerId)) continue;
                IInternalServerQuery query;
                try
                {
                    query = control.Query(listing.Target, allowDirect: false);
                }
                catch (Exception)
                {
                    checks[listing.ServerId] = new Readiness(ServerReadiness.Unknown, "Unknown", Array.Empty<string>());
                    continue;
                }

                queries.Add(new PendingQuery(listing.ServerId, query));
                dirty = true;
            }
        }

        private static Readiness Interpret(IInternalServerQuery query)
        {
            InternalLiveFailure failure;
            IReadOnlyList<string> problems;
            try
            {
                failure = query.Failure;
                problems = query.ContentProblems;
            }
            catch (Exception)
            {
                return new Readiness(ServerReadiness.Unknown, "Unknown", Array.Empty<string>());
            }

            switch (failure)
            {
                case InternalLiveFailure.None:
                    return new Readiness(ServerReadiness.Ready, "Ready", Array.Empty<string>());
                case InternalLiveFailure.ContentMismatch:
                    // A host may list 512 packages this game lacks: the selection names a few and counts the rest.
                    var lines = new List<string>(Math.Min(problems.Count, MaximumNeedsLines) + 1);
                    for (var index = 0; index < problems.Count && index < MaximumNeedsLines; index++) lines.Add(HostText.ForOwnLine(problems[index]));
                    if (problems.Count > MaximumNeedsLines) lines.Add("And " + (problems.Count - MaximumNeedsLines).ToString(CultureInfo.InvariantCulture) + " more.");
                    return new Readiness(ServerReadiness.Needs, "Needs " + Math.Max(problems.Count, 1).ToString(CultureInfo.InvariantCulture), lines.AsReadOnly());
                case InternalLiveFailure.WrongGameBuild:
                case InternalLiveFailure.ProtocolMismatch:
                    return new Readiness(ServerReadiness.Incompatible, "Incompatible", new[] { LiveFailureMessages.ForJoin(failure, InternalAdmissionCode.Other).Message });
                default:
                    return new Readiness(ServerReadiness.Unknown, "Unknown", new[] { LiveFailureMessages.ForJoin(failure, InternalAdmissionCode.Other).Message });
            }
        }

        private IReadOnlyList<ServerRow> BuildRows(MultiplayerSettings settings)
        {
            var built = new List<ServerRow>();
            switch (list)
            {
                case BrowseList.Lan:
                    foreach (var listing in listings) built.Add(RowFor(listing));
                    break;
                case BrowseList.Favorites:
                    foreach (var bookmark in settings.Favorites) built.Add(RowFor(bookmark));
                    break;
                case BrowseList.History:
                    foreach (var bookmark in settings.History) built.Add(RowFor(bookmark));
                    break;
            }

            return built.AsReadOnly();
        }

        private ServerRow RowFor(InternalLanListing listing)
        {
            var name = listing.HasInfo ? HostText.ForDisplay(listing.Name) : "Waiting for details";
            var players = listing.HasInfo ? listing.Players.ToString(CultureInfo.InvariantCulture) + "/" + listing.MaxPlayers.ToString(CultureInfo.InvariantCulture) : string.Empty;
            var readiness = ReadinessOf(listing);
            return new ServerRow(listing.ServerId, listing.PasswordRequired, name, HostText.ForDisplay(listing.Gamemode), HostText.ForDisplay(listing.World),
                players, listing.Players, listing.PingMilliseconds, readiness.State, readiness.Text);
        }

        private ServerRow RowFor(ServerBookmark bookmark)
        {
            var heard = Find(bookmark.ServerId);
            if (heard != null && heard.HasInfo) return RowFor(heard);

            return new ServerRow(bookmark.ServerId, false, HostText.ForDisplay(bookmark.Name), string.Empty, string.Empty, string.Empty, 0, null,
                ServerReadiness.Unknown, searching ? "Not on your network" : "Not searched");
        }

        private Readiness ReadinessOf(InternalLanListing listing)
        {
            if (listing.Incompatible) return new Readiness(ServerReadiness.Incompatible, "Incompatible", new[] { "The host runs a TopiaForge multiplayer version this game cannot join." });
            if (checks.TryGetValue(listing.ServerId, out var known)) return known;
            if (!listing.HasInfo) return Readiness.Waiting;
            return IsQuerying(listing.ServerId) ? Readiness.Checking : context.Content.IsReady ? Readiness.Waiting : Readiness.WaitingForMods;
        }

        private InternalLanListing? SelectedListing() => selectedKey == null ? null : Find(selectedKey);

        private ServerReadiness SelectedReadiness()
        {
            var listing = SelectedListing();
            return listing == null ? ServerReadiness.Unknown : ReadinessOf(listing).State;
        }

        private InternalLanListing? Find(string serverId)
        {
            for (var index = 0; index < listings.Count; index++)
            {
                if (string.Equals(listings[index].ServerId, serverId, StringComparison.Ordinal)) return listings[index];
            }

            return null;
        }

        private string? NameOf(string serverId)
        {
            var heard = Find(serverId);
            if (heard != null && heard.HasInfo) return heard.Name;
            foreach (var bookmark in context.Settings.Current.Favorites)
            {
                if (bookmark.ServerId == serverId) return bookmark.Name;
            }

            foreach (var bookmark in context.Settings.Current.History)
            {
                if (bookmark.ServerId == serverId) return bookmark.Name;
            }

            return null;
        }

        private string SelectionFor(string? key)
        {
            if (key == null) return string.Empty;
            var listing = Find(key);
            if (listing == null)
            {
                return list == BrowseList.Lan ? string.Empty
                    : context.HasService ? "Not on your local network right now. A public server can be joined from the TopiaForge launcher."
                    : "Not on your local network right now. Servers beyond it need the TopiaForge service, which this game is not set up to reach.";
            }

            var readiness = ReadinessOf(listing);
            switch (readiness.State)
            {
                case ServerReadiness.Needs:
                    return "This game needs: " + string.Join(" ", readiness.Problems) + " The TopiaForge launcher can install the host's exact mods.";
                case ServerReadiness.Incompatible:
                case ServerReadiness.Unknown:
                    return readiness.Problems.Count == 0 ? string.Empty : readiness.Problems[0];
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Forgets the checks of servers the local network no longer lists. A host can announce a new server id as each
        /// old one falls silent, so without this the cache would grow for as long as the player browses; with it, it holds
        /// at most what the provider lists.
        /// </summary>
        private void ForgetUnheard()
        {
            if (checks.Count == 0) return;
            List<string>? gone = null;
            foreach (var serverId in checks.Keys)
            {
                if (Find(serverId) == null) (gone ??= new List<string>()).Add(serverId);
            }

            if (gone == null) return;
            foreach (var serverId in gone) checks.Remove(serverId);
        }

        private bool IsQuerying(string serverId)
        {
            foreach (var pending in queries)
            {
                if (string.Equals(pending.ServerId, serverId, StringComparison.Ordinal)) return true;
            }

            return false;
        }

        private void CancelQueries()
        {
            foreach (var pending in queries)
            {
                try
                {
                    pending.Query.Dispose();
                }
                catch (Exception)
                {
                    // A query that cannot stop has nothing left to say.
                }
            }

            queries.Clear();
        }

        private sealed class Readiness
        {
            internal static readonly Readiness Waiting = new Readiness(ServerReadiness.Unknown, "Waiting", Array.Empty<string>());
            internal static readonly Readiness WaitingForMods = new Readiness(ServerReadiness.Unknown, "Waiting for your mods", Array.Empty<string>());
            internal static readonly Readiness Checking = new Readiness(ServerReadiness.Checking, "Checking", Array.Empty<string>());

            internal Readiness(ServerReadiness state, string text, IReadOnlyList<string> problems)
            {
                State = state;
                Text = text;
                Problems = problems;
            }

            internal ServerReadiness State { get; }

            internal string Text { get; }

            internal IReadOnlyList<string> Problems { get; }
        }

        private sealed class PendingQuery
        {
            internal PendingQuery(string serverId, IInternalServerQuery query)
            {
                ServerId = serverId;
                Query = query;
            }

            internal string ServerId { get; }

            internal IInternalServerQuery Query { get; }
        }
    }
}
