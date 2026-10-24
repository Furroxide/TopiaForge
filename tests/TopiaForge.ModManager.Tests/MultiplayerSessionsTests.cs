using System;
using System.Linq;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The in-game sessions interface's presenters, over a recording control: what each screen shows in each state, every
    /// transition, the approved prompts and their versioning, the realm rules, and that nothing reaches the network until the
    /// player acts.
    /// </summary>
    internal static partial class MultiplayerSessionsTests
    {
        internal static void Run(string root)
        {
            NothingReachesTheNetworkUntilThePlayerActs();
            UnavailableStatesSayWhy();
            DisplayNamesAreBounded();
            DisclosureShowsOnceForEachVersion();
            HideMyIpIsOnUntilThePlayerTurnsItOff();
            SettingsAreWrittenOffTheMainThread();
            HostSettingsAreBounded();
            HostingAtTheMenuStartsTheGameThenOpensIt();
            HostOffersOnlyGamesTheMenuCanStart();
            OpeningARunningGamePromotesItInPlace();
            PublicAndInviteHostingNeedTheService();
            AHostedSessionSaysHowTheServiceListsIt();
            HostingFailuresAreWordedAndEndingConfirms();
            ContentReadinessGatesHostingAndJoining();
            DirectPromptCancellationContactsNothing();
            JoiningWhileInAGameAsksToLeaveFirst();
            AJoinDuringAMainMenuLoadWaits(root);
            LeavingIsNeverRefused();
            ConnectingWalksThePhases();
            EveryFailureHasAClearMessage();
            FailuresStopAtTheirStep();
            CancelWorksThroughout();
            RetryTrustAndTheLauncher();
            AnUncheckedServerMayBeTrustedOrTriedAgain();
            AJoinThatNeedsIPv6SaysThisComputerHasNone();
            CopiedDetailsNeverHoldAnAddress();
            LauncherLinksCarryNoAddressUnlessTyped();
            PlayersShowOnceTheHostListsThem();
            ModerationChecksThePlayerKeyBeforeActing();
            LeavingAndEndingConfirm();
            ASessionThatEndsWhilePlayingIsToasted();
            BrowseListsChecksAndFavorites();
            BrowseBoundsWhatHostsList();
            OwnLinesLoseHostAddresses();
            NoScreenShowsAnAddress();
            InvitesAndListedServersNeedTheService();
            AnUnavailableRelayOffersTheDirectPrompt();
            TheServicesFailuresAreHandledAsTheirCodesSay();
            AHostSeesItsInvite();
            PromptsCloseWhenTheirGameChanges();
            PromptsShowOneAtATimeAndCloseWhenWithdrawn();
            AThrowingProviderEndsTheAttemptOnce();
            HostNamesWorldsAsPlayersKnowThem();
            RetryAsksForARefusedPasswordAgain();
            AHostCopiesItsInviteCode();
            AFailingClipboardStaysInTheGamesWords();
            EveryFailureSaysWhatToDoNext();
            APastedInviteLinkJoins();
            HostOffersTheGameLastHostedFirst();
            TheSettingsFileReadsAcrossVersions(root);
            LaunchedSessionsStart();
            LiveJoinsAndTheLauncher(root);
            LaunchesEndOnTheirOwnRecords();
            SettledFramesAllocateNothing();
            VoiceSaysWhenTheSessionOffersNone();
            Console.WriteLine("MultiplayerSessionsTests passed.");
        }

        private static void NothingReachesTheNetworkUntilThePlayerActs()
        {
            var rig = new SessionsRig();
            rig.Control.Lan = new[] { Listing('1', "LAN game") };
            foreach (MultiplayerSubTab tab in Enum.GetValues(typeof(MultiplayerSubTab)))
            {
                rig.Presenter.SelectTab(tab);
                rig.Frame(30);
            }

            rig.Presenter.Browse.SelectList(BrowseList.Internet);
            rig.Presenter.Browse.SelectList(BrowseList.Favorites);
            rig.Presenter.OnHidden();
            rig.Presenter.OnShown();
            rig.Frame(100);
            That(rig.Control.NetworkCalls == 0, "opening every tab and list contacts nothing (" + string.Join(", ", rig.Control.Calls.Distinct()) + ")");
            That(rig.Prompt == null, "and asks nothing");
        }

        private static void UnavailableStatesSayWhy()
        {
            var missing = new SessionsRig(withControl: false);
            That(!missing.Presenter.IsAvailable && missing.Presenter.AvailabilityText == MultiplayerSessionsPresenter.NotInstalled,
                "without the provider the tab says multiplayer is not installed");
            var off = new SessionsRig(liveAvailable: false);
            That(!off.Presenter.IsAvailable && off.Presenter.AvailabilityText == MultiplayerSessionsPresenter.SwitchedOff,
                "with live sessions switched off it says so");
            off.Presenter.Browse.StartSearch();
            That(!off.Presenter.Browse.CanSearch && off.Control.NetworkCalls == 0, "and nothing can be searched");
            var on = new SessionsRig();
            That(on.Presenter.IsAvailable && on.Presenter.AvailabilityText.Length == 0 && on.Presenter.StatusText == "Playing on your own.",
                "with them on, the tab shows the session's state");
        }

        private static void DisplayNamesAreBounded()
        {
            var rig = new SessionsRig();
            foreach (var bad in new[] { "A", "", new string('n', 25), "Sam\u0007", " Sam", "Sam " })
            {
                rig.Presenter.SetDisplayName(bad);
                That(rig.Presenter.DisplayNameError == MultiplayerSessionsPresenter.NameRule && rig.Environment.DisplayName == "Player",
                    "'" + bad.Length + " characters' is refused as a name, and the runtime keeps its own");
            }

            foreach (var good in new[] { "Bo", new string('n', 24), "Sam" })
            {
                rig.Presenter.SetDisplayName(good);
                That(rig.Presenter.DisplayNameError.Length == 0 && rig.Environment.DisplayName == good, good.Length + " characters is a name the runtime takes");
            }

            rig.Frame(3);
            That(rig.Launcher.Journal!.Changes[rig.Launcher.Journal.Changes.Count - 1].NewDisplayName == "Sam",
                "and the name is recorded in the launcher's journal, the one writable copy of it");
            var next = new SessionsRig(launcher: rig.Launcher);
            next.Store.Stored = rig.Store.Stored;
            var reloaded = new MultiplayerSessionsPresenter(next.Environment, next.Store, next.Work);
            reloaded.Start();
            next.Work.RunAll();
            reloaded.Tick(0.05f);
            That(reloaded.DisplayName == "Sam" && next.Environment.DisplayName == "Sam",
                "a name changed in game applies to the runtime when the game starts again, from the journal the launcher has not taken in");

            // Once the launcher took the journal in, its projection holds the name; a newer one it set in the launcher wins.
            var launcher = new MemoryLauncherChannels
            {
                Projection = new TopiaForge.ModManager.Core.MultiplayerPreferencesProjection(4, rig.Launcher.Journal.Changes[rig.Launcher.Journal.Changes.Count - 1].Sequence,
                    "Ada", 1, 0, false, true, false, new TopiaForge.ModManager.Core.MultiplayerHostingDefaults("Ada's den", 4, "lan", 27250, false), null,
                    rig.Launcher.Journal.Epoch),
                Journal = rig.Launcher.Journal
            };
            var projected = new SessionsRig(launcher: launcher);
            That(projected.Presenter.DisplayName == "Ada" && projected.Environment.DisplayName == "Ada",
                "the launcher's projection is read at start, and a change it already counts is not applied again");
        }

        private static void DisclosureShowsOnceForEachVersion()
        {
            var rig = new SessionsRig(acknowledged: false);
            ReadyHost(rig);
            rig.Environment.Phase = SessionPhase.Running;
            rig.Presenter.Host.Host();
            rig.Frame();
            That(rig.Prompt?.Kind == ScreenPromptKind.Disclosure && rig.Prompt.Title == MultiplayerPrivacyText.DisclosureTitle,
                "the first host shows the disclosure before anything else");
            rig.Answer(ScreenPromptKind.Disclosure, false);
            That(rig.Control.NetworkCalls == 0 && rig.Environment.PendingLaunch == null && rig.Store.Stored!.AcknowledgedDisclosure == 0,
                "closing it starts nothing and stores nothing");
            rig.Presenter.Host.Host();
            rig.Frame();
            That(rig.Prompt?.Kind == ScreenPromptKind.Disclosure, "so it shows again");
            rig.Answer(ScreenPromptKind.Disclosure, true);
            rig.Frame();
            That(rig.Store.Stored!.AcknowledgedDisclosure == MultiplayerPrivacyText.DisclosureVersion && rig.Control.Calls.Contains("StartHosting"),
                "acknowledging it stores its version in manager data, and hosting goes on");

            rig.Status(InternalSessionRole.Solo);
            rig.Frame();
            rig.Presenter.Connect.SetAddress("192.0.2.10:27250");
            rig.Presenter.Connect.JoinAddress();
            rig.Frame();
            That(rig.Prompt == null && rig.Control.Calls.Any(call => call.StartsWith("Query", StringComparison.Ordinal)), "the next join does not show it again");

            // A stored acknowledgement of an older version, as after the wording changes, shows it again.
            var older = new SessionsRig(acknowledged: false);
            older.Store.Stored = MultiplayerSettings.FromDocument(new MultiplayerSettingsDocument { SchemaVersionValue = 1, AcknowledgedPrivacyDisclosure = MultiplayerPrivacyText.DisclosureVersion - 1 });
            var presenter = new MultiplayerSessionsPresenter(older.Environment, older.Store, older.Work);
            presenter.Start();
            older.Work.RunAll();
            presenter.Tick(0.05f);
            That(presenter.Settings.Current.NeedsDisclosure, "an older acknowledged version needs the disclosure again");
            var newer = MultiplayerSettings.FromDocument(new MultiplayerSettingsDocument { SchemaVersionValue = 1, AcknowledgedPrivacyDisclosure = MultiplayerPrivacyText.DisclosureVersion + 1 });
            That(!newer.NeedsDisclosure && newer.AcknowledgeDisclosure().AcknowledgedDisclosure == MultiplayerPrivacyText.DisclosureVersion + 1,
                "a newer one does not, and acknowledging never lowers it");
        }

        private static void HideMyIpIsOnUntilThePlayerTurnsItOff()
        {
            That(MultiplayerSettings.Default.HideMyIp && MultiplayerSettings.FromDocument(new MultiplayerSettingsDocument()).HideMyIp,
                "Hide my IP is on by default, and when the setting is missing");
            var rig = new SessionsRig();
            rig.Presenter.SetHideMyIp(false);
            rig.Frame();
            That(rig.Prompt?.Kind == ScreenPromptKind.Disclosure && rig.Presenter.HideMyIp, "turning it off asks through the approved disclosure first");
            rig.Answer(ScreenPromptKind.Disclosure, false);
            That(rig.Presenter.HideMyIp, "closing the disclosure leaves it on");
            rig.Presenter.SetHideMyIp(false);
            rig.Answer(ScreenPromptKind.Disclosure, true);
            rig.Frame();
            That(!rig.Presenter.HideMyIp && rig.Store.Stored!.HideMyIp == false, "acknowledging it turns it off, and the setting is kept");
            rig.Presenter.SetHideMyIp(true);
            rig.Frame();
            That(rig.Presenter.HideMyIp && rig.Store.Stored!.HideMyIp && rig.Prompt == null, "turning it on needs nothing");
        }

        private static void SettingsAreWrittenOffTheMainThread()
        {
            // The name is the launcher's preference: it goes to the launcher's journal, on the worker, coalesced.
            var rig = new SessionsRig();
            var records = rig.Launcher.Records;
            rig.Presenter.SetDisplayName("Ada");
            rig.Presenter.SetDisplayName("Bo");
            That(rig.Launcher.Records == records && rig.Presenter.SettingsNote == MultiplayerSessionsPresenter.SavingSettings,
                "a change is recorded by the worker, not on this thread, and the tab says it is saving");
            rig.Frame(3);
            var changes = rig.Launcher.Journal!.Changes;
            That(changes[changes.Count - 1].NewDisplayName == "Bo" && rig.Launcher.Records <= records + 2 && rig.Presenter.SettingsNote.Length == 0,
                "the newest name is recorded, coalesced, and the note clears");

            // Hide my IP is this game's own setting: the settings file is written on the worker too.
            var saves = rig.Store.Saves;
            rig.Presenter.SetHideMyIp(false);
            rig.Answer(ScreenPromptKind.Disclosure, yes: true);
            That(rig.Store.Stored!.HideMyIp == false && rig.Store.Saves == saves + 1, "and the settings file is written by the worker");

            var failing = new SessionsRig();
            failing.Launcher.FailRecord = true;
            failing.Presenter.SetDisplayName("Cy");
            failing.Frame(3);
            That(failing.Noticed(MultiplayerPreferencesService.RecordFailedText) && failing.Presenter.DisplayName == "Cy", "a failed record is reported, and the change stays");
            failing.Store.FailSave = true;
            failing.Presenter.SetHideMyIp(false);
            failing.Answer(ScreenPromptKind.Disclosure, yes: true);
            failing.Frame(3);
            That(failing.Noticed(MultiplayerSettingsService.SaveFailedText), "a failed settings write is reported too");

            var unreadable = new SessionsRig();
            unreadable.Store.FailLoad = true;
            var presenter = new MultiplayerSessionsPresenter(unreadable.Environment, unreadable.Store, unreadable.Work);
            presenter.Start();
            unreadable.Work.RunAll();
            presenter.Tick(0.05f);
            That(presenter.TryTakeNotice(out var notice) && notice.Text == MultiplayerSettingsService.LoadFailedText, "unreadable settings are reported");
            var before = unreadable.Store.Saves;
            presenter.SetHideMyIp(false);
            presenter.PendingPrompt?.Answer(true);
            unreadable.Work.RunAll();
            presenter.Tick(0.05f);
            That(unreadable.Store.Saves == before, "and never overwritten with defaults");
        }

        internal static InternalLanListing Listing(char id, string name, int players = 2, bool password = false, bool incompatible = false) =>
            new InternalLanListing(new ScriptedJoinTarget(InternalJoinTargetKind.Lan, "LAN session", new string(id, 64)), new string(id, 64))
            {
                HasInfo = true,
                Name = name,
                Gamemode = "Sandbox",
                World = "Open Sandbox",
                Players = players,
                MaxPlayers = 8,
                PasswordRequired = password,
                Incompatible = incompatible,
                PingMilliseconds = 20
            };

        internal static InternalServerInfo Server(char id, string name) => new InternalServerInfo(new string(id, 64), name, Array.Empty<InternalHostPackage>());

        /// <summary>Puts a target game in the HOST list and refreshes it, as opening the tab does.</summary>
        private static void ReadyHost(SessionsRig rig)
        {
            rig.Environment.Targets.Add(new HostTargetChoice("Sandbox · open-sandbox", new TopiaForge.ModManager.Core.LaunchRequest("io.example.sandbox"), "Sandbox", "open-sandbox"));
            rig.Presenter.SelectTab(MultiplayerSubTab.Host);
            rig.Frame();
        }
    }
}
