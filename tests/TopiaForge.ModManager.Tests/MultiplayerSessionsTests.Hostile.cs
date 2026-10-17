using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>What a hostile host can put on this player's screens: bounded, and never an address it chose.</summary>
    internal static partial class MultiplayerSessionsTests
    {
        /// <summary>
        /// A host's descriptor may list 512 packages this game lacks, and a host on the local network may announce a new
        /// server id as each old one falls silent. BROWSE names a few problems and counts the rest, and keeps checks only
        /// for the servers the network lists now.
        /// </summary>
        private static void BrowseBoundsWhatHostsList()
        {
            var rig = new SessionsRig();
            rig.Control.Lan = new[] { Listing('2', "Strict game") };
            rig.Presenter.Browse.StartSearch();
            rig.Frame();
            var problems = Enumerable.Range(0, 512)
                .Select(index => "io.example.package" + index.ToString("D3", CultureInfo.InvariantCulture) + new string('x', 40) + " 1.0.0-" + new string('y', 50) + " is missing.")
                .ToArray();
            rig.Control.Queries[0].Complete(InternalLiveFailure.ContentMismatch, Server('2', "Strict game"), problems);
            rig.Frame(2);
            var browse = rig.Presenter.Browse;
            browse.Select(new string('2', 64));
            That(browse.Rows[0].ReadinessText == "Needs 512", "the mods column still counts every problem");
            That(browse.SelectionText.Length < 1200 && browse.SelectionText.Contains(problems[0], StringComparison.Ordinal) &&
                 browse.SelectionText.Contains("And " + (512 - BrowsePresenter.MaximumNeedsLines) + " more.", StringComparison.Ordinal) &&
                 !browse.SelectionText.Contains(problems[BrowsePresenter.MaximumNeedsLines], StringComparison.Ordinal),
                "the selection names the first few and counts the rest (" + browse.SelectionText.Length + " characters)");

            var churn = new SessionsRig();
            churn.Presenter.Browse.StartSearch();
            churn.Frame();
            for (var round = 0; round < 300; round++)
            {
                var id = round.ToString("x64", CultureInfo.InvariantCulture);
                churn.Control.Lan = new[]
                {
                    new InternalLanListing(new ScriptedJoinTarget(InternalJoinTargetKind.Lan, "LAN session", id), id) { HasInfo = true, Name = "churn", MaxPlayers = 8 }
                };
                churn.Frame();
                foreach (var query in churn.Control.Queries.Where(query => !query.IsCompleted)) query.Complete(InternalLiveFailure.ContentMismatch, null, problems);
                churn.Frame();
            }

            var checks = ((BrowsePresenter)churn.Presenter.Browse).CachedChecks;
            That(checks <= 1, "a host announcing a new server id each time keeps one check, not one per id (" + checks + ")");
            That(churn.Control.Queries.Count == 300, "and every server the network listed was checked once");
            churn.Presenter.Browse.StopSearch();
            That(((BrowsePresenter)churn.Presenter.Browse).CachedChecks == 0, "stopping the search forgets the checks");
        }

        /// <summary>
        /// This game's own check names the host's packages by the ids and versions the host gave, and those may be four
        /// dotted numbers: an address the host chose, this player's own perhaps. Every screen that shows those lines drops
        /// it, as copied details do, and keeps the real mod ids readable.
        /// </summary>
        private static void OwnLinesLoseHostAddresses()
        {
            var local = new[] { new InternalContentDescriptor("io.example.sandbox", "1.0.0", new string('a', 64), InternalContentMode.ImplicitShared) };
            var host = new[]
            {
                new InternalHostPackage("203.0.113.7", "1.0.0", new string('b', 64), InternalHostPackageRole.Implicit),
                new InternalHostPackage("io.example.zombies", "0.2.0-198.51.100.4", new string('c', 64), InternalHostPackageRole.Implicit),
                new InternalHostPackage("io.example.lobby", "1.0.0", new string('d', 64), InternalHostPackageRole.Implicit)
            };
            var verdict = MultiplayerContentAdmission.CheckHost(local, host);
            That(verdict.Problems.Any(Shows), "the check's own words name the host's address-shaped id and version");

            var browse = new SessionsRig();
            browse.Control.Lan = new[] { Listing('2', "Strict game") };
            browse.Presenter.Browse.StartSearch();
            browse.Frame();
            browse.Control.Queries[0].Complete(InternalLiveFailure.ContentMismatch, Server('2', "Strict game"), verdict.Problems);
            browse.Frame(2);
            browse.Presenter.Browse.Select(new string('2', 64));
            var shown = new List<string> { browse.Presenter.Browse.SelectionText };

            var connect = new SessionsRig();
            connect.Presenter.Connect.SetAddress("192.0.2.4:27250");
            connect.Presenter.Connect.JoinAddress();
            connect.Frame();
            connect.Control.Queries[0].Complete(InternalLiveFailure.ContentMismatch, Server('f', "Strict host"), verdict.Problems);
            connect.Frame();
            shown.Add(connect.Presenter.Connect.ResultText);

            var joined = BrowsingRig(ready: true);
            joined.Presenter.Browse.Select(new string('1', 64));
            joined.Presenter.Browse.Join();
            joined.Frame();
            joined.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.ContentMismatch);
            joined.Control.Status.ContentProblems = verdict.Problems;
            joined.Control.Status.ContentProblemsFromHost = false;
            joined.Frame();
            shown.AddRange(joined.Presenter.Connecting.FailureLines);
            joined.Presenter.Connecting.CopyDetails();
            shown.Add(joined.Environment.Copied.Single());

            foreach (var text in shown)
            {
                That(!Shows(text), "no screen shows an address the host put in a package id or version (" + text + ")");
            }

            That(shown.Take(2).All(text => text.Contains("io.example.lobby 1.0.0 is missing.", StringComparison.Ordinal)) &&
                 joined.Presenter.Connecting.FailureLines.Contains("io.example.lobby 1.0.0 is missing."),
                "and the real mod ids stay readable");
        }
    }
}
