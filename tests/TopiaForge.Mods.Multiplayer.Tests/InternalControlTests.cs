extern alias provider;

using TopiaForge.Mods.Internal;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using LiveJoinTimings = provider::TopiaForge.Multiplayer.Network.LiveJoinTimings;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The internal multiplayer SPI over the provider: hosting, joining, leaving and moderation driven through
/// <c>IInternalMultiplayerControl</c>, with ModManager's runtime host attached and the composition root running every
/// frame, on the deterministic network with real cryptography, transport and engine.
/// </summary>
internal static class InternalControlTests
{
    private static readonly PackageSpec Sandbox = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a');

    internal static void PlayersHostJoinAndLeaveThroughTheSpi()
    {
        using var rig = new ControlRig(140, Sandbox);
        rig.Host.Runtime.PreferredDisplayName = "Hoster";
        That(rig.Host.Control.Role == InternalSessionRole.Solo && rig.Host.Control.IsLiveAvailable, "a fresh provider is solo with live sessions available");
        That(rig.Run(() => rig.Host.Session.Snapshot.Participants.Single().DisplayName == "Hoster"),
            "the attached runtime names the solo player");

        rig.StartHosting(new InternalHostSettings("Spi host", 4) { Gamemode = "Sandbox" });
        That(rig.Host.Control.Role == InternalSessionRole.ListenHost, "hosting makes this process a listen host");
        That(rig.Host.Platform.RunInBackground && rig.Host.Runtime.HoldsRunInBackground, "a host keeps running in the background");

        var guest = rig.AddPeer("guest", Sandbox);
        guest.Runtime.PreferredDisplayName = "Guest";
        rig.JoinAndWait(guest);
        var status = guest.Control.GetStatus();
        That(guest.Control.Role == InternalSessionRole.Client && status.Role == InternalSessionRole.Client, "the guest is a client");
        That(status.Server != null && status.Server.Name == "Spi host" && status.Server.Gamemode == "Sandbox" &&
             status.Server.Packages.Single().Id == Sandbox.Id && status.ConnectedPlayers == 2,
            "the guest sees the host's verified description and both players");
        That(guest.Runtime.HoldsRunInBackground, "a joined player keeps running in the background");
        That(rig.Run(() => rig.Host.Session.Snapshot.Participants.Any(participant => participant.DisplayName == "Guest")),
            "the host sees the guest by the runtime's display name");

        guest.Control.Leave();
        status = guest.Control.GetStatus();
        That(status.JoinPhase == InternalJoinPhase.Ended && status.JoinFailure == InternalLiveFailure.Left && guest.Control.Role == InternalSessionRole.Solo,
            "leaving returns the guest to solo play");
        That(rig.Run(() => !guest.Runtime.HoldsRunInBackground) && guest.Platform.RunInBackground, "and gives the game its own background setting back");
        That(rig.Run(() => rig.Host.Control.GetStatus().ConnectedPlayers == 1), "the host sees the guest go");

        rig.Host.Control.StopHosting();
        That(rig.Host.Control.Role == InternalSessionRole.Solo && rig.Host.Control.GetStatus().HostPhase == InternalHostPhase.Idle, "stopping returns the host to solo play");
        That(rig.Run(() => !rig.Host.Runtime.HoldsRunInBackground), "and restores the background setting");
    }

