extern alias provider;

using TopiaForge.Mods;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;
using ProviderEngine = provider::TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal sealed class SoakScore
{
    public SoakScore(long value)
    {
        Value = value;
    }

    public long Value { get; }
}

/// <summary>A command request. Every one carries a nonce no other request shares, so the ledger can see a double execution.</summary>
internal sealed class SoakOrder
{
    public SoakOrder(int peer, int nonce, int amount)
    {
        Peer = peer;
        Nonce = nonce;
        Amount = amount;
    }

    public int Peer { get; }

    public int Nonce { get; }

    public int Amount { get; }
}

internal sealed class SoakMarker
{
    public SoakMarker(long value, long nudges)
    {
        Value = value;
        Nudges = nudges;
    }

    public long Value { get; }

    public long Nudges { get; }
}

internal sealed class SoakNudge
{
    public SoakNudge(int peer, int nonce, int delta)
    {
        Peer = peer;
        Nonce = nonce;
        Delta = delta;
    }

    public int Peer { get; }

    public int Nonce { get; }

    public int Delta { get; }
}

internal sealed class SoakFlash
{
    public SoakFlash(long score)
    {
        Score = score;
    }

    public long Score { get; }
}

/// <summary>What one process ran canonically: every add and nudge by nonce, and each session's running score.</summary>
internal sealed class SoakLedger
{
    private readonly Dictionary<(int Peer, int Nonce), string> adds = new();
    private readonly HashSet<(int Peer, int Nonce)> nudges = new();
    private readonly Dictionary<string, (long Sum, int Count)> sessions = new(StringComparer.Ordinal);

    internal List<string> Violations { get; } = new();

    internal int Adds => adds.Count;

    internal int Nudges => nudges.Count;

    internal bool RanAdd(int peer, int nonce) => adds.ContainsKey((peer, nonce));

    internal bool RanNudge(int peer, int nonce) => nudges.Contains((peer, nonce));

    internal (long Sum, int Count) Session(string id) => sessions.TryGetValue(id, out var totals) ? totals : (0, 0);

    internal void RecordAdd(string session, SoakOrder order)
    {
        if (!adds.TryAdd((order.Peer, order.Nonce), session))
        {
            Violations.Add("add " + order.Peer + "/" + order.Nonce + " ran twice, in " + adds[(order.Peer, order.Nonce)] + " and " + session);
            return;
        }

        var totals = Session(session);
        sessions[session] = (totals.Sum + order.Amount, totals.Count + 1);
    }

    internal void RecordNudge(SoakNudge nudge)
    {
        if (!nudges.Add((nudge.Peer, nudge.Nonce))) Violations.Add("nudge " + nudge.Peer + "/" + nudge.Nonce + " ran twice");
    }
}

/// <summary>
/// The mod every soak peer runs, registered the same way on every process, as a mod would be: a coalesced score that
/// owner-predicted commands raise, an immediate tally the host's ticks raise, owner-predicted markers the host spawns,
/// changes, hands between players and despawns, and a presentation event. Only the process holding world authority runs
/// the ticks, so a client in solo play runs them in its own session.
/// </summary>
internal sealed class SoakWorkload
{
    internal const int MaximumMarkers = 8;
    internal static readonly MultiplayerCommandType<SoakOrder, SoakScore> AddType = new("soak.add");
    internal static readonly ReplicatedObjectType<SoakMarker, SoakNudge> MarkerType = new("soak.marker");
    internal static readonly PresentationEventType<SoakFlash> FlashType = new("soak.flash", new JsonTestCodec<SoakFlash>());

    private readonly Provider provider;
    private readonly Random random;
    private readonly Func<bool> quiet;

    internal SoakWorkload(Provider provider, int seed, Func<bool> quiet)
    {
        this.provider = provider;
        this.quiet = quiet;
        random = new Random(seed);
        Score = Require(provider.RegisterState(new ReplicatedStateDefinition<SoakScore>("soak.score", new SoakScore(0), new JsonTestCodec<SoakScore>())), "score");
        Tally = Require(provider.RegisterState(new ReplicatedStateDefinition<SoakScore>(
            "soak.tally", new SoakScore(0), new JsonTestCodec<SoakScore>(), MultiplayerDelivery.Immediate, 10)), "tally");
        Require(provider.RegisterCommand(new MultiplayerCommandDefinition<SoakOrder, SoakScore>(
            AddType, new JsonTestCodec<SoakOrder>(), new JsonTestCodec<SoakScore>(), Add, PredictionMode.Owner, 20, 4096)), "add");
        Require(provider.RegisterObjectType(new ReplicatedObjectTypeDefinition<SoakMarker, SoakNudge>(
            MarkerType, new JsonTestCodec<SoakMarker>(), new JsonTestCodec<SoakNudge>(), Nudge, PredictionMode.Owner, 15, 4096,
            MultiplayerDelivery.Coalesced, 5)), "marker");
        Require(provider.RegisterPresentation(new PresentationEventDefinition<SoakFlash>(FlashType, _ => Flashes++)), "flash");
        provider.SubscribeTick(Tick);
    }

