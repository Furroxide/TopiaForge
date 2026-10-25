using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A remote player's UI and actions keep the contracts the local ones keep: a modal always reports one answer, an
    /// author's failing callback is reported to its mod, and nothing reports success when it cannot reach the player.
    /// </summary>
    internal static partial class PlayersRemoteContractTests
    {
        internal static void Run()
        {
            var failures = new List<string>();
            Check(failures, DisposingARemoteModalCancelsIt);
            Check(failures, APlayerWhoLeavesCancelsItsModal);
            Check(failures, AnEndingScopeCancelsItsRemoteModals);
            Check(failures, StoppingHostingCancelsOpenModals);
            Check(failures, AFailingRemoteCallbackIsReportedToItsMod);
            Check(failures, AModOtherPlayersLackSaysItCannotReachThem);
            Check(failures, ToastsKeepTheLocalContract);
            Check(failures, AFailingJoinHandlerIsReported);
            Check(failures, AHealPastFullHealthCannotBeBanked);
            Check(failures, TheFakeKeepsTheModalContract);
            Check(failures, AShortHostTeleportHidesNoLaterTeleport);
            Check(failures, AStoppingScopeCancelsBeforeItIsDisposed);
            Check(failures, CancellationCanResetTheRuntime);
            Check(failures, ResetMustCancelBeforeAScopeFinishesDisposing);
            Check(failures, PlacementsKeepTheTeleportAndSpeedBudgets);
            Check(failures, ConsecutiveHealsCannotBankExcessHealth);
            Check(failures, DismissalSubscribersAreIsolated);
            Check(failures, TheRecipientCanDisposeItsModal);
            Check(failures, DismissalSurvivesAContentUpdate);
            Check(failures, RecipientScopeDisposalAnswersTheHost);
            Check(failures, RecipientCleanupOrdersAnswerTheHost);
            Check(failures, RecipientCleanupCanCancelEveryOpenModal);
            Check(failures, AHostHideIsNotThePlayersDismissal);
            Check(failures, CancellationsKeepTheirChecksUnderAFlood);
            Check(failures, HealCreditKeepsToItsGap);
            if (failures.Count > 0) throw new InvalidOperationException("Players API: " + string.Join(" | ", failures));
            Console.WriteLine("PlayersRemoteContractTests: 25 groups passed.");
        }

        private static void Check(List<string> failures, Action test)
        {
            try { test(); }
            catch (InvalidOperationException exception) { failures.Add(test.Method.Name + ": " + exception.Message); }
        }

        private static void DisposingARemoteModalCancelsIt()
        {
            using var peers = new Peers();
            var answers = new List<bool>();
            var modal = peers.Host.Player(peers.First.Id).Ui.ShowModal(new UiModalRequest("Ready?", "Start the round?"), answers.Add).Value!;
            peers.Rig.Frames(3);
            That(peers.FirstServices.Ui.Modals.Count == 1, "the player sees the modal");
            modal.Dispose();
            peers.Rig.Frames(3);
            That(answers.Count == 1 && !answers[0], "disposing a remote modal reports one cancellation, as a local modal does (" + answers.Count + ")");
            That(peers.FirstServices.Ui.Modals.Count == 0 && !modal.IsOpen, "and closes it on the player's game");
            modal.Close();
            That(answers.Count == 1, "a cancelled modal never answers twice");
        }

        private static void APlayerWhoLeavesCancelsItsModal()
        {
            using var peers = new Peers();
            var answers = new List<bool>();
            peers.Host.Player(peers.First.Id).Ui.ShowModal(new UiModalRequest("Ready?", "Start the round?"), answers.Add);
            peers.Rig.Frames(3);
            peers.Rig.Network.Leave(peers.First.Control);
            peers.Rig.Frames(3);
            That(answers.Count == 1 && !answers[0], "a player who leaves cancels the modal it was asked (" + answers.Count + ")");
            peers.Rig.Frames(3);
            That(answers.Count == 1, "and only once");
        }

        private static void AnEndingScopeCancelsItsRemoteModals()
        {
            using var peers = new Peers();
            var scope = new PlayersTestServices(peers.HostRuntime, peers.Rig.Host.Control, peers.Rig.Host.Backend);
            var answers = new List<bool>();
            scope.Player(peers.First.Id).Ui.ShowModal(new UiModalRequest("Vote", "Skip this map?"), answers.Add);
            peers.Rig.Frames(3);
            That(peers.FirstServices.Ui.Modals.Count == 1, "the scope's modal reaches the player");
            scope.Dispose();
            That(answers.Count == 1 && !answers[0], "the scope that showed it ending counts as cancelling, as IUiService.ShowModal says (" + answers.Count + ")");
            peers.Rig.Frames(3);
            That(peers.FirstServices.Ui.Modals.Count == 0 && answers.Count == 1, "the player's copy closes, and nothing answers again");
        }

        private static void StoppingHostingCancelsOpenModals()
        {
            using var peers = new Peers();
            var answers = new List<bool>();
            peers.Host.Player(peers.First.Id).Ui.ShowModal(new UiModalRequest("Ready?", "Start the round?"), answers.Add);
            peers.Rig.Frames(3);
            peers.Rig.Network.StopHosting();
            peers.Rig.Frames(3);
            That(answers.Count == 1 && !answers[0], "a session that ends cancels its open modals (" + answers.Count + ")");
        }

        private static void AFailingRemoteCallbackIsReportedToItsMod()
        {
            using var peers = new Peers();
            var worked = 0;
            var surface = peers.Host.Player(peers.First.Id).Ui.CreateSurface(new UiSurfaceRequest("shop", "Shop", "", content: new UiColumn(
                new UiButton("closed", "Closed", () => throw new InvalidOperationException("the shop is closed")),
                new UiButton("strict", "Strict", () => throw new NotSupportedException("strict mode")),
                new UiButton("open", "Open", () => worked++)))).Value!;
            peers.Rig.Frames(3);
            var rejected = peers.HostRuntime.Rejected;
            var controls = ((UiColumn)peers.FirstServices.Ui.Surfaces[0].Content!).Children;
            ((UiButton)controls[0]).Activated();
            ((UiButton)controls[1]).Activated();
            ((UiButton)controls[2]).Activated();
            peers.Rig.Frames(3);
            var entries = peers.Host.Logger.Entries;
            string Show() => string.Join(" / ", entries.Select(entry => entry.Message + " (" + entry.Exception?.Message + ")"));
            That(worked == 1, "the next reply still runs");
            That(entries.Any(entry => entry.Level == CapturedLogLevel.Error && entry.Message.Contains("'closed'", StringComparison.Ordinal) && entry.Exception?.Message == "the shop is closed"),
                "an author's InvalidOperationException goes to the mod's own log, naming the control: " + Show());
            That(entries.Any(entry => entry.Message.Contains("'strict'", StringComparison.Ordinal) && entry.Exception is NotSupportedException),
                "and so does any other exception: " + Show());
            var runtimeLines = peers.Rig.Host.Log.Lines;
            That(!runtimeLines.Any(line => line.Contains("frame stage failed", StringComparison.Ordinal)), "a callback never fails the whole frame stage: " + string.Join(" / ", runtimeLines));
            That(peers.HostRuntime.Rejected == rejected, "an author's exception is not counted as a refused reply (" + (peers.HostRuntime.Rejected - rejected) + ")");
            var completions = 0;
            peers.Host.Player(peers.First.Id).Ui.ShowModal(new UiModalRequest("Ready?", "Start?"), _ => { completions++; throw new InvalidOperationException("the vote is broken"); });
            peers.Rig.Frames(3);
            peers.FirstServices.Ui.Modals[0].Confirm();
            peers.Rig.Frames(3);
            That(completions == 1 && peers.Host.Logger.Entries.Any(entry => entry.Exception?.Message == "the vote is broken"), "a failing modal answer is logged too");
            surface.Dispose();
        }

        private static void AModOtherPlayersLackSaysItCannotReachThem()
        {
            using var peers = new Peers();
            using var hostOnly = new PlayersTestServices(peers.HostRuntime, peers.Rig.Host.Control, peers.Rig.Host.Backend, "host-only", networked: false);
            var target = hostOnly.Player(peers.First.Id);
            var health = peers.First.Backend.Health;
            var feet = peers.First.Backend.Feet;
            var results = new[]
            {
                Describe(target.Damage(new PlayerDamageRequest(30, "trap"))),
                Describe(target.Heal(5, "shrine")),
                Describe(target.Teleport(new Vec3(40, 0, 40))),
                Describe(target.Ui.CreateSurface(new UiSurfaceRequest("rules", "Rules", "Host rules"))),
                Describe(target.Ui.ShowModal(new UiModalRequest("Rules", "Accept?"), _ => { })),
                Describe(target.Ui.ShowToast("Welcome")),
            };
            peers.Rig.Frames(24);
            That(results.All(result => result.StartsWith("Unavailable:", StringComparison.Ordinal) && result.Contains("client-local or server-only", StringComparison.Ordinal)),
                "every action and window of a mod the player lacks says it cannot reach them: " + string.Join(" / ", results));
            That(peers.First.Backend.Health == health && peers.First.Backend.Feet.X == feet.X && peers.FirstServices.Ui.Surfaces.Count == 0 &&
                peers.FirstServices.Ui.Modals.Count == 0 && peers.FirstServices.Ui.Toasts.Count == 0, "and nothing was sent");
            That(hostOnly.Player(peers.Rig.Host.Id).Heal(1, "own").Succeeded, "it still acts on the host's own player");
            That(target.TryGetSnapshot(out _) && target.TryGetHealth(out _) && target.Entity != null, "non-shared mods retain presence reads");
            using var column = hostOnly.Directory.RegisterScoreboardColumn(new ScoreboardColumnDefinition("local-score", "Score")).Value!;
            That(column.SetValue(target.Id, "7", 7).Succeeded, "non-shared mods can still write scoreboard columns");
            peers.Rig.Frames(3);
            That(peers.FirstServices.Context.Runtime.Scoreboard.Count == 1, "their columns still reach the other player without a mod receiver");
            That(peers.Host.Player(peers.First.Id).Damage(new PlayerDamageRequest(30, "trap")).Succeeded, "a shared mod still reaches the player");
            peers.Rig.Frames(24);
            That(peers.First.Backend.Health < health, "and its damage arrives");
        }

        private static void ToastsKeepTheLocalContract()
        {
            using var peers = new Peers();
            var longToast = new string('x', 600);
            var local = peers.Host.Player(peers.Rig.Host.Id).Ui.ShowToast(longToast);
            That(local.Succeeded && peers.Host.Ui.Toasts.Count == 1, "the local player's toast follows Context.Ui, which has no remote limit (" + Describe(local) + ")");
            var remote = peers.Host.Player(peers.First.Id).Ui.ShowToast(longToast);
            That(remote.ErrorCode == ModErrorCode.InvalidArgument && remote.ErrorMessage.Contains("512", StringComparison.Ordinal),
                "a toast too long for another player says how long one may be (" + Describe(remote) + ")");
            var blank = peers.Host.Player(peers.First.Id).Ui.ShowToast(" ");
            That(blank.ErrorCode == ModErrorCode.InvalidArgument, "a blank toast is refused, as Context.Ui refuses it (" + Describe(blank) + ")");
            That(peers.Host.Player(peers.First.Id).Ui.ShowToast("Round two").Succeeded, "a fitting toast still goes");
            peers.Rig.Frames(3);
            That(peers.FirstServices.Ui.Toasts.Count == 1 && peers.FirstServices.Ui.Toasts[0].Message == "Round two", "and only it arrives");
        }

        private static void AFailingJoinHandlerIsReported()
        {
            var failures = new List<Exception>();
            var snapshot = Snapshot("local");
            using var directory = new PlayerDirectory(() => snapshot, () => 0, null, failures.Add);
            var later = 0;
            using var broken = directory.SubscribeJoined(_ => throw new InvalidOperationException("the lobby is full"));
            using var counted = directory.SubscribeJoined(_ => later++);
            snapshot = Snapshot("local", "p1");
            _ = directory.Players;
            That(later == 1, "the next handler still runs");
            That(failures.Count == 1 && failures[0].Message == "the lobby is full", "the handler's exception goes to the directory's report (" + failures.Count + ")");
            using var fake = new FakePlayerDirectory();
            using var fakeBroken = fake.SubscribeLeft(_ => throw new InvalidOperationException("the fake lobby is closed"));
            fake.Add(new ParticipantId("p1"), "Sam");
            fake.Remove(new ParticipantId("p1"));
            That(fake.CallbackErrors.Count == 1 && fake.CallbackErrors[0].Contains("the fake lobby is closed", StringComparison.Ordinal),
                "the fake keeps the failure for the test to see, as FakeUiModal does (" + fake.CallbackErrors.Count + ")");
        }

        private static void AHealPastFullHealthCannotBeBanked()
        {
            using var peers = new Peers();
            var target = peers.Host.Player(peers.First.Id);
            float Seen() => target.TryGetHealth(out var health) ? health!.Current : -1f;
            That(Seen() == 100f, "the player starts at full health (" + Seen() + ")");
            That(target.Heal(50, "shrine").Succeeded, "the host heals a player already at full health");
            peers.Rig.Frames(24);
            peers.First.Backend.Health = 40;
            That(peers.Rig.RunUntil(() => Seen() <= 41f, 120), "the host sees the damage the player reports (" + Seen() + ")");
            peers.First.Backend.Health = 100;
            peers.Rig.Frames(20);
            That(Seen() < 60f, "a hostile game's jump back to full health still rises at the regeneration rate: the earlier heal " +
                "added nothing, so it left nothing to spend (" + Seen() + ")");
        }

        private static void TheFakeKeepsTheModalContract()
        {
            using var fake = new FakePlayerDirectory();
            var sam = fake.Add(new ParticipantId("p1"), "Sam");
            var answers = new List<bool>();
            sam.Ui.ShowModal(new UiModalRequest("Ready?", "Start the round?"), answers.Add);
            fake.Remove(sam.Id);
            That(answers.Count == 1 && !answers[0], "the fake cancels a departed player's modal, as the game does (" + answers.Count + ")");
        }

        private static void AShortHostTeleportHidesNoLaterTeleport()
        {
            using var peers = new Peers();
            var target = peers.Host.Player(peers.First.Id);
            var feet = peers.First.Backend.Feet;
            That(target.Teleport(new Vec3(feet.X + 0.5f, feet.Y, feet.Z)).Succeeded, "the host moves the player half a metre");
            peers.Rig.Frames(24);
            var corrections = peers.Rig.Host.Counters.Corrections;
            That(corrections == 0, "the short move lands where the host placed it (" + corrections + ")");
            // Later the player's own game teleports it, as a respawn or a teleporter does.
            peers.First.Backend.Feet = new Core.Multiplayer.NetVector3(feet.X + 200f, feet.Y, feet.Z);
            peers.Rig.Frames(24);
            That(peers.Rig.Host.Counters.Corrections == corrections, "that teleport is still the player's own, within the host's teleport " +
                "allowance, not a speed violation the short move's expectation hid (" + peers.Rig.Host.Counters.Corrections + " corrections)");
        }

        private static void AStoppingScopeCancelsBeforeItIsDisposed()
        {
            using var peers = new Peers();
            var lifetime = new StoppableLifetime();
            var context = peers.HostRuntime.Bind("players-test", true, new CapturedModLogger(), lifetime,
                new FakeLocalPlayerService(new FakeModLifetime()), new FakeUiService(new FakeModLifetime()), new ModInputActivity());
            using var directory = new PlayerDirectory(() => peers.Rig.Host.Control.SessionSnapshot, () => 0, context);
            context.Directory = directory;
            That(directory.TryGet(peers.First.Id, out var target), "the scope sees the player");
            var answers = new List<bool>();
            target!.Ui.ShowModal(new UiModalRequest("Vote", "Skip this map?"), answers.Add);
            peers.Rig.Frames(3);
            That(peers.FirstServices.Ui.Modals.Count == 1, "the scope's modal reaches the player");
            // A scope's close begins stopping, then waits for native work to drain before it disposes its resources.
            lifetime.Stopping = true;
            peers.Rig.Frames(3);
            That(answers.Count == 1 && !answers[0], "a scope that has begun stopping cancels its modal before it disposes it (" + answers.Count + ")");
            That(peers.FirstServices.Ui.Modals.Count == 0, "and the player's copy closes");
            lifetime.Dispose();
            peers.Rig.Frames(3);
            That(answers.Count == 1, "disposing it later answers nothing again");
        }

        /// <summary>A scope lifetime that can be stopping before it is disposed, as a scope that drains native work is.</summary>
        private sealed class StoppableLifetime : IModLifetime
        {
            private readonly FakeModLifetime inner = new FakeModLifetime();
            internal bool Stopping { get; set; }
            public System.Threading.CancellationToken StoppingToken => inner.StoppingToken;
            public bool IsStopping => Stopping || inner.IsStopping;
            public IDisposable Track(IDisposable resource) => inner.Track(resource);
            public IDisposable Defer(Action cleanup) => inner.Defer(cleanup);
            public void Dispose() => inner.Dispose();
        }

        private static string Describe<T>(OperationResult<T> result) where T : notnull =>
            result.Succeeded ? "Succeeded" : result.ErrorCode + ": " + result.ErrorMessage;

        private static MultiplayerSessionSnapshot Snapshot(params string[] ids) => new MultiplayerSessionSnapshot(
            new MultiplayerSessionId("contract"), MultiplayerSessionState.Ready, MultiplayerProcessKind.Interactive,
            MultiplayerExecutionSide.Client | MultiplayerExecutionSide.Server, new ParticipantId("local"),
            ids.Select(id => new MultiplayerParticipant(new ParticipantId(id), id == "local" ? "Sam" : "Guest", id == "local", true, id == "local", default, null, default)).ToArray(),
            new NetworkTick(1), new SessionSeed(1), new ParticipantId("local"), MultiplayerSessionEndReason.None);

        private static void That(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        /// <summary>A listen host and one joined player, each with the Players runtime and the test mod bound.</summary>
        private sealed class Peers : IDisposable
        {
            internal Peers()
            {
                Rig = new PresenceRig();
                Rig.Host.Runtime.Disconnect();
                HostRuntime = MultiplayerPlayers.Install(Rig.Host.Runtime, Rig.Host.Presence);
                Rig.Host.Runtime.Connect();
                Host = new PlayersTestServices(HostRuntime, Rig.Host.Control, Rig.Host.Backend);
                Rig.OpenToMultiplayer();
                First = Rig.AddPlayer("p1");
                First.Runtime.Disconnect();
                var firstRuntime = MultiplayerPlayers.Install(First.Runtime, First.Presence);
                First.Runtime.Connect();
                FirstServices = new PlayersTestServices(firstRuntime, First.Control, First.Backend);
                Rig.Join(First);
                Rig.Frames(24);
            }

            internal PresenceRig Rig { get; }
            internal MultiplayerPlayers HostRuntime { get; }
            internal PlayersTestServices Host { get; }
            internal PresenceNode First { get; }
            internal PlayersTestServices FirstServices { get; }

            public void Dispose()
            {
                FirstServices.Dispose();
                Host.Dispose();
            }
        }
    }
}
