using System.Text;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static partial class PerPlayerStateTests
{
    private static readonly ParticipantId Alpha = new("alpha");
    private static readonly ParticipantId Beta = new("beta");

    internal static void Run()
    {
        GeneratedPrivateStateAndTransactions();
        RawLinksContainOnlyTheirOwnValues();
        ReconnectReplacementAndDisposal();
        SoloAndScopeCleanup();
        PerPlayerProviderTests.SoloProviderAndScopedFacadeUsePrivateState();
        PrivateChannelEnforcesTargetsAndAuthority();
        UnreadPrivateMessagesEndWithTheirStream();
        BoundsAndCredits();
        RemovalPrecedesReplacementUnderBackpressure();
        HostilePrivateEnvelopes();
        GraceExpiryForgetsValues();
        SystemChannelTests.SystemMessagesTravelBothWaysInOrder();
        SystemStreamTests.StreamsRestartWhenAPlayerRejoins();
        SessionTimeChannelTests.OnlyTheHostSendsTheSessionsTime();
        Console.WriteLine("Per-player delivery: 14 focused regression groups passed.");
    }

    private static void GeneratedPrivateStateAndTransactions()
    {
        using var h = new EngineHarness(MultiplayerProcessKind.Interactive);
        var host = new PrivateContract();
        using var hostBinding = host.BindMultiplayer(h.HostSession).Value!;
        var a = h.AddClient("alpha");
        var b = h.AddClient("beta");
        var alpha = new PrivateContract();
        var beta = new PrivateContract();
        using var aBinding = alpha.BindMultiplayer(a.Session).Value!;
        using var bBinding = beta.BindMultiplayer(b.Session).Value!;
        h.Flush();
        var observed = new List<string>();
        using var observer = alpha.Cards.SubscribeChanged(value => { observed.Add(value.Text); value.Text = "observer edit"; });
        That(host.Cards.Set(Alpha, new PrivateCard { Text = "alpha secret" }).Succeeded, "host assigns alpha");
        That(host.Cards.Set(Beta, new PrivateCard { Text = "beta secret" }).Succeeded, "host assigns beta");
        h.Flush();
        That(alpha.Cards.Get(Alpha).Value!.Text == "alpha secret" && beta.Cards.Get(Beta).Value!.Text == "beta secret", "each player gets its own card");
        That(observed.SequenceEqual(new[] { "alpha secret" }), "only the local player's changes notify, with detached copies");
        That(alpha.Cards.Get(Beta).ErrorCode == ModErrorCode.NotAuthoritative, "other players cannot be read");
        That(alpha.Cards.Set(Alpha, new PrivateCard()).ErrorCode == ModErrorCode.NotAuthoritative, "clients cannot assign themselves");
        var copy = host.Cards.Get(Alpha).Value!;
        copy.Text = "detached";
        That(host.Cards.Get(Alpha).Value!.Text == "alpha secret", "host reads are detached too");
        var rejected = a.Session.SubmitAsync(alpha.AssignCommandType, new PrivateCard { Text = "reject" });
        h.Flush();
        That(!rejected.Result.Result.Succeeded && host.Cards.Get(Alpha).Value!.Text == "alpha secret", "a failing command rolls back its private write");
        That(alpha.Cards.Get(Alpha).Value!.Text == "alpha secret", "failed private writes never reach the recipient");
        var accepted = a.Session.SubmitAsync(alpha.AssignCommandType, new PrivateCard { Text = "accepted" });
        h.Flush();
        That(accepted.Result.Result.Succeeded && alpha.Cards.Get(Alpha).Value!.Text == "accepted", "canonical command commits private state");
        That(beta.Cards.Get(Beta).Value!.Text == "beta secret", "another player's command does not change beta");
    }

    private static void RawLinksContainOnlyTheirOwnValues()
    {
        using var h = new EngineHarness();
        using var state = Register(h.HostSession);
        var a = h.AddRawPeer("alpha");
        var b = h.AddRawPeer("beta");
        a.Join("Alpha"); b.Join("Beta"); h.Flush();
        var initialA = a.Receive(); var initialB = b.Receive();
        That(initialA.Concat(initialB).Where(x => x.Type == (ulong)MessageType.StateDelta).Count() == 0, "private fields are absent from the shared snapshot");
        state.Set(Alpha, new PrivateCard { Text = "ONLY_ALPHA_9347" });
        state.Set(Beta, new PrivateCard { Text = "ONLY_BETA_2816" });
        h.Flush();
        var bytesA = a.Receive().SelectMany(x => x.Body).ToArray();
        var bytesB = b.Receive().SelectMany(x => x.Body).ToArray();
        That(Contains(bytesA, "ONLY_ALPHA_9347") && !Contains(bytesA, "ONLY_BETA_2816"), "alpha's complete link transcript excludes beta's secret");
        That(Contains(bytesB, "ONLY_BETA_2816") && !Contains(bytesB, "ONLY_ALPHA_9347"), "beta's complete link transcript excludes alpha's secret");
        // A late third peer must receive only its default, never either already-assigned value.
        var c = h.AddRawPeer("late"); c.Join("Late"); h.Flush();
        var late = c.Receive().SelectMany(x => x.Body).ToArray();
        That(!Contains(late, "ONLY_ALPHA_9347") && !Contains(late, "ONLY_BETA_2816"), "late shared and private baselines exclude other players");
    }

    private static void ReconnectReplacementAndDisposal()
    {
        using var h = new EngineHarness();
        using var host = Register(h.HostSession);
        var a = h.AddClient("alpha");
        using var local = Register(a.Session);
        h.Flush();
        var visible = "";
        using var observer = local.SubscribeChanged(value => visible = value.Text);
        host.Set(Alpha, new PrivateCard { Text = "first" }); h.Flush();
        That(visible == "first", "private presentation observed its own value");
        h.Network.Sever(a.Link); h.Advance();
        That(local.Get(Alpha).ErrorCode == ModErrorCode.NotAuthoritative, "disconnected clients cannot read retained private data");
        That(visible == "", "disconnect resets notify presentation after the local id is retired");
        host.Set(Alpha, new PrivateCard { Text = "during grace" });
        h.Join(a.Engine, "alpha"); h.Flush();
        That(local.Get(Alpha).Value!.Text == "during grace", "a fresh stream restores the latest private baseline during grace");
        h.Host.ReplaceSession(new MultiplayerSessionId("replacement"), new SessionSeed(7)); h.Flush();
        That(local.Get(Alpha).Value!.Text == "" && host.Get(Alpha).Value!.Text == "", "session replacement clears private values on both sides");
        host.Set(Alpha, new PrivateCard { Text = "removed" }); h.Flush();
        host.Dispose(); h.Flush();
        That(local.Get(Alpha).Value!.Text == "", "unregistration resets remote state");
        using var rebound = Register(h.HostSession);
        rebound.Set(Alpha, new PrivateCard { Text = "new binding" }); h.Flush();
        That(local.Get(Alpha).Value!.Text == "new binding", "a new registration can restart versions without stale state");
        local.Dispose();
        using var lateBinding = Register(a.Session);
        That(lateBinding.Get(Alpha).Value!.Text == "new binding", "a late client binding adopts its bounded private cache");
    }

    private static void SoloAndScopeCleanup()
    {
        using var h = new EngineHarness(MultiplayerProcessKind.Interactive);
        var facade = h.Host.CreateFacade("scoped");
        var handle = new PerPlayerState<PrivateCard>(new PrivateCard());
        var codec = new JsonTestCodec<PrivateCard>();
        using var binding = handle.Bind(facade, "card", codec).Value!;
        var local = new ParticipantId("host");
        var seen = "";
        using var observer = handle.SubscribeChanged(value => seen = value.Text);
        That(handle.Set(local, new PrivateCard { Text = "solo" }).Succeeded && seen == "solo", "solo uses the same transactional state and not a wire loopback");
        That(handle.Set(new ParticipantId("unknown"), new PrivateCard()).ErrorCode == ModErrorCode.NotFound, "unknown targets cannot allocate private rows");
        facade.Dispose();
        That(!handle.Get(local).Succeeded, "scope disposal retires private registrations");
        binding.Dispose();
        using var next = handle.Bind(h.HostSession, "card", codec).Value!;
        That(handle.Get(local).Value!.Text == "", "rebinding restores the frozen default, not the prior player's secret");
    }

    private static IPerPlayerState<PrivateCard> Register(EngineSessionFacade session, string id = "card") =>
        session.RegisterPerPlayerState(new ReplicatedStateDefinition<PrivateCard>(id, new PrivateCard(), new JsonTestCodec<PrivateCard>())).Value!;

    private static bool Contains(byte[] bytes, string value) => Encoding.UTF8.GetString(bytes).Contains(value, StringComparison.Ordinal);
}

[MultiplayerContract(Id = "private.cards")]
internal partial class PrivateContract
{
    [ReplicatedState("card")]
    internal PerPlayerState<PrivateCard> Cards = new(new PrivateCard());

    [MultiplayerCommand("assign", Prediction = PredictionMode.None, MaximumPayloadBytes = 512)]
    private OperationResult<PrivateCard> Assign(MultiplayerCommandContext context, PrivateCard request)
    {
        var result = Cards.Set(context.SenderId, request);
        return request.Text == "reject" ? OperationResult<PrivateCard>.Failure(ModErrorCode.InvalidArgument, "Rejected.") : result;
    }
}

public sealed class PrivateCard
{
    [NetworkBound(96)]
    public string Text { get; set; } = "";
}
