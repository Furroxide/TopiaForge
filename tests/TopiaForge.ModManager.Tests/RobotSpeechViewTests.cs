using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static class RobotSpeechViewTests
    {
        internal static void Run()
        {
            Admission();
            OfflineHideSurvivesPromotion();
            ActiveHide();
            LeaseLifetimes();
            Console.WriteLine("Robot speech views: admission, offline promotion, active playback and lease lifetimes passed.");
        }

        private static void Admission()
        {
            using var rig = new RobotSpeechRig(2);
            var hiddenGuest = rig.Guests[0];
            var visibleGuest = rig.Guests[1];
            using var hostView = new View(rig.Host);
            using var hiddenView = new View(hiddenGuest);
            using var visibleView = new View(visibleGuest);
            using var hostHide = Hide(rig, hostView, rig.Host);
            using var guestHide = Hide(rig, hostView, hiddenGuest);
            Check(!rig.Canonical.Visible && hiddenGuest.Body.Visible,
                "the host view is already hidden, while the remote hide has not reached the guest");

            rig.Log.Clear();
            const string text = "A private line for the visible audience.";
            var call = rig.Robot.SayAsync(text);
            var sent = rig.Log.Where(packet => Encoding.UTF8.GetString(packet.Payload).Contains(text, StringComparison.Ordinal)).ToArray();
            Check(sent.Length == 1 && sent[0].To.Equals(visibleGuest.Peer.Control.Id) && sent[0].Payload[1] == RobotSpeechWire.Line,
                "only the visible peer receives a packet containing the line, even before projection");
            rig.World.Frames(8);
            Check(!hiddenGuest.Body.Visible && visibleGuest.Body.Visible,
                "the production view stream projects the same hide only to the selected guest");
            Check(rig.Canonical.SpeechPlays == 0 && hiddenGuest.Body.SpeechPlays == 0 && visibleGuest.Body.SpeechPlays == 1,
                "a host view hide preserves remote synthesis without playing locally or for a hidden guest");
            visibleGuest.Body.SpeechCompletions.Single().SetResult(OperationResult<bool>.Success(true));
            rig.Later();
            Check(call.IsCompletedSuccessfully && call.Result.Succeeded && call.Result.Value,
                "a hidden listener is excluded from completion as well as text");

            hostHide.Dispose();
            guestHide.Dispose();
            Check(rig.World.Run(() => hiddenGuest.Body.Visible) && rig.Canonical.Visible, "releasing both leases restores the views");
            Check(hiddenGuest.Body.SpeechPlays == 0, "release never replays the private line");
            rig.Robot.SayAsync("A new line after restoration.");
            rig.World.Frames(8);
            Check(rig.Canonical.SpeechPlays == 1 && hiddenGuest.Body.SpeechPlays == 1 && visibleGuest.Body.SpeechPlays == 2,
                "both restored viewers receive fresh speech");
        }

        private static void OfflineHideSurvivesPromotion()
        {
            using var rig = new RobotSpeechRig(players: 0, open: false);
            using var hostView = new View(rig.Host);
            Check(!rig.Host.Peer.Replication.TryCreateRef(rig.Robot, out _), "the routed robot has no network name offline");
            var result = rig.Owner.Seam.WorldView(hostView).Hide(rig.Robot);
            Check(result.Succeeded, result.ErrorMessage);
            using var hide = result.Value!;
            Check(!rig.Canonical.Visible, "the offline shared lease hides the local body");

            rig.World.OpenToMultiplayer();
            var guest = rig.Add("listener");
            Check(hide.IsActive && rig.Host.Peer.Replication.TryCreateRef(rig.Robot, out _) && guest.Body.Visible,
                "hosting retains the shared lease and adopts the robot before the guest binds a view");
            rig.Log.Clear();
            var call = rig.Robot.SayAsync("A line from a robot hidden before hosting.");
            Check(rig.Log.All(packet => packet.Payload[1] != RobotSpeechWire.Line),
                "the host excludes a guest before projecting an offline lease registered without a network name");
            rig.Later();
            Check(call.IsCompletedSuccessfully && call.Result.Succeeded && !call.Result.Value && guest.Body.SpeechPlays == 0,
                "the retained lease leaves no admitted audience");

            using var guestView = new View(guest);
            Check(rig.World.Run(() => !guest.Body.Visible), "the retained offline lease also hides the guest's replica");
            hide.Dispose();
            Check(rig.World.Run(() => guest.Body.Visible) && rig.Canonical.Visible, "releasing the promoted lease restores both viewers");
            rig.Robot.SayAsync("A fresh line after releasing the offline lease.");
            rig.World.Frames(8);
            Check(guest.Body.SpeechPlays == 1, "the promoted robot can speak to the restored guest");
        }

        private static void ActiveHide()
        {
            using var rig = new RobotSpeechRig(2);
            var hiddenGuest = rig.Guests[0];
            var visibleGuest = rig.Guests[1];
            using var hostView = new View(rig.Host);
            using var hiddenView = new View(hiddenGuest);
            using var visibleView = new View(visibleGuest);
            var call = rig.Robot.SayAsync("An active line.");
            rig.World.Frames(5);
            var local = rig.Canonical.SpeechTokens.Single();
            var hidden = hiddenGuest.Body.SpeechTokens.Single();
            var visible = visibleGuest.Body.SpeechTokens.Single();
            rig.Log.Clear();

            using var hostHide = Hide(rig, hostView, rig.Host);
            rig.World.Frames(5);
            Check(local.IsCancellationRequested && !hidden.IsCancellationRequested && !visible.IsCancellationRequested,
                "an active host view hide cancels only local playback");
            Check(rig.Log.All(packet => packet.Payload[1] != RobotSpeechWire.Cancel),
                "the host view hide sends no remote cancellation");

            using var guestHide = Hide(rig, hostView, hiddenGuest);
            rig.World.Frames(8);
            Check(hidden.IsCancellationRequested && !hiddenGuest.Body.Visible && !visible.IsCancellationRequested,
                "a guest view hide revokes its active playback while the visible guest continues");
            var cancelled = rig.Log.Where(packet => packet.Payload[1] == RobotSpeechWire.Cancel).ToArray();
            Check(cancelled.Length == 1 && cancelled[0].To.Equals(hiddenGuest.Peer.Control.Id),
                "only the revoked guest receives a cancellation");
            rig.Later();
            Check(!call.IsCompleted, "the call still waits for the visible guest");
            visibleGuest.Body.SpeechCompletions.Single().SetResult(OperationResult<bool>.Success(true));
            rig.World.Frames(5);
            Check(call.IsCompletedSuccessfully && !call.Result.Succeeded,
                "revocation keeps the existing failure result for interrupted playback");
        }

        private static void LeaseLifetimes()
        {
            using var rig = new RobotSpeechRig();
            var guest = rig.Guests[0];
            using var hostView = new View(rig.Host);
            using var guestView = new View(guest);
            var viewOwner = new AuthorityMod(rig.Host.Peer, ModExecutionContext.GamemodeServer);
            using var viewLifetime = viewOwner.Lifetime;
            using var guestHide = Hide(rig, hostView, guest, viewOwner);
            Check(rig.World.Run(() => !guest.Body.Visible), "a separately owned lease projects to the guest");
            viewLifetime.Dispose();
            Check(!guestHide.IsActive && rig.World.Run(() => guest.Body.Visible),
                "stopping the view owner releases its hide independently of the speech owner");

            rig.Canonical.Visible = false;
            using var localHide = Hide(rig, hostView, rig.Host);
            rig.Log.Clear();
            var refused = rig.Robot.SayAsync("A canonically invisible source.");
            Check(refused.IsCompletedSuccessfully && refused.Result.ErrorCode == ModErrorCode.Unavailable
                && rig.Log.All(packet => packet.Payload[1] != RobotSpeechWire.Line),
                "an originally disabled renderer keeps its source ineligible for every viewer");
            localHide.Dispose();
            Check(!rig.Canonical.Visible, "releasing a hide preserves an originally disabled renderer");
            rig.Canonical.Visible = true;
            rig.Robot.SayAsync("Speech remains owned after view owner retirement.");
            rig.World.Frames(5);
            Check(guest.Body.SpeechPlays == 1, "a fresh line can reach the restored guest");

            using var hostHide = Hide(rig, hostView, rig.Host);
            using var sessionHide = Hide(rig, hostView, guest);
            Check(rig.World.Run(() => !guest.Body.Visible), "session-owned projection applies the hide");
            guest.Peer.Runtime.Disconnect();
            rig.Host.Peer.Runtime.Disconnect();
            Check(!hostHide.IsActive && !sessionHide.IsActive && rig.Canonical.Visible
                && !OwnedRendererVisibility<FirstPartyRobot>.TryGetOriginal(guest.Body, out _),
                "detaching the session retires the host leases and guest renderer hold");
        }

        private static IViewLease Hide(RobotSpeechRig rig, View hostView, RobotSpeechRig.SpeechPeer viewer, AuthorityMod? owner = null)
        {
            // The routed handle passed to Hide differs from the provider body admitted for speech.
            var result = (owner ?? rig.Owner).Seam.PlayerWorldView(hostView, viewer.Peer.Control.Id.Value).Hide(rig.Robot);
            Check(result.Succeeded, result.ErrorMessage);
            return result.Value!;
        }

        private static void Check(bool condition, string reason)
        {
            if (!condition) throw new InvalidOperationException("Robot speech views: " + reason);
        }

        // Only the renderer flags are simulated. Runtime leases, canonical target references and the
        // per-player view and speech channels all run their production paths.
        private sealed class View : IInternalWorldViewRenderer, IInternalWorldViewBinding
        {
            private readonly RobotSpeechRig.SpeechPeer peer;
            private readonly OwnedRendererVisibility<FirstPartyRobot> visibility =
                new OwnedRendererVisibility<FirstPartyRobot>(robot => robot.IsAlive, robot => robot.Visible, (robot, enabled) => robot.Visible = enabled);
            internal View(RobotSpeechRig.SpeechPeer peer)
            {
                this.peer = peer;
                peer.Peer.Replication.Views.Activate(this);
            }
            public string WorldId => peer.CurrentWorld!;
            public IInternalWorldViewRenderer Renderer => this;
            public bool IsAlive { get; private set; } = true;
            public bool FogAvailable => false;
            public bool EnvironmentAvailable => false;
            public IReadOnlyList<string> Presets => Array.Empty<string>();
            public event Action Stopped = delegate { };
            public bool HasPreset(string preset) => false;
            public OperationResult<bool> SetEnvironment(string? preset) => OperationResult<bool>.Success(true);
            public OperationResult<bool> SetFog(VisionFog? fog) => OperationResult<bool>.Success(true);
            public bool TryNameAuthored(IEntity entity, out string scene, out string id) { scene = id = ""; return false; }
            public IEntity? ResolveAuthored(string scene, string id) => null;
            public OperationResult<IDisposable> Hide(IEntity entity)
            {
                var body = peer.Peer.Replication.Views.ResolveProviderEntity(entity, this) as FirstPartyRobot;
                if (body == null && peer.Peer.Control.IsHost) peer.Robots.Agents.TryGetValue(entity.Id, out body);
                return body != null
                    ? OperationResult<IDisposable>.Success(visibility.Hide(new[] { body }))
                    : OperationResult<IDisposable>.Failure(ModErrorCode.NotFound, "No renderer for this body.");
            }
            public void Dispose()
            {
                if (!IsAlive) return;
                IsAlive = false;
                try { Stopped(); }
                finally { visibility.Dispose(); }
            }
        }
    }
}
