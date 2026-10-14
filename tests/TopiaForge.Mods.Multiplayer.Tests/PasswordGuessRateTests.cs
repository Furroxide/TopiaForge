extern alias provider;

using System.Diagnostics;
using TopiaForge.Mods;
using provider::TopiaForge.Multiplayer.Network;
using provider::TopiaForge.Multiplayer.Transport;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using ProviderEngine = provider::TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// Online guessing end to end, with real cryptography, transport, engine and live flows on the virtual network. Every
/// guess is a fresh identity on a fresh endpoint that completes a pinned handshake with a password-protected host and
/// asks to join with a random proof. The runs measure how many guesses the host checks: from one address and port, from
/// one address rotating ports, and from many addresses. None may get more than its budgets allow, and a player already
/// in the session plays on, and comes back over a new link, while the host turns every newcomer's password away.
/// </summary>
internal static class PasswordGuessRateTests
{
    private const long Minute = 60_000_000;

    internal static void GuessersGetNoMoreThanTheirBudgets()
    {
        var minutes = HarnessOptions.ExtendedSoak ? 30 : 4;
        var lines = new List<string>
        {
            Measure("one address and port", 170, sources: 1, rotatePorts: false, parallel: 1, minutes, PasswordGuessLimits.DefaultPerNetwork),
            Measure("one address, a new port each time", 171, sources: 1, rotatePorts: true, parallel: LiveHost.MaximumLinksPerSource, minutes, PasswordGuessLimits.DefaultPerNetwork),
            Measure("64 addresses", 172, sources: 64, rotatePorts: true, parallel: 16, minutes, PasswordGuessLimits.DefaultPerHost)
        };
        if (HarnessOptions.ExtendedSoak) lines.Add(Measure("256 addresses", 173, sources: 256, rotatePorts: true, parallel: 16, minutes, PasswordGuessLimits.DefaultPerHost));
        Console.WriteLine("Password guessing, " + minutes + " virtual minutes each: " + string.Join("; ", lines) + ".");
    }

    internal static void APlayerInTheSessionNeverWaitsForGuessers()
    {
        using var rig = new LiveRig(174, seededTransports: true);
        var hostScore = Register(rig.Host);
        rig.StartHosting(new HostSettings("Locked", 8) { Port = 0, Password = "open sesame" });
        var regular = rig.AddClient("regular");
        var regularScore = Register(regular);
        var join = rig.Join(regular, new JoinSettings("Regular") { Password = "open sesame" });
        That(rig.Run(() => join.Phase == JoinPhase.InSession), "a player who knows the password joins (" + join.Failure + ")");
        var participant = regular.Snapshot.LocalParticipantId;

        // Guessers from many networks spend the host's budget, and it turns every newcomer's password away.
        var policy = rig.Host.Live!.Host!.Policy;
        var guessers = new Guessers(rig, 175, sources: 16, rotatePorts: true, parallel: 8);
        That(rig.Run(() =>
        {
            guessers.Step(rig.Network.Now);
            return policy.Guesses.HostLocked(rig.Network.Now);
        }, 60_000_000), "the guessers spend the host's budget (" + guessers + ")");
        var lockedAt = rig.Network.Now;
        guessers.Stopped = true;
        That(rig.Run(() =>
        {
            guessers.Step(rig.Network.Now);
            return !guessers.Busy;
        }, 30_000_000) && guessers.Checked == PasswordGuessLimits.DefaultPerHost, "after exactly its budget of wrong guesses (" + guessers + ")");

        var command = regular.SubmitAsync(AddType, new AddRequest(2));
        That(rig.Run(() => command.IsCompleted) && command.Result.Result.Succeeded && hostScore.Value.Value == 2 && regularScore.Value.Value == 2,
            "the player in the session plays on");

        var newcomer = rig.AddClient("newcomer");
        Register(newcomer);
        var refused = rig.Join(newcomer, new JoinSettings("Newcomer") { Password = "open sesame" });
        That(rig.Run(() => refused.Phase == JoinPhase.Ended) && refused.RejectionCode == AdmissionCode.TooManyAttempts &&
             refused.Detail.Contains("try again shortly", StringComparison.Ordinal),
            "a newcomer with the right password hears to try again shortly, with a report (" + refused.RejectionCode + ": " + refused.Detail + ")");

        // The regular player's link drops while the host still turns passwords away; it comes back as the same player.
        var hostNode = rig.HostFactory.Networks[^1].Node;
        rig.Network.Partition(hostNode, rig.NodeOf(regular), cut: true);
        That(rig.Run(() => join.Phase == JoinPhase.Reconnecting, 20_000_000), "the regular player's link is lost");
        rig.Network.Partition(hostNode, rig.NodeOf(regular), cut: false);
        That(rig.Run(() => join.Phase == JoinPhase.InSession, 20_000_000) && regular.Snapshot.LocalParticipantId.Equals(participant),
            "and it comes back over a new link as the same player (" + join.Phase + ", " + join.Failure + ": " + join.Detail + ")");
        That(policy.Guesses.HostLocked(rig.Network.Now), "while the host was still turning newcomers away (" + (rig.Network.Now - lockedAt) / 1_000_000 + " s in)");

        // Once the lockout is over, the newcomer gets in. Nothing runs in the background meanwhile, so the wait needs no
        // real time.
        var deadline = rig.Network.Now + (2 * Minute);
        while (policy.Guesses.HostLocked(rig.Network.Now) && rig.Network.Now < deadline)
        {
            rig.Host.Pump();
            foreach (var client in rig.Clients) client.Pump();
            rig.Network.RunFor(5_000);
        }

        That(!policy.Guesses.HostLocked(rig.Network.Now), "the host's lockout ends");
        var admitted = rig.Join(newcomer, new JoinSettings("Newcomer") { Password = "open sesame" });
        That(rig.Run(() => admitted.Phase == JoinPhase.InSession), "and the newcomer joins (" + admitted.Failure + ")");
        That(rig.Network.Spins == 0, "no busy loop");
    }