    internal static void HostsKickBanUnbanAndMuteThroughTheSpi()
    {
        using var rig = new ControlRig(141, Sandbox);
        rig.StartHosting(new InternalHostSettings("Moderated", 8));
        var first = rig.AddPeer("first", Sandbox);
        var second = rig.AddPeer("second", Sandbox);
        first.Runtime.PreferredDisplayName = "First";
        second.Runtime.PreferredDisplayName = "Second";
        rig.JoinAndWait(first);
        rig.JoinAndWait(second);
        That(rig.Run(() => rig.Host.Control.GetStatus().ConnectedPlayers == 3), "two players join");
        var firstId = rig.Host.IdOf("First");
        var secondId = rig.Host.IdOf("Second");
        var hostId = rig.Host.Session.Snapshot.LocalParticipantId!.Value;

        That(first.Control.Kick(secondId, "no").ErrorCode == ModErrorCode.NotAuthoritative, "a joined player cannot kick");
        That(rig.Host.Control.Kick(hostId, "no").ErrorCode == ModErrorCode.InvalidArgument, "the host cannot kick itself");
        That(rig.Host.Control.Kick(new ParticipantId("nobody"), "no").ErrorCode == ModErrorCode.NotFound, "kicking nobody finds nobody");
        That(rig.Host.Control.Kick(firstId, "Mind your manners.").Succeeded, "the host kicks a player");
        That(rig.Run(() => first.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended), "the kicked player's join ends");
        var kicked = first.Control.GetStatus();
        That(kicked.JoinFailure == InternalLiveFailure.Kicked && kicked.HostMessage == "Mind your manners." && first.Control.Role == InternalSessionRole.Solo,
            "and the player hears why in the host's words, back in solo play");
        That(kicked.JoinDetail == "The host removed this player from the session.", "while this game's own detail stays its own");

        // Server mute drops a player's chat when it reaches the host, so nothing the host relays can carry it.
        var hostChat = Open(rig.Host.Control, InternalSystemChannelKind.Chat);
        var secondChat = Open(second.Control, InternalSystemChannelKind.Chat);
        That(second.Control.SetServerMute(hostId, true).ErrorCode == ModErrorCode.NotAuthoritative, "a joined player cannot mute for everyone");
        That(rig.Host.Control.SetServerMute(hostId, true).ErrorCode == ModErrorCode.InvalidArgument, "the host cannot mute itself");
        That(rig.Host.Control.SetServerMute(secondId, true).Succeeded && rig.Host.Control.IsServerMuted(secondId), "the host mutes a player");
        That(secondChat.Send(Bytes("muted"), InternalSystemTarget.Host).Succeeded, "the muted player can still send");
        That(rig.Run(() => hostChat.GetStatistics().Dropped == 1), "but the host drops it at ingress");
        That(!hostChat.TryReceive(out _), "so the host never takes it in");
        That(rig.Host.Control.SetServerMute(secondId, false).Succeeded && !rig.Host.Control.IsServerMuted(secondId), "the host unmutes the player");
        That(secondChat.Send(Bytes("heard"), InternalSystemTarget.Host).Succeeded, "the player speaks again");
        That(rig.Run(() => hostChat.GetStatistics().Pending == 1) && hostChat.TryReceive(out var heard) &&
             heard.Sender.Equals(secondId) && Text(heard.Payload) == "heard", "and the host hears it from that player");

        That(rig.Host.Control.Ban(hostId).ErrorCode == ModErrorCode.InvalidArgument, "the host cannot ban itself");
        That(rig.Host.Control.Ban(secondId).Succeeded, "the host bans a player");
        That(rig.Run(() => second.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended) &&
             second.Control.GetStatus().JoinFailure == InternalLiveFailure.Banned, "the banned player is removed");
        var bans = rig.Host.Control.GetBans();
        That(bans.Count == 1 && bans[0].DisplayName == "Second" && bans[0].BanId.Length == 32, "the ban is listed by name");
        var bannedKey = rig.Host.Provider.Live!.Bans.Single().Identity;
        That(!bannedKey.Contains(bans[0].BanId, StringComparison.Ordinal) && !bans[0].BanId.Contains(bannedKey.Substring(0, 16), StringComparison.Ordinal),
            "under an id that is neither the banned key nor part of it");
        var refused = second.Control.Join(second.Target(), new InternalJoinSettings());
        That(refused.Succeeded && rig.Run(() => second.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended), "a banned player's next join ends");
        That(second.Control.GetStatus().JoinFailure == InternalLiveFailure.Banned && second.Control.GetStatus().RejectionCode == InternalAdmissionCode.Banned,
            "refused as banned");
        That(rig.Host.Control.Unban("0123456789abcdef0123456789abcdef").ErrorCode == ModErrorCode.NotFound, "an unknown ban id lifts nothing");
        That(rig.Host.Control.Unban(bans[0].BanId).Succeeded && rig.Host.Control.GetBans().Count == 0, "the host lifts the ban");
        rig.JoinAndWait(second);
        That(rig.Run(() => rig.Host.Session.Snapshot.Participants.Count(participant => participant.IsConnected && participant.DisplayName == "Second") == 1),
            "and the player is back");

        rig.Host.Control.SetServerMute(rig.Host.IdOf("Second"), true);
        rig.Host.Control.StopHosting();
        That(rig.Host.Control.GetStatistics().ServerMutes == 0, "mutes end with hosting");
    }

