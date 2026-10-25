using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class PlayersRemoteContractTests
    {
        internal static void CancellationCanResetTheRuntime()
        {
            using var peers = new Peers();
            var cancelled = 0;
            var ui = peers.Host.Player(peers.First.Id).Ui;
            ui.ShowModal(new UiModalRequest("First", "Ready?"), _ => cancelled++);
            ui.ShowModal(new UiModalRequest("Second", "Ready?"), _ => cancelled++);
            ui.ShowModal(new UiModalRequest("Last", "Ready?"), _ => { cancelled++; peers.Rig.Host.Runtime.Disconnect(); });
            peers.Rig.Frames(3);
            peers.Rig.Network.Leave(peers.First.Control);
            peers.Rig.Frames(3);
            That(cancelled == 3, "resetting during cancellation must answer all three exactly once: " + cancelled);
            That(!peers.Rig.Host.Log.Lines.Any(line => line.Contains("frame stage failed", StringComparison.Ordinal)),
                "cancellation must not fail a frame: " + string.Join(" / ", peers.Rig.Host.Log.Lines));
        }

        internal static void ResetMustCancelBeforeAScopeFinishesDisposing()
        {
            using var peers = new Peers();
            using var scope = new PlayersTestServices(peers.HostRuntime, peers.Rig.Host.Control, peers.Rig.Host.Backend);
            var disposed = false;
            var late = false;
            var answered = 0;
            peers.Host.Player(peers.First.Id).Ui.ShowModal(new UiModalRequest("First", "Ready?"), _ =>
            {
                scope.Dispose();
                disposed = true;
            });
            scope.Player(peers.First.Id).Ui.ShowModal(new UiModalRequest("Scoped", "Ready?"), _ => { answered++; late |= disposed; });
            peers.Rig.Frames(3);
            peers.Rig.Host.Runtime.Disconnect();
            That(answered == 1 && !late, "the scoped cancellation must run within scope disposal, never after it returns");
        }

        internal static void PlacementsKeepTheTeleportAndSpeedBudgets()
        {
            foreach (var flags in new[] { PresenceFlags.None, PresenceFlags.Teleported })
            {
                var policy = new PresencePlausibility(PresencePolicy.Default, 0);
                PresenceCheck Pose(float x, PresenceFlags mark, long time) => policy.Check(
                    NetQuantization.QuantizePosition(new NetVector3(x, 0, 0)), NetQuantization.IdentityRotationBits,
                    mark, policy.Correction, time, PresenceBounds.Everywhere);
                Pose(0, PresenceFlags.None, 0);
                policy.ExpectPlacement(NetQuantization.QuantizePosition(new NetVector3(500, 0, 0)), 0);
                That(Pose(500, flags, 50_000).Verdict == PresenceVerdict.Accepted, "placement accepts either flag");
                That(Pose(-500, PresenceFlags.Teleported, 100_000).Verdict == PresenceVerdict.Accepted, "placement costs no teleport");
                That(Pose(500, PresenceFlags.Teleported, 150_000).Verdict == PresenceVerdict.Accepted, "second own teleport fits");
                That(Pose(-500, PresenceFlags.Teleported, 200_000).Violation == PresenceViolation.Teleport, "third own teleport is refused");
            }
            var miss = new PresencePlausibility(PresencePolicy.Default, 0);
            PresenceCheck Miss(float x, PresenceFlags mark, long time) => miss.Check(
                NetQuantization.QuantizePosition(new NetVector3(x, 0, 0)), NetQuantization.IdentityRotationBits,
                mark, miss.Correction, time, PresenceBounds.Everywhere);
            Miss(0, PresenceFlags.None, 0);
            miss.ExpectPlacement(NetQuantization.QuantizePosition(new NetVector3(100, 0, 0)), 0);
            That(Miss(500, PresenceFlags.Teleported, 50_000).Verdict == PresenceVerdict.Accepted, "missed placement uses own allowance");
            That(Miss(-500, PresenceFlags.Teleported, 100_000).Verdict == PresenceVerdict.Accepted, "second own teleport fits");
            That(Miss(500, PresenceFlags.Teleported, 150_000).Violation == PresenceViolation.Teleport, "miss did spend the allowance");
            That(Miss(500, PresenceFlags.None, 200_000).Violation == PresenceViolation.Speed, "unflagged hostile jump is still speed");
        }

        internal static void ConsecutiveHealsCannotBankExcessHealth()
        {
            using var peers = new Peers();
            var target = peers.Host.Player(peers.First.Id);
            float Seen() => target.TryGetHealth(out var health) ? health!.Current : -1;
            peers.First.Backend.Health = 40;
            That(peers.Rig.RunUntil(() => Seen() <= 41, 120), "initial damage is visible");
            That(target.Heal(50, "first").Succeeded && target.Heal(50, "second").Succeeded, "both heals sent before a new health report");
            peers.Rig.Frames(24);
            That(Seen() == 100, "the allowed heals fill health");
            peers.First.Backend.Health = 40;
            That(peers.Rig.RunUntil(() => Seen() <= 41, 120), "later damage is visible");
            peers.First.Backend.Health = 100;
            peers.Rig.Frames(20);
            That(Seen() < 60, "two pending heals must not leave excess allowance for a false report: " + Seen());

            var delayed = new PresenceHealth(PresencePolicy.Default);
            delayed.Take(40, 100, 0);
            delayed.AllowGain(60);
            delayed.Take(100, 100, 10_000_000);
            delayed.Take(40, 100, 10_000_000);
            delayed.Take(100, 100, 10_000_001);
            That(delayed.Health < 41, "health restored by regeneration retires unspent heal credit too");
        }

        internal static void DismissalSubscribersAreIsolated()
        {
            using var peers = new Peers();
            var surface = (RemoteUiSurface)peers.Host.Player(peers.First.Id).Ui.CreateSurface(new UiSurfaceRequest("test", "Window", "Body")).Value!;
            var later = 0;
            surface.Dismissed += () => throw new InvalidOperationException("dismissal failed");
            surface.Dismissed += () => later++;
            peers.Rig.Frames(3);
            peers.FirstServices.Ui.Surfaces[0].Hide();
            peers.Rig.Frames(3);
            That(later == 1 && peers.Host.Logger.Entries.Any(entry => entry.Exception?.Message == "dismissal failed"),
                "each dismissal subscriber must run and report its own error: " + later);
        }

        internal static void TheRecipientCanDisposeItsModal()
        {
            using var peers = new Peers();
            var answers = 0;
            var confirmed = false;
            var modal = peers.Host.Player(peers.First.Id).Ui.ShowModal(new UiModalRequest("Ready", "Start?"), answer =>
            {
                answers++;
                confirmed |= answer;
            }).Value!;
            peers.Rig.Frames(3);
            peers.FirstServices.Ui.Modals[0].Dispose();
            peers.Rig.Frames(3);
            That(answers == 1 && !confirmed && !modal.IsOpen, "disposing the recipient's modal reports one cancellation to the host");
            modal.Dispose();
            peers.Rig.Frames(3);
            That(answers == 1, "the host cannot cancel it twice");
        }

        internal static void DismissalSurvivesAContentUpdate()
        {
            using var peers = new Peers();
            var clicks = 0;
            var surface = (RemoteUiSurface)peers.Host.Player(peers.First.Id).Ui.CreateSurface(new UiSurfaceRequest("test", "Window", "Body",
                content: new UiButton("buy", "Buy", () => clicks++))).Value!;
            var dismissed = 0;
            ((IUiSurfaceDismissalSource)surface).Dismissed += () => dismissed++;
            peers.Rig.Frames(3);

            void Reply(byte kind, ulong revision)
            {
                var writer = new WorldWireWriter(32768);
                writer.Begin(kind); writer.WriteString("players-test"); writer.WriteVarUInt(surface.Token); writer.WriteVarUInt(revision);
                writer.WriteString(kind == 14 ? "buy" : ""); writer.WriteString("");
                peers.FirstServices.Context.Runtime.Channel!.Send(writer.Segment, InternalSystemTarget.Host);
            }
            var oldRevision = surface.Revision;
            That(surface.SetContent(new UiButton("buy", "Buy again", () => clicks++)).Succeeded, "replace the control");
            Reply(14, oldRevision);
            Reply(15, 0);
            Reply(15, surface.Revision + 1);
            peers.Rig.Frames(3);
            That(clicks == 0 && dismissed == 0 && surface.IsVisible, "old controls and nonexistent dismissal revisions remain refused");

            peers.FirstServices.Ui.Surfaces[0].Hide();
            That(surface.SetContent(new UiColumn()).Succeeded, "the host updates content while dismissal is in flight");
            peers.Rig.Frames(6);
            That(dismissed == 1 && !surface.IsVisible && !peers.FirstServices.Ui.Surfaces[0].IsVisible,
                "a content update must not discard the player's dismissal or reopen the window: dismissed=" + dismissed);
        }

        internal static void RecipientScopeDisposalAnswersTheHost()
        {
            using var peers = new Peers();
            var answers = 0;
            var modal = peers.Host.Player(peers.First.Id).Ui.ShowModal(new UiModalRequest("Ready", "Start?"), _ => answers++).Value!;
            peers.Rig.Frames(3);
            peers.FirstServices.Dispose();
            peers.Rig.Frames(3);
            That(answers == 1 && !modal.IsOpen, "ending the recipient scope must cancel the host modal: " + answers);
            modal.Dispose();
            peers.Rig.Frames(3);
            That(answers == 1, "later host disposal must not repeat the recipient cancellation");
        }

        internal static void RecipientCleanupOrdersAnswerTheHost()
        {
            foreach (var stopping in new[] { false, true })
            {
                using var peers = new Peers();
                var runtime = peers.FirstServices.Context.Runtime;
                peers.FirstServices.Dispose();
                using var uiLifetime = new FakeModLifetime();
                using var lifetime = new StoppableLifetime();
                var ui = new FakeUiService(uiLifetime);
                var context = runtime.Bind("players-test", true, new CapturedModLogger(), lifetime,
                    new FakeLocalPlayerService(uiLifetime), ui, new ModInputActivity());
                var answers = 0;
                var confirmed = false;
                for (var i = 0; i < 2; i++)
                    peers.Host.Player(peers.First.Id).Ui.ShowModal(new UiModalRequest("Ready", "Start?"), answer => { answers++; confirmed |= answer; });
                peers.Rig.Frames(3);
                That(ui.Modals.Count == 2, "both requests reach the recipient scope");
                if (stopping) lifetime.Stopping = true;
                else context.Dispose();
                peers.Rig.Frames(3);
                That(answers == 2 && !confirmed && ui.Modals.Count == 0,
                    "recipient cleanup cancels both before UI disposal, including while a scope is draining: " + answers);
                lifetime.Dispose();
                uiLifetime.Dispose();
                peers.Rig.Frames(3);
                That(answers == 2, "later disposal cannot repeat a recipient cancellation");
            }
        }

        internal static void RecipientCleanupCanCancelEveryOpenModal()
        {
            using var peers = new Peers();
            var answers = 0;
            for (var i = 0; i < 64; i++)
            {
                That(peers.Host.Player(peers.First.Id).Ui.ShowModal(new UiModalRequest("Ready", "Start?"), _ => answers++).Succeeded,
                    "each modal fits the open UI and send budgets");
                if (i % 20 == 19) peers.Rig.Frames(60);
            }
            peers.Rig.Frames(3);
            That(peers.FirstServices.Ui.Modals.Count == 64, "all sixty-four modals reached the recipient");
            peers.FirstServices.Dispose();
            peers.Rig.Frames(3);
            That(answers == 64, "scope cleanup must not lose modal cancellations to the control reply burst: " + answers);
        }

        internal static void AHostHideIsNotThePlayersDismissal()
        {
            foreach (var shownAgain in new[] { "never", "as it was", "with new content" })
            {
                using var peers = new Peers();
                var surface = peers.Host.Player(peers.First.Id).Ui.CreateSurface(new UiSurfaceRequest("round", "Round", "Waiting")).Value!;
                var dismissed = 0;
                ((IUiSurfaceDismissalSource)surface).Dismissed += () => dismissed++;
                peers.Rig.Frames(3);
                var received = peers.FirstServices.Ui.Surfaces[0];
                var rejected = peers.HostRuntime.Rejected;
                surface.Hide();
                That(peers.Rig.RunUntil(() => !received.IsVisible, 10), "the player's copy hides with the host's");
                // The host shows the window again before anything the player's game sent about that hide could arrive.
                if (shownAgain == "with new content") That(surface.SetContent(new UiText("Round two")).Succeeded, "the host replaces the content");
                if (shownAgain != "never") surface.Show();
                peers.Rig.Frames(6);
                var open = shownAgain != "never";
                That(surface.IsVisible == open && received.IsVisible == open && dismissed == 1 && peers.HostRuntime.Rejected == rejected,
                    "applying the host's own hide is not the player's dismissal, so nothing answers it and a window shown again (" +
                    shownAgain + ") stays open: host " + surface.IsVisible + ", player " + received.IsVisible + ", dismissed " + dismissed +
                    ", refused replies " + (peers.HostRuntime.Rejected - rejected));
            }
        }

        internal static void CancellationsKeepTheirChecksUnderAFlood()
        {
            using var peers = new Peers();
            var second = peers.Rig.AddPlayer("p2");
            second.Runtime.Disconnect();
            var secondRuntime = MultiplayerPlayers.Install(second.Runtime, second.Presence);
            second.Runtime.Connect();
            using var secondServices = new PlayersTestServices(secondRuntime, second.Control, second.Backend);
            peers.Rig.Join(second);
            peers.Rig.Frames(24);
            var firstRuntime = peers.FirstServices.Context.Runtime;
            var answers = new List<bool>();
            var modal = (RemoteUiModal)peers.Host.Player(peers.First.Id).Ui.ShowModal(new UiModalRequest("Ready", "Start?"), answers.Add).Value!;
            peers.Rig.Frames(3);
            void Answer(MultiplayerPlayers from, ulong token, ulong revision, string value, int copies)
            {
                var writer = new WorldWireWriter(256);
                writer.Begin(14); writer.WriteString("players-test"); writer.WriteVarUInt(token); writer.WriteVarUInt(revision);
                writer.WriteString(""); writer.WriteString(value);
                for (var i = 0; i < copies; i++) from.Channel!.Send(writer.Segment, InternalSystemTarget.Host);
            }
            // Another player floods cancellations for this modal, far past the cancellation burst.
            for (var round = 0; round < 6; round++) { Answer(secondRuntime, modal.Token, 1, "0", 64); peers.Rig.Frames(2); }
            Answer(firstRuntime, modal.Token, 2, "0", 1);
            Answer(firstRuntime, modal.Token + 1000, 1, "0", 1);
            Answer(firstRuntime, modal.Token, 1, "2", 1);
            peers.Rig.Frames(3);
            That(answers.Count == 0 && modal.IsOpen, "another player, a wrong revision, a wrong token or a value other than 0 or 1 cannot cancel it");
            peers.FirstServices.Ui.Modals[0].Close();
            peers.Rig.Frames(3);
            That(answers.Count == 1 && !answers[0], "the target's own cancellation still arrives through the flood (" + answers.Count + ")");
            Answer(firstRuntime, modal.Token, 1, "0", 3);
            Answer(firstRuntime, modal.Token, 1, "1", 1);
            peers.Rig.Frames(3);
            That(answers.Count == 1, "a cancellation never runs twice (" + answers.Count + ")");
            var clicks = 0;
            peers.Host.Player(second.Id).Ui.CreateSurface(new UiSurfaceRequest("go", "Go", "", content: new UiButton("go", "Go", () => clicks++)));
            peers.Rig.Frames(3);
            ((UiButton)secondServices.Ui.Surfaces[0].Content!).Activated();
            peers.Rig.Frames(3);
            That(clicks == 1, "the flood spent only its sender's cancellation budget; its control replies still run (" + clicks + ")");
        }

        internal static void HealCreditKeepsToItsGap()
        {
            // Two heals at 40 of 100 can restore 60 between them, whatever the next report says.
            var damaged = new PresenceHealth(PresencePolicy.Default);
            damaged.Take(40, 100, 0);
            damaged.AllowGain(50);
            damaged.AllowGain(50);
            damaged.Take(30, 100, 0);
            damaged.Take(100, 100, 1);
            That(damaged.Health < 91, "two heals at 40 leave no more than the 60 they could restore for a false report after damage: " + damaged.Health);

            // Health that regeneration raised to a report the host held back retires credit it can no longer use.
            var held = new PresenceHealth(PresencePolicy.Default);
            held.Take(40, 100, 0);
            held.Take(100, 100, 0);
            held.AllowGain(10);
            held.Advance(2_400_000);
            held.Take(50, 100, 2_400_000);
            held.Take(100, 100, 2_400_001);
            That(held.Health < 51, "health regenerated toward a held report retires heal credit it can no longer use: " + held.Health);
        }
    }
}