    internal IReplicatedState<SoakScore> Score { get; }

    internal IReplicatedState<SoakScore> Tally { get; }

    internal SoakLedger Ledger { get; } = new();

    internal int Flashes { get; private set; }

    internal IReadOnlyList<IReplicatedObject<SoakMarker, SoakNudge>> Markers => provider.GetObjects(MarkerType);

    /// <summary>Describes everything replicated, for comparing one process's view with another's.</summary>
    internal string Describe()
    {
        var markers = Markers.Select(marker => marker.Id + "@" + marker.Version + ":" + (marker.OwnerId?.Value ?? "server") + "=" +
                                                marker.State.Value + "/" + marker.State.Nudges);
        return "score " + Score.Value.Value + "@" + Score.Version + ", tally " + Tally.Value.Value + "@" + Tally.Version +
               ", markers [" + string.Join(", ", markers) + "]";
    }

    private bool Canonical => provider.Engine.InTransaction && provider.Engine.Scope.Kind == ProviderEngine.TransactionKind.Canonical;

    private OperationResult<SoakScore> Add(MultiplayerCommandContext command, SoakOrder order)
    {
        if (order.Amount < 1 || order.Amount > 9)
        {
            return OperationResult<SoakScore>.Failure(ModErrorCode.InvalidArgument, "An add is 1 to 9.");
        }

        var updated = Score.Update(current => OperationResult<SoakScore>.Success(new SoakScore(current.Value + order.Amount)));
        if (!updated.TryGetValue(out var score)) return updated;
        if (Canonical) Ledger.RecordAdd(provider.Snapshot.Id.Value, order);
        command.Emit(FlashType, new SoakFlash(score.Value));
        return updated;
    }

    private OperationResult<SoakMarker> Nudge(ReplicatedObjectCommandContext command, SoakMarker state, SoakNudge nudge)
    {
        if (!command.SenderOwnsTarget) return OperationResult<SoakMarker>.Failure(ModErrorCode.NotAuthoritative, "Only the owner nudges a marker.");
        if (nudge.Delta < -3 || nudge.Delta > 3) return OperationResult<SoakMarker>.Failure(ModErrorCode.InvalidArgument, "A nudge is -3 to 3.");
        if (Canonical) Ledger.RecordNudge(nudge);
        return OperationResult<SoakMarker>.Success(new SoakMarker(state.Value + nudge.Delta, state.Nudges + 1));
    }

    /// <summary>Host ticks: the tally climbs, markers come and go, change and change hands, all inside the tick's transaction.</summary>
    private void Tick(MultiplayerTickContext tick)
    {
        if (quiet()) return;
        var step = tick.Tick.Value;
        if (step % 4 == 0) Tally.Update(current => OperationResult<SoakScore>.Success(new SoakScore(current.Value + 1)));
        var markers = Markers;
        if (step % 30 == 0)
        {
            if (markers.Count >= MaximumMarkers) provider.DespawnObject(markers[random.Next(markers.Count)].Id);
            else provider.SpawnObject(MarkerType, new SoakMarker(random.Next(100), 0), PickOwner());
        }

        if (step % 10 == 5 && markers.Count > 0)
        {
            var marker = markers[random.Next(markers.Count)];
            if (!marker.OwnerId.HasValue) marker.Apply(current => OperationResult<SoakMarker>.Success(new SoakMarker(current.Value + 1, current.Nudges)));
        }

        if (step % 97 == 0 && markers.Count > 0) markers[random.Next(markers.Count)].TransferOwnership(PickOwner());
    }

    /// <summary>Picks a connected player, the host's own included, or the server.</summary>
    private ParticipantId? PickOwner()
    {
        var connected = provider.Snapshot.Participants.Where(participant => participant.IsConnected).ToList();
        var pick = random.Next(connected.Count + 1);
        return pick == connected.Count ? null : connected[pick].Id;
    }

    private static T Require<T>(OperationResult<T> result, string what) where T : notnull =>
        result.TryGetValue(out var value) ? value : throw new InvalidOperationException("Assertion failed: the soak " + what + " registers: " + result.ErrorMessage);
}