    internal static void PasswordJoinsWaitForThePlayerThroughTheSpi()
    {
        using var rig = new ControlRig(142, Sandbox);
        rig.StartHosting(new InternalHostSettings("Locked", 4) { Password = "open sesame" });
        var guest = rig.AddPeer("guest", Sandbox);
        That(guest.Control.SupplyPassword("early").ErrorCode == ModErrorCode.InvalidState, "nothing waits for a password before a join");
        That(guest.Control.Join(guest.Target(), new InternalJoinSettings()).Succeeded, "the join starts without a password");
        That(rig.Run(() => guest.Control.GetStatus().JoinPhase == InternalJoinPhase.AwaitingPassword), "and waits for one");
        That(guest.Control.SupplyPassword("bad\u0001").ErrorCode == ModErrorCode.InvalidArgument, "a password with control characters is refused");
        That(guest.Control.SupplyPassword("open says me").Succeeded, "the player types a wrong one");
        That(rig.Run(() => guest.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended) &&
             guest.Control.GetStatus().RejectionCode == InternalAdmissionCode.PasswordWrong, "which the host refuses");
        rig.JoinAndWait(guest, new InternalJoinSettings { Password = "open sesame" });
        That(rig.Host.Control.StartHosting(new InternalHostSettings("Twice", 4)).ErrorCode == ModErrorCode.InvalidState, "a host cannot host twice");
        That(guest.Control.StartHosting(new InternalHostSettings("Joined", 4)).ErrorCode == ModErrorCode.InvalidState, "a joined player cannot host");
        That(guest.Control.Join(guest.Target(), new InternalJoinSettings()).ErrorCode == ModErrorCode.InvalidState, "nor join twice");
        That(rig.Host.Control.Join(rig.Host.Target(), new InternalJoinSettings()).ErrorCode == ModErrorCode.InvalidState, "and a host cannot join elsewhere");
    }

    internal static void JoinsWaitForTheIdentityAndRetryAfterItFails()
    {
        using var rig = new ControlRig(146, Sandbox);
        rig.StartHosting(new InternalHostSettings("Patient", 4));
        var peer = new ControlPeer("unlucky", rig.Network, rig.Resolver, rig.PackageRoot, new[] { Sandbox });
        peer.Files.FailReads = true;
        That(peer.Composition.Connect(), "the runtime attaches, which starts loading the identity");
        rig.Peers.Add(peer);
        That(peer.Control.Join(peer.Target(), new InternalJoinSettings()).Succeeded &&
             peer.Control.GetStatus().JoinPhase == InternalJoinPhase.Resolving, "a join waits for the identity it proves");
        That(peer.Control.StartHosting(new InternalHostSettings("Meanwhile", 4)).ErrorCode == ModErrorCode.InvalidState,
            "a player waiting to join cannot host meanwhile");
        peer.Control.Leave();
        var left = peer.Control.GetStatus();
        That(left.JoinPhase == InternalJoinPhase.Ended && left.JoinFailure == InternalLiveFailure.Cancelled && left.JoinDetail.Length == 0,
            "leaving a join that has not started ends it as cancelled (" + left.JoinPhase + ", " + left.JoinFailure + ")");

        That(peer.Control.Join(peer.Target(), new InternalJoinSettings()).Succeeded, "the player joins again");
        That(rig.Run(() => peer.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended), "an identity that cannot load ends the join");
        That(peer.Control.GetStatus().JoinFailure == InternalLiveFailure.IdentityUnavailable && peer.Control.Role == InternalSessionRole.Solo,
            "saying why, with this player still solo");
        That(peer.Runtime.RecentDiagnostics.Any(record => record.Code == "live.identity-failed"), "and the runtime hears of it");
        peer.Files.FailReads = false;
        rig.JoinAndWait(peer);
        That(rig.Run(() => rig.Host.Control.GetStatus().ConnectedPlayers == 2), "the next join loads the identity again and gets in");
    }