    /// <summary>Runs guessers against a fresh host for <paramref name="minutes"/> and checks them against their budget's schedule.</summary>
    private static string Measure(string name, int seed, int sources, bool rotatePorts, int parallel, int minutes, int budget)
    {
        var watch = Stopwatch.StartNew();
        using var rig = new LiveRig(seed, seededTransports: true);
        rig.StartHosting(new HostSettings("Locked", 8) { Port = 0, Password = "open sesame" });
        var guessers = new Guessers(rig, seed, sources, rotatePorts, parallel);
        var end = rig.Network.Now + (minutes * Minute);
        while (rig.Network.Now < end)
        {
            rig.Host.Pump();
            guessers.Step(rig.Network.Now);
            var wake = guessers.Busy ? rig.Network.Now + 5_000 : Math.Min(guessers.NextStartMicros, end);
            rig.Network.RunUntil(Math.Max(wake, rig.Network.Now + 5_000));
        }

        // A guesser that retries the moment each lockout ends gets one budget per lockout: at 0, 1, 3 and 7 minutes, then
        // every 8. Nothing may beat that, and the run must have found every budget it could.
        var batches = Batches(minutes * Minute);
        That(guessers.Checked <= budget * batches && guessers.Checked >= budget * (batches - 1),
            name + ": the host checks at most " + (budget * batches) + " guesses in " + minutes + " minutes, and the guessers found nearly all (" + guessers + ")");
        That(rig.Network.Spins == 0, name + ": no busy loop");
        return name + " " + guessers.Checked + " checked (" + (guessers.Checked / (double)minutes).ToString("0.00") + " a minute), " +
               guessers.Refused + " turned away unchecked, " + guessers.Unanswered + " unanswered, in " + watch.Elapsed.TotalSeconds.ToString("0.0") + " s";
    }

