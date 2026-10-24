using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.Json;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Sessions;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.SessionAssert;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// What a player would otherwise do by hand: pick a game by a world id, type a refused password back in somewhere else,
    /// copy an invite code out letter by letter, cut the code out of a shared link, pick the game they host again every
    /// time, or puzzle over a failure that does not say what to try next.
    /// </summary>
    internal static partial class MultiplayerSessionsTests
    {
        /// <summary>Words that tell the player what to do next; every failure they can meet uses one, or needs none.</summary>
        private static readonly string[] NextSteps =
        {
            "Try again", "try again", "Retry", "Check", "check", "Ask the host", "ask the host", "Choose another", "Update", "update", "Close",
            "Wait", "trust it", "launcher can", "Join again", "Host again", "by itself", "Try another", "Withdraw one", "still work"
        };

        /// <summary>Failures that leave nothing for the player to do, or whose words are the privacy gate's own.</summary>
        private static readonly InternalLiveFailure[] NothingToDo =
        {
            InternalLiveFailure.None, InternalLiveFailure.Left, InternalLiveFailure.Kicked, InternalLiveFailure.Banned, InternalLiveFailure.HostEnded,
            InternalLiveFailure.DirectNotAllowed
        };

        /// <summary>
        /// HOST lists games, and a hosted session announces its world, by the names players know: a world a package declares,
        /// and one the game discovered, such as a native level. Before, every Free Play world read as a 64-character id, and the
        /// announced world, cut to 48 characters, reached the service's public server list as half an id.
        /// </summary>
        private static void HostNamesWorldsAsPlayersKnowThem()
        {
            using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(Program.FindRepoRoot(),
                "tests", "fixtures", "gamemode-v6", "resolution", "discovery-longest-family-accepts-correct-attribution.json")));
            var root = fixture.RootElement;
            var manifest = ModManifestJson.Deserialize(root.GetProperty("profile").GetProperty("packages")[0].GetProperty("manifest").GetRawText());
            var profile = new EffectiveProfile("fixture", 1, new[] { new ResolvedPackage(manifest.Id, manifest.Version, manifest) });
            var observation = RuntimeObservation.FromEnvelopes(profile, new[] {
                LaunchTransportJson.ReadObservation(root.GetProperty("observation").GetProperty("envelopes")[0].GetRawText()) });
            var previews = LaunchTargetPreviewBuilder.Build(profile, observation);
            var discovered = previews.SelectMany(preview => preview.Choices).Select(choice => choice.WorldId)
                .Single(id => id.EndsWith(".level.nested.level", StringComparison.Ordinal));

            var names = HostTargets.WorldNames(profile, observation);
            var offered = HostTargets.AtTheMenu(previews, names);
            That(offered.Any(target => target.Label == "menu · Observed instance" && target.World == "Observed instance"),
                "a world the game discovered is offered, and announced, by the name its discovery gave it (" + string.Join(" | ", offered.Select(target => target.Label)) + ")");
            That(offered.Any(target => target.Label == "menu · open_sandbox") && offered.All(target => target.Label.IndexOf("io.github", StringComparison.Ordinal) < 0),
                "a declared world by its declared name, and no game by a world id");
            That(HostTargets.WorldName(discovered.ToUpperInvariant(), names) == "Observed instance" && HostTargets.WorldName("io.example.unnamed", names) == "io.example.unnamed" &&
                 HostTargets.WorldName(null, names).Length == 0,
                "names are found whatever the id's case, and a world with no name keeps its id");
            That(HostTargets.WorldNames(profile, null).ContainsKey(discovered) == false, "a discovered world's name comes only with its observation");

            var rig = new SessionsRig();
            rig.Environment.Targets.AddRange(offered);
            rig.Presenter.SelectTab(MultiplayerSubTab.Host);
            rig.Frame();
            var observed = rig.Presenter.Host.TargetOptions.ToList().IndexOf("menu · Observed instance");
            rig.Presenter.Host.SelectTarget(observed);
            rig.Presenter.Host.Host();
            rig.Frame();
            rig.Environment.Phase = SessionPhase.Running;
            rig.Environment.PendingLaunch!(true, "Launch target reached Running.");
            rig.Frame(3);
            That(rig.Control.LastHost?.World == "Observed instance" && rig.Control.LastHost.Gamemode == "menu",
                "the session announces the world by its name to the players browsing for one (" + rig.Control.LastHost?.World + ")");
        }

        /// <summary>
        /// A password the host turned away is asked for again on the overlay, never sent again: before, RETRY sent the same
        /// wrong password, which could not succeed and counted toward the host's limit on guesses, and typing it again meant
        /// going back to CONNECT.
        /// </summary>
        private static void RetryAsksForARefusedPasswordAgain()
        {
            foreach (var code in new[] { InternalAdmissionCode.PasswordWrong, InternalAdmissionCode.TooManyAttempts, InternalAdmissionCode.PasswordRequired })
            {
                var rig = TypedJoinWithPassword("open sesame");
                rig.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Rejected, code: code);
                rig.Frame();
                var overlay = rig.Presenter.Connecting;
                That(overlay.HasFailed && overlay.CanRetry, code + " offers RETRY");
                overlay.Retry();
                rig.Frame();
                That(JoinCalls(rig) == 2 && rig.Control.LastJoin!.Password == null, code + ": RETRY joins again without the refused password");
                rig.Status(InternalSessionRole.Solo, InternalJoinPhase.AwaitingPassword);
                rig.Frame();
                That(overlay.NeedsPassword, code + ": and the session's request for a password shows on the overlay, to type it there");
            }

            var full = TypedJoinWithPassword("open sesame");
            full.Status(InternalSessionRole.Solo, InternalJoinPhase.Ended, InternalLiveFailure.Rejected, code: InternalAdmissionCode.Full);
            full.Frame();
            full.Presenter.Connecting.Retry();
            full.Frame();
            That(full.Control.LastJoin!.Password == "open sesame", "a refusal that is not about the password keeps it for the retry");
            That(LiveFailureMessages.ForJoin(InternalLiveFailure.Rejected, InternalAdmissionCode.PasswordWrong).Message.IndexOf("Retry to enter it again", StringComparison.Ordinal) > 0,
                "and the wrong password's words say how to try again");
        }

        /// <summary>A typed host joined with a typed password, as CONNECT starts it; the check found the server.</summary>
        private static SessionsRig TypedJoinWithPassword(string password)
        {
            var rig = new SessionsRig();
            rig.Presenter.Connect.SetAddress("192.0.2.4:27250");
            rig.Presenter.Connect.SetPassword(password);
            rig.Presenter.Connect.JoinAddress();
            rig.Frame();
            rig.Control.Queries[0].Complete(InternalLiveFailure.None, Server('2', "Typed"));
            rig.Frame();
            That(rig.Control.LastJoin?.Password == password, "the typed password goes with the join");
            return rig;
        }

        /// <summary>
        /// A host shares its invite code by pasting it: SESSION copies it to the clipboard on the host's click. The code goes
        /// nowhere else, so the toast that says it was copied does not repeat it.
        /// </summary>
        private static void AHostCopiesItsInviteCode()
        {
            var rig = new SessionsRig(service: true);
            That(!rig.Presenter.Session.CanCopyInvite, "solo play has no invite to copy");
            rig.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            rig.Control.Status.Internet = new InternalInternetStatus { ServiceConfigured = true, ListingPhase = InternalListingPhase.Registering };
            rig.Frame();
            rig.Presenter.Session.CopyInvite();
            That(!rig.Presenter.Session.CanCopyInvite && rig.Environment.Copied.Count == 0, "nor does a session the service has not registered yet");

            rig.Status(InternalSessionRole.ListenHost, host: InternalHostPhase.Hosting);
            rig.Control.Status.Internet = new InternalInternetStatus
            {
                ServiceConfigured = true,
                ListingPhase = InternalListingPhase.Online,
                Invite = new InternalInvite("ABCDE12345", Invite, "topiaforge://join/ABCDE12345", 1_800_000_600)
            };
            rig.Frame();
            That(rig.Presenter.Session.CanCopyInvite, "a host with an invite can copy its code");
            rig.Presenter.Session.CopyInvite();
            rig.Frame();
            That(rig.Environment.Copied.SequenceEqual(new[] { Invite }), "copying puts the code on the clipboard as players type it, and nothing else");
            That(rig.Noticed(SessionPresenter.InviteCopied) &&
                 rig.Notices.All(notice => notice.Text.IndexOf("ABCDE", StringComparison.Ordinal) < 0 && notice.Text.IndexOf("12345", StringComparison.Ordinal) < 0),
                "and the toast says so without the code");

            // A clipboard that fails is said in this game's words: nothing it throws escapes the click, where the kit would log it.
            rig.Environment.CopyFailure = new InvalidOperationException("the clipboard refused " + Invite);
            rig.Presenter.Session.CopyInvite();
            rig.Frame();
            That(rig.Noticed(SessionPresenter.InviteNotCopied) &&
                 rig.Notices.All(notice => notice.Text.IndexOf("ABCDE", StringComparison.Ordinal) < 0 && notice.Text.IndexOf("refused", StringComparison.Ordinal) < 0),
                "a clipboard that fails says so, without the code or the clipboard's own words");

            var joined = new SessionsRig(service: true);
            joined.Status(InternalSessionRole.Client, InternalJoinPhase.InSession);
            joined.Control.Status.Internet = new InternalInternetStatus { ServiceConfigured = true, Invite = new InternalInvite("ABCDE12345", Invite, "topiaforge://join/ABCDE12345", 1) };
            joined.Frame();
            joined.Presenter.Session.CopyInvite();
            That(!joined.Presenter.Session.CanCopyInvite && joined.Environment.Copied.Count == 0, "a joined player copies no invite, whatever the status holds");
        }

        /// <summary>
        /// Copying the failure details through a clipboard that fails says so in this game's words: what the clipboard threw
        /// never escapes the click, where the kit's isolation would log its message as it is.
        /// </summary>
        private static void AFailingClipboardStaysInTheGamesWords()
        {
            var refused = Failed(InternalLiveFailure.InviteNotFound);
            refused.Environment.CopyFailure = new InvalidOperationException("the clipboard refused " + Invite);
            refused.Presenter.Connecting.CopyDetails();
            refused.Frame();
            That(refused.Noticed(ConnectingPresenter.DetailsNotCopied) && refused.Environment.Copied.Count == 0 &&
                 refused.Notices.All(notice => notice.Text.IndexOf("refused", StringComparison.Ordinal) < 0 && notice.Text.IndexOf("ABCDE", StringComparison.Ordinal) < 0),
                "copying the details through a failing clipboard says so, and nothing escapes the click");
        }

        /// <summary>
        /// Every failure a player can meet, joining or hosting, says what to do next: try again, check something, ask the host,
        /// update, or that the game carries on by itself. Only failures that leave nothing to do are exempt.
        /// </summary>
        private static void EveryFailureSaysWhatToDoNext()
        {
            foreach (InternalLiveFailure failure in Enum.GetValues(typeof(InternalLiveFailure)))
            {
                if (NothingToDo.Contains(failure)) continue;
                foreach (var allowsDirect in new[] { false, true })
                {
                    var join = LiveFailureMessages.ForJoin(failure, InternalAdmissionCode.Other, allowsDirect).Message;
                    That(NextSteps.Any(step => join.IndexOf(step, StringComparison.Ordinal) >= 0), failure + " says what to do next when joining (" + join + ")");
                }

                var host = LiveFailureMessages.ForHost(failure).Message;
                That(NextSteps.Any(step => host.IndexOf(step, StringComparison.Ordinal) >= 0), failure + " says what to do next when hosting (" + host + ")");
            }

            foreach (InternalAdmissionCode code in Enum.GetValues(typeof(InternalAdmissionCode)))
            {
                if (code == InternalAdmissionCode.Banned) continue;
                var refusal = LiveFailureMessages.ForJoin(InternalLiveFailure.Rejected, code).Message;
                That(NextSteps.Any(step => refusal.IndexOf(step, StringComparison.Ordinal) >= 0), "the host's " + code + " refusal says what to do next (" + refusal + ")");
            }

            That(NextSteps.Any(step => LiveFailureMessages.WaitingOutTheService.Message.IndexOf(step, StringComparison.Ordinal) >= 0),
                "and so does a wait the service named");
        }

        /// <summary>
        /// A player pastes what the host shared, which may be the invite's link rather than its code, perhaps with a line
        /// break picked up from a chat window: the invite field reads the code out of either. Before, both were refused as a
        /// mistyped code.
        /// </summary>
        private static void APastedInviteLinkJoins()
        {
            foreach (var pasted in new[] { "topiaforge://join/ABCDE12345", "  TopiaForge://join/abcde-12345\r\n", "abcde 12345\n" })
            {
                var rig = new SessionsRig(service: true);
                var connect = rig.Presenter.Connect;
                connect.SetInviteCode(pasted);
                That(connect.InviteError.Length == 0 && connect.CanJoinInvite, "a pasted invite reads as its code (" + pasted.Trim() + ")");
                connect.JoinInvite();
                rig.Frame();
                That(rig.Control.Calls.Where(call => call.StartsWith("Query", StringComparison.Ordinal)).SequenceEqual(new[] { "Query:Invite:relay" }),
                    "and is checked as an invite, through the service alone (" + pasted.Trim() + ")");
            }

            var server = new SessionsRig(service: true);
            server.Presenter.Connect.SetInviteCode(LauncherJoinLink.ForServer(new string('7', 64))!);
            That(server.Presenter.Connect.InviteError == ConnectPresenter.InviteRule && !server.Presenter.Connect.CanJoinInvite,
                "a server link is no invite, and the field says what it takes");
            That(LauncherJoinLink.InviteCodeIn("topiaforge://join/ABCDE12345/") == null && LauncherJoinLink.InviteCodeIn("https://example.org/join/ABCDE12345") == null &&
                 LauncherJoinLink.InviteCodeIn("topiaforge://join/direct/" + new string('7', 64) + "/192.0.2.4/27250") == null && LauncherJoinLink.InviteCodeIn(null) == null,
                "and nothing else reads as an invite");
        }

        /// <summary>
        /// HOST offers the game last hosted first: hosting remembers it by launch target and world, and the next time HOST
        /// shows at the menu it is selected. A game the player picks stays picked while it is offered, however the list
        /// changes. Before, HOST always offered the first game, and held a pick only by its place in the list.
        /// </summary>
        private static void HostOffersTheGameLastHostedFirst()
        {
            var sample = new HostTargetChoice("Free Play · Sample", new LaunchRequest("io.example.freeplay", "io.example.sample"), "Free Play", "Sample");
            var sandbox = new HostTargetChoice("Free Play · Open sandbox", new LaunchRequest("io.example.freeplay", "io.example.sandbox"), "Free Play", "Open sandbox");
            var rig = HostRig(null, sample, sandbox);
            That(rig.Presenter.Host.SelectedTarget == 0, "with nothing hosted yet, HOST offers the first game");
            rig.Presenter.Host.SelectTarget(1);
            rig.Presenter.Host.Host();
            rig.Frame();
            var stored = rig.Store.Stored!;
            That(stored.HostedTarget == "io.example.freeplay" && stored.HostedWorld == "io.example.sandbox", "hosting remembers the game, by its target and world");

            var restarted = HostRig(stored, sample, sandbox);
            var host = restarted.Presenter.Host;
            That(host.SelectedTarget == 1, "the next time, HOST offers the game last hosted first");
            host.SelectTarget(0);
            restarted.Environment.Targets.Insert(0, new HostTargetChoice("Arena · Pit", new LaunchRequest("io.example.arena", "io.example.pit"), "Arena", "Pit"));
            restarted.Presenter.SelectTab(MultiplayerSubTab.Browse);
            restarted.Presenter.SelectTab(MultiplayerSubTab.Host);
            restarted.Frame();
            That(host.TargetOptions[host.SelectedTarget] == sample.Label, "a game the player picked stays picked as the list changes");

            var gone = HostRig(stored, sample);
            That(gone.Presenter.Host.SelectedTarget == 0, "a remembered game no longer offered falls back to the first");

            var running = new SessionsRig();
            running.Environment.CurrentGame = new HostTargetChoice("This game: Free Play · Open sandbox", new LaunchRequest("io.example.freeplay", "io.example.sandbox"), "Free Play", "Open sandbox");
            running.Environment.Phase = SessionPhase.Running;
            running.Presenter.SelectTab(MultiplayerSubTab.Host);
            running.Frame();
            running.Presenter.Host.Host();
            running.Frame();
            That(running.Store.Stored!.HostedTarget == "io.example.freeplay" && running.Store.Stored.HostedWorld == "io.example.sandbox",
                "opening a running game in place remembers that game too");

            var document = stored.ToDocument();
            document.HostedTarget = "not an id/../";
            That(MultiplayerSettings.FromDocument(document).HostedTarget == null && MultiplayerSettings.FromDocument(document).HostedWorld == null,
                "a stored game that is not a launch target is forgotten");
            document.HostedTarget = null;
            document.HostedWorld = null;
            That(MultiplayerSettings.FromDocument(document).HostedTarget == null, "and settings written before this remember no game");
            That(stored.WithHideMyIp(false).HostedTarget == "io.example.freeplay" && stored.AcknowledgeDisclosure().HostedWorld == "io.example.sandbox",
                "every other change keeps the game remembered");
        }

        /// <summary>
        /// The settings file keeps schema 1 because a reader ignores the members it does not know. A game from before the game
        /// last hosted was remembered reads a newer game's file, schema and all, and this game reads what that older game
        /// writes back, remembering no game. The display name and the hosting defaults are the launcher's preferences now: this
        /// game writes neither to the file, and ignores what an older game wrote there, so neither has a second writable copy.
        /// </summary>
        private static void TheSettingsFileReadsAcrossVersions(string root)
        {
            var directory = Path.Combine(root, "settings-across-versions");
            Directory.CreateDirectory(directory);
            var store = new FileMultiplayerSettingsStore(directory);
            store.Save(MultiplayerSettings.Default.AcknowledgeDisclosure().WithHideMyIp(false).WithHostedGame("io.example.freeplay", "io.example.sandbox"));
            var path = Path.Combine(directory, FileMultiplayerSettingsStore.FileName);
            var written = File.ReadAllText(path);
            That(written.IndexOf("\"hostedTarget\"", StringComparison.Ordinal) > 0, "the game last hosted is written to the file");
            That(written.IndexOf("\"displayName\"", StringComparison.Ordinal) < 0 && written.IndexOf("\"serverName\"", StringComparison.Ordinal) < 0 &&
                 written.IndexOf("\"hostVisibility\"", StringComparison.Ordinal) < 0, "and neither the name nor the hosting defaults, which the launcher keeps");

            var older = JsonUtil.LoadPersistentFile<SettingsBeforeTheHostedGame?>(path, null);
            That(older != null && older.SchemaVersion == 1 && older.HideMyIp == false && older.AcknowledgedPrivacyDisclosure == 1,
                "a game from before the game last hosted reads the newer file as schema 1, keeping everything it knows");

            older!.DisplayName = "Sam";
            older.ServerName = "Night";
            older.HostVisibility = "invite";
            JsonUtil.SaveFile(path, older);
            var reread = store.Load();
            That(!reread.HideMyIp && reread.AcknowledgedDisclosure == 1 && reread.HostedTarget == null && reread.HostedWorld == null,
                "and this game reads what that older game writes back, remembering no game");
            store.Save(reread.WithHideMyIp(true));
            That(File.ReadAllText(path).IndexOf("Night", StringComparison.Ordinal) < 0,
                "and what an older game kept there of the launcher's preferences is not carried forward");
        }

        /// <summary>The settings file as a game wrote it before it remembered the game last hosted.</summary>
        [DataContract]
        internal sealed class SettingsBeforeTheHostedGame
        {
            [DataMember(Name = "schemaVersion")]
            public int SchemaVersion { get; set; }

            [DataMember(Name = "acknowledgedPrivacyDisclosure")]
            public int AcknowledgedPrivacyDisclosure { get; set; }

            [DataMember(Name = "hideMyIp", EmitDefaultValue = false)]
            public bool? HideMyIp { get; set; }

            [DataMember(Name = "displayName", EmitDefaultValue = false)]
            public string? DisplayName { get; set; }

            [DataMember(Name = "serverName", EmitDefaultValue = false)]
            public string? ServerName { get; set; }

            [DataMember(Name = "hostMaxPlayers")]
            public int HostMaxPlayers { get; set; }

            [DataMember(Name = "hostVisibility", EmitDefaultValue = false)]
            public string? HostVisibility { get; set; }

            [DataMember(Name = "hostVoice")]
            public bool HostVoice { get; set; }

            [DataMember(Name = "favorites", EmitDefaultValue = false)]
            public List<ServerBookmarkDocument>? Favorites { get; set; }

            [DataMember(Name = "history", EmitDefaultValue = false)]
            public List<ServerBookmarkDocument>? History { get; set; }
        }

        /// <summary>HOST at the main menu, with <paramref name="choices"/> to start and the settings an earlier run left.</summary>
        private static SessionsRig HostRig(MultiplayerSettings? settings, params HostTargetChoice[] choices)
        {
            var rig = new SessionsRig(settings: settings);
            rig.Environment.Targets.AddRange(choices);
            rig.Presenter.SelectTab(MultiplayerSubTab.Host);
            rig.Frame();
            return rig;
        }
    }
}