    internal static void SessionControlsAnswerForTheirRole()
    {
        using var rig = new ControlRig(143, Sandbox);
        var soloId = rig.Host.Session.Snapshot.Id;
        That(rig.Host.Control.ReplaceSession().Succeeded && !rig.Host.Session.Snapshot.Id.Equals(soloId), "a solo player replaces its own session");
        rig.StartHosting(new InternalHostSettings("Replacing", 4));
        var guest = rig.AddPeer("guest", Sandbox);
        rig.JoinAndWait(guest);
        That(guest.Control.ReplaceSession().ErrorCode == ModErrorCode.NotAuthoritative, "a joined player cannot replace the session");
        var hosted = rig.Host.Session.Snapshot.Id;
        That(rig.Host.Control.ReplaceSession().Succeeded, "the host replaces the session");
        That(rig.Run(() => guest.Session.Snapshot.Id.Equals(rig.Host.Session.Snapshot.Id) && !guest.Session.Snapshot.Id.Equals(hosted) &&
                           guest.Session.Snapshot.State == MultiplayerSessionState.Ready),
            "and the joined player follows it, still connected");
        That(guest.Control.GetStatus().JoinPhase == InternalJoinPhase.InSession, "without leaving");

        var invite = guest.Target("topiaforge://join/ABCDE12345");
        That(invite.Kind == InternalJoinTargetKind.Invite, "an invite link makes an invite target");
        var refused = rig.Host.Control.Join(invite, new InternalJoinSettings());
        That(refused.ErrorCode == ModErrorCode.InvalidState, "a host is refused before the target is even looked at");
        guest.Control.Leave();
        That(guest.Control.Join(invite, new InternalJoinSettings()).ErrorCode == ModErrorCode.Unavailable, "an invite needs the TopiaForge service");
        using var query = guest.Control.Query(invite, false);
        That(query.IsCompleted && query.Failure == InternalLiveFailure.ServiceUnavailable, "and so does looking it up");

        var standalone = rig.AddPeer("standalone", Sandbox, PackageSpec.Standalone("io.example.solo", "1.0.0", 'c'));
        var hosting = standalone.Control.StartHosting(new InternalHostSettings("Never", 4));
        That(hosting.ErrorCode == ModErrorCode.InvalidState && hosting.ErrorMessage.Contains("Solo", StringComparison.Ordinal),
            "a standalone mod refuses hosting and is named");
    }

    internal static void QueriesLookAtAServerWithoutJoining()
    {
        using var rig = new ControlRig(144, Sandbox);
        rig.StartHosting(new InternalHostSettings("Browsable", 6) { World = "Arena" });
        var fits = rig.AddPeer("fits", Sandbox);
        using (var query = fits.Control.Query(fits.Target(), false))
        {
            That(rig.Run(() => query.IsCompleted), "the lookup completes");
            That(query.Failure == InternalLiveFailure.None && query.Server!.Name == "Browsable" && query.Server.World == "Arena" &&
                 query.Server.MaxPlayers == 6 && query.ContentProblems.Count == 0, "it describes the server and finds this game fits");
            Exception? offThread = null;
            var reader = new Thread(() =>
            {
                try
                {
                    _ = query.IsCompleted;
                }
                catch (InvalidOperationException exception)
                {
                    offThread = exception;
                }
            });
            reader.Start();
            reader.Join();
            That(offThread != null, "a query, like the rest of the interface, answers only on the main thread");
        }

        var differs = rig.AddPeer("differs", PackageSpec.Implicit(Sandbox.Id, "0.2.1", 'b'));
        using (var query = differs.Control.Query(differs.Target(), false))
        {
            That(rig.Run(() => query.IsCompleted) && query.Failure == InternalLiveFailure.ContentMismatch, "a game whose content differs is told before joining");
            That(query.ContentProblems.SequenceEqual(new[] { "Sandbox: the host runs 0.2.0; this game has 0.2.1." }), "with what differs (" + string.Join(" | ", query.ContentProblems) + ")");
            That(query.Server != null, "and still sees the description");
        }

        That(rig.Host.Control.GetStatus().ConnectedPlayers == 1, "and nothing ever connected");
    }

    internal static void ReadinessHoldsKeepAJoiningPlayerSynchronizing()
    {
        // The host's two-second deadline, with holds allowed far longer: the holds' own bounds are tested below.
        using var rig = new ControlRig(145, Sandbox) { Timings = new LiveJoinTimings(2_000_000, 30_000_000, 60_000_000) };
        rig.StartHosting(new InternalHostSettings("Following", 4));
        using (rig.Host.Control.HoldReadiness("host world"))
        {
            That(rig.Host.Control.IsReadinessHeld && rig.Host.Session.Snapshot.State == MultiplayerSessionState.Ready, "a host's hold changes nothing");
        }

        var guest = rig.AddPeer("guest", Sandbox);
        guest.Runtime.PreferredDisplayName = "Guest";
        var hold = guest.Control.HoldReadiness("loading the host's world");
        That(guest.Control.Join(guest.Target(), new InternalJoinSettings()).Succeeded, "the guest joins while holding");
        That(rig.Run(() => guest.Session.Snapshot.State == MultiplayerSessionState.Synchronizing &&
                           rig.Host.Session.Snapshot.Participants.Any(participant => participant.DisplayName == "Guest")),
            "the host admits the guest");
        That(!rig.Run(() => guest.Session.Snapshot.State == MultiplayerSessionState.Ready, 5_000_000), "the guest stays short of Ready past the host's deadline");
        That(guest.Control.GetStatus().JoinPhase == InternalJoinPhase.Synchronizing && guest.Control.GetStatus().IsReadinessHeld,
            "the join waits, synchronizing, and is not timed out");
        var guestId = rig.Host.IdOf("Guest");
        That(!rig.Host.Control.IsParticipantReady(guestId), "the host is not told the guest is ready");
        hold.Dispose();
        That(rig.Run(() => guest.Control.GetStatus().JoinPhase == InternalJoinPhase.InSession), "releasing the hold completes the join");
        That(rig.Run(() => rig.Host.Control.IsParticipantReady(guestId)) && guest.Control.IsParticipantReady(guest.Session.Snapshot.LocalParticipantId!.Value),
            "and both sides see the guest ready");
        Throws<ArgumentException>(() => guest.Control.HoldReadiness(" "), "a hold names its reason");
    }