    /// <summary>How many budgets an eager guesser can spend by <paramref name="micros"/>: lockouts of 1, 2, 4, then 8 minutes.</summary>
    private static int Batches(long micros)
    {
        var batches = 0;
        long at = 0;
        var lockout = PasswordGuessLimits.DefaultFirstLockoutMicros;
        while (at <= micros)
        {
            batches++;
            at += lockout;
            lockout = Math.Min(lockout * 2, PasswordGuessLimits.DefaultLongestLockoutMicros);
        }

        return batches;
    }

    private static readonly JsonTestCodec<CounterValue> CounterCodec = new();
    private static readonly JsonTestCodec<AddRequest> RequestCodec = new();
    private static readonly MultiplayerCommandType<AddRequest, CounterValue> AddType = new("add");

    private static IReplicatedState<CounterValue> Register(IMultiplayerSession session)
    {
        if (!session.RegisterState(new ReplicatedStateDefinition<CounterValue>("score", new CounterValue(0), CounterCodec)).TryGetValue(out var score))
        {
            throw new InvalidOperationException("Assertion failed: the score registers");
        }

        if (!session.RegisterCommand(new MultiplayerCommandDefinition<AddRequest, CounterValue>(
                AddType, RequestCodec, CounterCodec,
                (_, request) => score.Update(current => OperationResult<CounterValue>.Success(new CounterValue(current.Value + request.Amount))),
                PredictionMode.None)).Succeeded)
        {
            throw new InvalidOperationException("Assertion failed: the add command registers");
        }

        return score;
    }
}

/// <summary>
/// Guessers on the virtual network: slots that each run one attempt at a time, every attempt a fresh identity on a fresh
/// endpoint from one of the sources, completing a pinned handshake and asking to join with a random proof. A slot turned
/// away unchecked, or left unanswered, waits a few seconds before it tries again; a refusal costs nothing to send, so
/// probing faster would only cost the run time, never earn a guess.
/// </summary>
internal sealed class Guessers
{
    private const long ProbeMicros = 5_000_000;
    private const long TimeoutMicros = 15_000_000;

    private readonly LiveRig rig;
    private readonly VirtualNode host;
    private readonly byte[] hostKey;
    private readonly bool rotatePorts;
    private readonly Random random;
    private readonly List<VirtualNode?> sources = new();
    private readonly Slot[] slots;
    private int next;

    internal Guessers(LiveRig rig, int seed, int sources, bool rotatePorts, int parallel)
    {
        this.rig = rig;
        host = rig.HostFactory.Networks[^1].Node;
        hostKey = rig.Host.Live!.Host!.Descriptor!.Descriptor.TransportKey;
        this.rotatePorts = rotatePorts;
        random = new Random(seed);
        if (!rotatePorts && (sources != 1 || parallel != 1)) throw new ArgumentException("One port holds one attempt at a time.");

        // Rotating guessers share an anchor's address; a fixed-port guesser takes over the port its last attempt left.
        for (var index = 0; index < sources; index++) this.sources.Add(rotatePorts ? rig.Network.AddRaw("source-" + index) : null);
        slots = Enumerable.Range(0, parallel).Select(_ => new Slot()).ToArray();
    }

    internal int Checked { get; private set; }

    internal int Refused { get; private set; }

    internal int Unanswered { get; private set; }

    internal int Attempts { get; private set; }

    internal bool Stopped { get; set; }

    /// <summary>Gets whether any attempt is under way, so the network must run in small steps.</summary>
    internal bool Busy => slots.Any(slot => slot.Node != null);

    /// <summary>Gets when the next idle slot may start an attempt.</summary>
    internal long NextStartMicros => Stopped ? long.MaxValue : slots.Min(slot => slot.IdleUntil);

    internal void Step(long now)
    {
        foreach (var slot in slots) Advance(slot, now);
    }

    public override string ToString() => Attempts + " attempts: " + Checked + " checked, " + Refused + " turned away, " + Unanswered + " unanswered";

