extern alias provider;

using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Hosting and joining end to end, with real crypto, transport, engine and flows on the deterministic network: every
/// way a join is admitted or refused, the password, kick, ban, stop, privacy and reconnecting.
/// </summary>
internal static class LiveSessionTests
{
    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddCommandType = new("add");

    internal static void APlayerJoinsAHostAndLeaves()
    {
        using var rig = new LiveRig(130);
        var hostScore = Register(rig.Host);
        Run(rig, rig.Host.SubmitAsync(AddCommandType, new AddRequest(3)), "the host scores in solo play");
        rig.StartHosting(new HostSettings("Rig host", 4) { Port = 0 });
        That(rig.Host.Live!.HostPhase == HostPhase.Hosting && rig.Host.Live.Host!.Descriptor!.Descriptor.Name == "Rig host",
            "the host serves a signed descriptor");
        That(rig.Host.Snapshot.Participants.Count == 1 && hostScore.Value.Value == 3, "opening the session keeps it as it was");

        var guest = rig.AddClient("guest");
        var guestScore = Register(guest);
        var soloId = guest.Snapshot.Id;
        Run(rig, guest.SubmitAsync(AddCommandType, new AddRequest(9)), "the guest plays solo first");
        var join = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => join.Phase == JoinPhase.InSession), "the guest joins (" + join.Phase + ", " + join.Failure + ": " + join.Detail + ")");
        That(guest.Snapshot.Participants.Count == 2 && !guest.Snapshot.Id.Equals(soloId) && guestScore.Value.Value == 3,
            "and sees the host's session and values (" + guest.Snapshot.Participants.Count + " players, " + guest.Snapshot.Id + ", score " + guestScore.Value.Value + ")");
        var hostView = rig.Host.Snapshot.Participants.Single(participant => !participant.IsHost);
        That(hostView.IsConnected && hostView.PlayerKey.IsValid && hostView.DisplayName == "Guest", "the host sees the guest with a per-host player key");

        var command = guest.SubmitAsync(AddCommandType, new AddRequest(1));
        That(rig.Run(() => command.IsCompleted && guestScore.Value.Value == 4) && hostScore.Value.Value == 4, "a command runs once on the host");

        join.Leave();
        That(join.Phase == JoinPhase.Ended && join.Failure == LiveFailure.Left && guest.Snapshot.IsSoloHost(), "leaving returns the guest to solo play");
        That(rig.Run(() => rig.Host.Snapshot.Participants.Count(participant => participant.IsConnected) == 1), "and the host sees the guest go");
        That(rig.Network.Spins == 0, "no busy loop");
    }

    internal static void PasswordsAreProvedNeverSent()
    {
        using var rig = new LiveRig(131);
        Register(rig.Host);
        rig.StartHosting(new HostSettings("Locked", 4) { Port = 0, Password = "open sesame" });
        That(rig.Host.Live!.Host!.Descriptor!.Descriptor.PasswordRequired, "the descriptor says a password is needed");
        var sent = new List<byte[]>();
        rig.Network.Observe = (_, _, bytes) => sent.Add(bytes);

        var guest = rig.AddClient("guest");
        Register(guest);
        var soloId = guest.Snapshot.Id;
        var join = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => join.Phase == JoinPhase.AwaitingPassword), "a join without a password waits for one");
        That(guest.Snapshot.Id.Equals(soloId), "while the solo session carries on");
        join.SupplyPassword("open says me");
        That(rig.Run(() => join.Phase == JoinPhase.Ended), "a wrong password is refused");
        That(join.Failure == LiveFailure.Rejected && join.RejectionCode == AdmissionCode.PasswordWrong && guest.Snapshot.IsSoloHost(),
            "with the reason, and the guest is back in solo play");

        var second = rig.Join(guest, new JoinSettings("Guest") { Password = "open sesame" });
        That(rig.Run(() => second.Phase == JoinPhase.InSession), "the right password joins (" + second.Failure + ")");
        var password = System.Text.Encoding.UTF8.GetBytes("open sesame");
        That(!sent.Any(bytes => Contains(bytes, password)), "the password never crossed the network in the clear");
        second.Leave();

        // Five wrong proofs from one identity within a minute stop the sixth before it is even checked; one is already spent.
        for (var attempt = 1; attempt < HostPolicy.AttemptsPerMinute; attempt++)
        {
            var wrong = rig.Join(guest, new JoinSettings("Guest") { Password = "guess " + attempt });
            That(rig.Run(() => wrong.Phase == JoinPhase.Ended) && wrong.RejectionCode == AdmissionCode.PasswordWrong, "wrong guess " + attempt);
        }

        var limited = rig.Join(guest, new JoinSettings("Guest") { Password = "open sesame" });
        That(rig.Run(() => limited.Phase == JoinPhase.Ended) && limited.RejectionCode == AdmissionCode.TooManyAttempts,
            "after five wrong guesses even the right password waits out the minute (" + limited.RejectionCode + ")");
    }

    internal static void HostsKickBanAndStop()
    {
        using var rig = new LiveRig(132);
        Register(rig.Host);
        rig.StartHosting(new HostSettings("Moderated", 8) { Port = 0 });
        var first = rig.AddClient("first");
        var second = rig.AddClient("second");
        var third = rig.AddClient("third");
        var joins = new[] { first, second, third }.Select(client =>
        {
            Register(client);
            return rig.Join(client, new JoinSettings(client == first ? "First" : client == second ? "Second" : "Third"));
        }).ToArray();
        That(rig.Run(() => joins.All(join => join.Phase == JoinPhase.InSession)), "three players join");
        That(rig.Host.Snapshot.Participants.Count(participant => participant.IsConnected) == 4, "the host sees all three");

        var firstId = rig.Host.Snapshot.Participants.Single(participant => participant.DisplayName == "First").Id;
        That(rig.Host.Live!.Host!.Kick(firstId, "Mind your manners."), "the host kicks a player");
        That(rig.Run(() => joins[0].Phase == JoinPhase.Ended) && joins[0].Failure == LiveFailure.Kicked && joins[0].Detail == "Mind your manners." &&
             first.Snapshot.IsSoloHost(), "the kicked player hears why and returns to solo play");

        var secondId = rig.Host.Snapshot.Participants.Single(participant => participant.DisplayName == "Second").Id;
        That(rig.Host.Live.Ban(secondId, "Second"), "the host bans a player");
        That(rig.Run(() => joins[1].Phase == JoinPhase.Ended) && joins[1].Failure == LiveFailure.Banned, "the banned player is removed");
        That(rig.Run(() => rig.HostFiles.Contents.ContainsKey(BanList.FilePath)) &&
             BanList.Parse(rig.HostFiles.Contents[BanList.FilePath]).Count == 1, "the ban is saved");
        var again = rig.Join(second, new JoinSettings("Second"));
        That(rig.Run(() => again.Phase == JoinPhase.Ended) && again.Failure == LiveFailure.Banned && again.RejectionCode == AdmissionCode.Banned,
            "a banned identity cannot come back");
        var kickedAgain = rig.Join(first, new JoinSettings("First"));
        That(rig.Run(() => kickedAgain.Phase == JoinPhase.InSession), "a kicked player may rejoin");

        rig.Host.Live.StopHosting();
        That(rig.Run(() => joins[2].Phase == JoinPhase.Ended && kickedAgain.Phase == JoinPhase.Ended) &&
             joins[2].Failure == LiveFailure.HostEnded && kickedAgain.Failure == LiveFailure.HostEnded, "stopping ends everyone's session");
        That(rig.Host.Snapshot.Participants.Count == 1 && rig.Host.Live.HostPhase == HostPhase.Idle, "and the host plays on alone");
    }

    internal static void JoinsCheckBuildContentCapacityAndPrivacy()
    {
        using var rig = new LiveRig(133);
        Register(rig.Host);
        rig.StartHosting(new HostSettings("Picky", 2) { Port = 0 });

        var oldGame = rig.AddClient("old", new TestContent("2478"));
        Register(oldGame);
        var oldSolo = oldGame.Snapshot.Id;
        var wrongBuild = rig.Join(oldGame, new JoinSettings("Old"));
        That(rig.Run(() => wrongBuild.Phase == JoinPhase.Ended) && wrongBuild.Failure == LiveFailure.WrongGameBuild && oldGame.Snapshot.Id.Equals(oldSolo),
            "another game build is refused before anything connects, and the solo session is untouched");

        var missing = rig.AddClient("missing", new TestContent { Missing = new[] { "io.example.sandbox 0.2.0 is not installed" } });
        Register(missing);
        var content = rig.Join(missing, new JoinSettings("Missing"));
        That(rig.Run(() => content.Phase == JoinPhase.Ended) && content.Failure == LiveFailure.ContentMismatch && content.Content!.Problems.Count == 1,
            "missing packages stop the join and say which");

        rig.Resolver.Kind = TransportPathKind.Direct;
        var direct = rig.AddClient("direct");
        Register(direct);
        var hidden = rig.Join(direct, new JoinSettings("Direct"));
        That(rig.Run(() => hidden.Phase == JoinPhase.Ended) && hidden.Failure == LiveFailure.DirectNotAllowed && rig.Resolver.LastAllowDirect == false,
            "a direct path outside the LAN needs the player's consent for this session, and the resolver is told it has none");
        var refused = rig.Join(direct, new JoinSettings("Direct") { AllowDirect = true });
        That(rig.Run(() => refused.Phase == JoinPhase.Ended, 20_000_000) && refused.Failure != LiveFailure.None && refused.Failure != LiveFailure.Left,
            "and a relay-only host never answers on one (" + refused.Failure + ")");
        rig.Resolver.Kind = TransportPathKind.Lan;

        var fits = rig.AddClient("fits");
        Register(fits);
        var fitsJoin = rig.Join(fits, new JoinSettings("Fits"));
        That(rig.Run(() => fitsJoin.Phase == JoinPhase.InSession), "the first guest fits");
        var overflow = rig.AddClient("overflow");
        Register(overflow);
        var full = rig.Join(overflow, new JoinSettings("Overflow"));
        That(rig.Run(() => full.Phase == JoinPhase.Ended) && full.Failure == LiveFailure.Rejected && full.RejectionCode == AdmissionCode.Full,
            "a full session refuses the next (" + full.RejectionCode + ")");

        rig.Resolver.Override = ResolvedTarget.Failed(LiveFailure.HostIdentityChanged, "This address served a different server last time.");
        var changed = rig.Join(overflow, new JoinSettings("Overflow"));
        That(rig.Run(() => changed.Phase == JoinPhase.Ended) && changed.Failure == LiveFailure.HostIdentityChanged, "a changed identity stops the join");
        rig.Resolver.Override = null;
    }

    internal static void ALostLinkReconnectsWithinTheGrace()
    {
        using var rig = new LiveRig(134);
        var hostScore = Register(rig.Host);
        rig.StartHosting(new HostSettings("Flaky", 4) { Port = 0 });
        var guest = rig.AddClient("guest");
        var guestScore = Register(guest);
        var join = rig.Join(guest, new JoinSettings("Guest"));
        That(rig.Run(() => join.Phase == JoinPhase.InSession), "the guest joins");
        var participant = guest.Snapshot.LocalParticipantId;
        var hostNode = rig.HostFactory.Networks[^1].Node;
        var guestNode = rig.NodeOf(guest);

        rig.Network.Partition(hostNode, guestNode, cut: true);
        That(rig.Run(() => join.Phase == JoinPhase.Reconnecting, 20_000_000), "a long silence makes the guest reconnect");
        rig.Network.Partition(hostNode, guestNode, cut: false);
        That(rig.Run(() => join.Phase == JoinPhase.InSession, 20_000_000), "the guest gets back in");
        That(guest.Snapshot.LocalParticipantId.Equals(participant), "as the same participant, within the host's grace");
        var command = guest.SubmitAsync(AddCommandType, new AddRequest(2));
        That(rig.Run(() => command.IsCompleted && guestScore.Value.Value == 2) && hostScore.Value.Value == 2, "and plays on");

        rig.Network.Partition(hostNode, rig.NodeOf(guest), cut: true);
        That(rig.Run(() => join.Phase == JoinPhase.Ended, 90_000_000), "a link that stays lost ends the session");
        That(join.Failure == LiveFailure.ConnectionLost && guest.Snapshot.IsSoloHost(), "and the guest is back in solo play");
    }

    internal static void HostingRecoversSavesInOrderAndBrowsesPrivately()
    {
        // An identity that cannot be read fails hosting, and the player can simply try again once it can.
        using var rig = new LiveRig(135);
        Register(rig.Host);
        rig.HostFiles.FailReads = true;
        rig.Host.Live!.StartHosting(new HostSettings("Recovering") { Port = 0 });
        That(rig.Run(() => rig.Host.Live.HostPhase == HostPhase.Failed) && rig.Host.Live.HostFailure == LiveFailure.IdentityUnavailable,
            "an unreadable identity fails hosting");
        rig.HostFiles.FailReads = false;
        rig.StartHosting(new HostSettings("Recovering") { Port = 0 });
        That(rig.Host.Live.HostPhase == HostPhase.Hosting, "and hosting starts once it can be read");

        // Two bans close together, the first saved slowly: it must not land after, and undo, the second.
        var first = rig.AddClient("first");
        var second = rig.AddClient("second");
        var joins = new[] { first, second }.Select(client =>
        {
            Register(client);
            return rig.Join(client, new JoinSettings(client == first ? "First" : "Second"));
        }).ToArray();
        That(rig.Run(() => joins.All(join => join.Phase == JoinPhase.InSession)), "two players join");
        var release = new TaskCompletionSource();
        var writes = 0;
        rig.HostFiles.BeforeWrite = path => path == BanList.FilePath && Interlocked.Increment(ref writes) == 1 ? release.Task : Task.CompletedTask;
        rig.Host.Live.Ban(rig.Host.Snapshot.Participants.Single(participant => participant.DisplayName == "First").Id, "First");
        That(rig.Run(() => Volatile.Read(ref writes) == 1), "the first ban starts saving");
        rig.Host.Live.Ban(rig.Host.Snapshot.Participants.Single(participant => participant.DisplayName == "Second").Id, "Second");
        rig.Run(() => Volatile.Read(ref writes) == 2, 200_000);
        release.SetResult();
        That(rig.Run(() => Volatile.Read(ref writes) == 2 && Saved(rig) == 2), "the file ends with both bans (" + Saved(rig) + ")");

        // Browsing the LAN never answers a beacon from beyond it with this player's address.
        first.Live!.StartBrowsing(0);
        That(rig.Factories[first].Networks[^1].Stack.Endpoint.Options.RelayOnly, "a LAN browser is relay-only");
    }

    private static int Saved(LiveRig rig)
    {
        lock (rig.HostFiles.Contents)
        {
            return rig.HostFiles.Contents.TryGetValue(BanList.FilePath, out var bytes) ? BanList.Parse(bytes).Count : 0;
        }
    }

    private static bool IsSoloHost(this MultiplayerSessionSnapshot snapshot) =>
        snapshot.State == MultiplayerSessionState.Ready && snapshot.LocalParticipantId.HasValue && snapshot.HostParticipantId.HasValue &&
        snapshot.LocalParticipantId.Value.Equals(snapshot.HostParticipantId.Value) && snapshot.Participants.Count == 1;

    private static bool Contains(byte[] haystack, byte[] needle)
    {
        for (var start = 0; start + needle.Length <= haystack.Length; start++)
        {
            if (haystack.AsSpan(start, needle.Length).SequenceEqual(needle)) return true;
        }

        return false;
    }

    private static IReplicatedState<CounterValue> Register(IMultiplayerSession session)
    {
        if (!session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)).TryGetValue(out var score))
        {
            throw new InvalidOperationException("Assertion failed: the score registers");
        }

        if (!session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                AddCommandType,
                RequestCodec,
                CounterCodec,
                (_, request) => score.Update(current => OperationResult<CounterValue>.Success(new CounterValue(current.Value + request.Amount))),
                PredictionMode.None)).Succeeded)
        {
            throw new InvalidOperationException("Assertion failed: the add command registers");
        }

        return score;
    }

    private static void Run(LiveRig rig, Task<MultiplayerCommandConfirmation<CounterValue>> task, string message) =>
        That(rig.Run(() => task.IsCompleted) && task.Result.Result.Succeeded, message);

}