    internal static void ReadinessHoldsAreBounded()
    {
        // A hold may go two seconds without progress and six in all; production allows a minute and five.
        using var rig = new ControlRig(148, Sandbox) { Timings = new LiveJoinTimings(10_000_000, 2_000_000, 6_000_000) };
        rig.StartHosting(new InternalHostSettings("Withholding", 6));

        // The host sends its snapshot and then nothing: without progress, the join ends at the idle bound.
        var idle = rig.AddPeer("idle", Sandbox);
        var stuck = idle.Control.HoldReadiness("loading a world that never comes");
        That(idle.Control.Join(idle.Target(), new InternalJoinSettings()).Succeeded, "the idle player joins while holding");
        That(rig.Run(() => idle.Control.GetStatus().JoinPhase == InternalJoinPhase.Synchronizing && idle.Control.IsReadinessHeld &&
                           idle.Provider.Engine.IsAwaitingLocalReadiness), "and waits for its runtime once the snapshot is here");
        var heldAt = rig.Network.Now;
        That(rig.Run(() => idle.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended, 4_000_000), "a hold without progress ends the join");
        var idleStatus = idle.Control.GetStatus();
        That(idleStatus.JoinFailure == InternalLiveFailure.TimedOut && idleStatus.JoinDetail == "The session did not finish loading in time." &&
             idle.Control.Role == InternalSessionRole.Solo && rig.Network.Now - heldAt >= 1_900_000,
            "as timed out, back in solo play, at the idle bound (" + (rig.Network.Now - heldAt) + " us)");
        stuck.Dispose();

        // Progress keeps a hold alive past the idle bound, but never past the limit.
        var patient = rig.AddPeer("patient", Sandbox);
        var loading = patient.Control.HoldReadiness("loading a large world");
        That(patient.Control.Join(patient.Target(), new InternalJoinSettings()).Succeeded, "the patient player joins while holding");
        That(rig.Run(() => patient.Provider.Engine.IsAwaitingLocalReadiness), "and waits for its runtime");
        var patientAt = rig.Network.Now;
        That(rig.Run(() =>
        {
            loading.ReportProgress();
            return patient.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended;
        }, 10_000_000), "a hold that keeps reporting progress still ends");
        var elapsed = rig.Network.Now - patientAt;
        That(patient.Control.GetStatus().JoinFailure == InternalLiveFailure.TimedOut && elapsed >= 5_900_000 && elapsed < 7_000_000,
            "at the limit, not the idle bound (" + elapsed + " us)");
        loading.Dispose();

        // A session the host replaces is bounded the same way when the player resynchronizes.
        var follower = rig.AddPeer("follower", Sandbox);
        rig.JoinAndWait(follower);
        var next = follower.Control.HoldReadiness("loading the next world");
        That(rig.Host.Control.ReplaceSession().Succeeded, "the host moves to a new world");
        That(rig.Run(() => follower.Provider.Engine.IsAwaitingLocalReadiness), "the follower resynchronizes and waits for its runtime");
        That(rig.Run(() => follower.Control.GetStatus().JoinPhase == InternalJoinPhase.Ended, 4_000_000) &&
             follower.Control.GetStatus().JoinFailure == InternalLiveFailure.TimedOut, "and a resynchronization without progress ends as timed out too");
        next.Dispose();
    }

    internal static IInternalSystemChannel Open(IInternalMultiplayerControl control, InternalSystemChannelKind kind)
    {
        var opened = control.OpenSystemChannel(kind);
        if (!opened.TryGetValue(out var channel)) throw new InvalidOperationException("Assertion failed: the " + kind + " channel opens (" + opened.ErrorMessage + ")");
        return channel;
    }

    internal static ArraySegment<byte> Bytes(string text) => new(System.Text.Encoding.UTF8.GetBytes(text));

    internal static string Text(byte[] payload) => System.Text.Encoding.UTF8.GetString(payload);
}