    private void Advance(Slot slot, long now)
    {
        if (slot.Node == null)
        {
            if (!Stopped && now >= slot.IdleUntil) Start(slot, now);
            return;
        }

        var attempt = slot.Attempt!;
        if (attempt.State == ConnectAttemptState.Failed || (attempt.State == ConnectAttemptState.Pending && now - slot.StartedAt > TimeoutMicros))
        {
            Finish(slot, null, now);
            return;
        }

        if (attempt.State != ConnectAttemptState.Established) return;
        var link = attempt.Link!;
        if (!slot.Sent)
        {
            link.Send(ProviderEngine.EngineChannel.Session, JoinFrame());
            slot.Sent = true;
        }

        while (link.TryReceive(out _, out var frame))
        {
            var code = Rejection(frame);
            if (code != null)
            {
                Finish(slot, code, now);
                return;
            }
        }

        if (link.State == ProviderEngine.EngineLinkState.Closed || now - slot.StartedAt > TimeoutMicros) Finish(slot, null, now);
    }

    private void Start(Slot slot, long now)
    {
        var index = next++ % sources.Count;
        if (!rotatePorts && sources[0] != null && !sources[0]!.Removed) return;
        var key = new byte[32];
        random.NextBytes(key);
        var stack = new NetworkStack(new TransportEndpoint(key, new TransportOptions { AcceptsInbound = false, Random = new SeededTransportRandom(random.Next()) }, now));
        VirtualNode node;
        if (rotatePorts) node = rig.Network.AddStack("guess", stack, sources[index]);
        else if (sources[0] == null) node = rig.Network.AddStack("guess", stack);
        else node = rig.Network.AddStack("guess", stack, sources[0], samePort: true);
        if (!rotatePorts) sources[0] = node;
        slot.Node = node;
        slot.Attempt = new ConnectAttempt();
        slot.StartedAt = now;
        slot.Sent = false;
        stack.Connect(slot.Attempt, VirtualDatagramNetwork.PathTo(host, TransportPathKind.Lan), hostKey, now);
        Attempts++;
    }

    private void Finish(Slot slot, AdmissionCode? code, long now)
    {
        if (code == AdmissionCode.PasswordWrong) Checked++;
        else if (code == AdmissionCode.TooManyAttempts) Refused++;
        else Unanswered++;
        rig.Network.Retire(slot.Node!, now);
        slot.Node = null;
        slot.Attempt = null;
        slot.IdleUntil = code == AdmissionCode.PasswordWrong ? now : now + ProbeMicros;
    }

    private byte[] JoinFrame()
    {
        var proof = new byte[PasswordProof.ProofSize];
        random.NextBytes(proof);
        var frame = new ProviderEngine.ProtocolWriter();
        ProviderEngine.JoinMessages.WriteJoinRequest(frame, new ProviderEngine.ProtocolWriter(), new ProviderEngine.JoinRequestMessage(
            ProviderEngine.SessionEngine.ProtocolVersion, "Guesser", Array.Empty<ProviderEngine.ContractEntry>(), JoinContent.Encode(proof, new byte[] { 1, 2, 3 })));
        return frame.ToArray();
    }

    /// <summary>Reads the host's answer: the code of its admission report, or null when the frame holds none.</summary>
    private static AdmissionCode? Rejection(byte[] frame)
    {
        var reader = new ProviderEngine.ProtocolReader(frame);
        while (!reader.AtEnd)
        {
            var type = reader.ReadVarUInt();
            var length = reader.ReadLength(ProviderEngine.ProtocolLimits.MaximumFrameBytes);
            var start = frame.Length - reader.Remaining;
            reader.Skip(length);
            That(type != ProviderEngine.MessageType.JoinAccepted, "a random proof is never admitted");
            if (type != ProviderEngine.MessageType.JoinRejected) continue;
            var rejected = ProviderEngine.JoinMessages.ReadJoinRejected(new ProviderEngine.ProtocolReader(frame, start, length));
            return AdmissionReport.TryDecode(rejected.Report, out var code, out _) ? code : AdmissionCode.Other;
        }

        return null;
    }

    private sealed class Slot
    {
        internal VirtualNode? Node { get; set; }

        internal ConnectAttempt? Attempt { get; set; }

        internal long StartedAt { get; set; }

        internal long IdleUntil { get; set; }

        internal bool Sent { get; set; }
    }
}
