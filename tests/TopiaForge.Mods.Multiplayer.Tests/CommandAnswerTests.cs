using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Multiplayer.CounterSample;
using TopiaForge.Multiplayer.Engine;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// A generated command's answer through <c>Submit{Handler}</c>: on the main thread, once, at the settle point that brings
/// the host's result, and never once the mod, its binding or the caller's scope has stopped. Nothing polls for it.
/// </summary>
internal static class CommandAnswerTests
{
    private const string Owner = "io.example.counter";

    internal static void SoloPlayAnswersAtOnceAfterTheCommandsEvents()
    {
        using var rig = MultiplayerTestRig.CreateStandalone();
        var mod = new CounterSampleMod();
        using var binding = Require(mod.BindMultiplayer(rig.Server.Session), "the counter binds");
        var answers = new List<string>();
        mod.SubmitIncrement(
            new CounterRequest { Amount = 2 },
            answer => answers.Add(answer.Result.Value!.Value + " presented=" + mod.LastPresentedValue + " predicted=" + answer.WasPredicted));
        That(answers.SequenceEqual(new[] { "2 presented=2 predicted=False" }),
            "solo play answers before the submit returns, after the command's events were presented: " + string.Join(", ", answers));
    }

    internal static void AClientsAnswerArrivesWithTheHostsResult()
    {
        using var rig = MultiplayerTestRig.CreateListenServer();
        var host = new CounterSampleMod();
        using var hostBinding = Require(host.BindMultiplayer(rig.Server.Session), "the host binds");
        var guestNode = rig.AddRemoteClient("guest");
        var guest = new CounterSampleMod();
        using var guestBinding = Require(guest.BindMultiplayer(guestNode.Session), "the guest binds");
        rig.Flush();

        var mainThread = Environment.CurrentManagedThreadId;
        var answers = new List<MultiplayerCommandConfirmation<CounterResponse>>();
        var threads = new List<int>();
        guest.SubmitIncrement(new CounterRequest { Amount = 1 }, answer =>
        {
            answers.Add(answer);
            threads.Add(Environment.CurrentManagedThreadId);
        });
        That(answers.Count == 0, "a client's answer waits for the host");

        rig.Flush();
        That(answers.Count == 1 && answers[0].Result.Succeeded && answers[0].Result.Value!.Value == 1 && answers[0].WasPredicted,
            "the host's result arrives in the guest's answer, after its own prediction");
        That(threads.Single() == mainThread, "on the thread that runs the session, never a worker");
        That(host.LastPresentedValue == 1, "the host ran the command once");

        rig.Flush();
        rig.Advance(30);
        That(answers.Count == 1, "an answer runs once");
    }

    internal static void NoAnswerOnceTheBindingIsReleased()
    {
        using var rig = MultiplayerTestRig.CreateListenServer();
        var host = new CounterSampleMod();
        using var hostBinding = Require(host.BindMultiplayer(rig.Server.Session), "the host binds");
        var guestNode = rig.AddRemoteClient("guest");
        var guest = new CounterSampleMod();
        var guestBinding = Require(guest.BindMultiplayer(guestNode.Session), "the guest binds");
        rig.Flush();

        var answered = 0;
        guest.SubmitIncrement(new CounterRequest { Amount = 1 }, _ => answered++);
        guestBinding.Dispose();
        rig.Flush();
        That(answered == 0, "an answer that arrives after the binding was released never runs");
    }

    internal static void NoAnswerOnceTheCallersScopeStops()
    {
        using var rig = MultiplayerTestRig.CreateListenServer();
        var host = new CounterSampleMod();
        using var hostBinding = Require(host.BindMultiplayer(rig.Server.Session), "the host binds");
        var guestNode = rig.AddRemoteClient("guest");
        var guest = new CounterSampleMod();
        using var guestBinding = Require(guest.BindMultiplayer(guestNode.Session), "the guest binds");
        rig.Flush();

        var answered = 0;
        using var scope = new CancellationTokenSource();
        guest.SubmitIncrement(new CounterRequest { Amount = 1 }, _ => answered++, scope.Token);
        scope.Cancel();
        rig.Flush();
        That(answered == 0, "cancelling the scope's token withdraws the answer");

        guest.SubmitIncrement(new CounterRequest { Amount = 1 }, _ => answered++, scope.Token);
        rig.Flush();
        That(answered == 0 && host.LastPresentedValue == 0, "a scope that already stopped submits nothing at all");
    }

    internal static void ASessionThatGoesAwayAnswersCancelled()
    {
        using var rig = MultiplayerTestRig.CreateListenServer();
        var host = new CounterSampleMod();
        using var hostBinding = Require(host.BindMultiplayer(rig.Server.Session), "the host binds");
        var guestNode = rig.AddRemoteClient("guest");
        var guest = new CounterSampleMod();
        using var guestBinding = Require(guest.BindMultiplayer(guestNode.Session), "the guest binds");
        rig.Flush();

        var answers = new List<ModErrorCode>();
        guest.SubmitIncrement(new CounterRequest { Amount = 1 }, answer => answers.Add(answer.Result.ErrorCode));
        rig.Disconnect(guestNode);
        rig.Flush();
        That(answers.SequenceEqual(new[] { ModErrorCode.Cancelled }),
            "a mod that still runs hears that its submission was withdrawn: " + string.Join(", ", answers));
    }

    /// <summary>A mod lifetime that stops with an answer on its way: the owner's facade closes, and nothing answers.</summary>
    internal static void NoAnswerOnceTheModStops()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive);
        var host = new CounterSampleMod();
        Require(host.BindMultiplayer(harness.Host.CreateFacade(Owner)), "the host binds");
        var (client, _, _) = harness.AddClient("guest");
        var lifetime = new FakeModLifetime();
        var guest = new CounterSampleMod();

        // The binding stays live here, so only the stopped mod's own session stands between the answer and the mod.
        using var binding = Require(guest.BindMultiplayer(client.CreateFacade(Owner, lifetime)), "the guest binds");
        harness.Flush();

        var answered = 0;
        guest.SubmitIncrement(new CounterRequest { Amount = 1 }, _ => answered++);
        lifetime.Dispose();
        harness.Flush();
        That(answered == 0, "an answer for a mod that stopped never runs");
        That(host.LastPresentedValue == 0, "and a submission the mod never sent is withdrawn with it");
    }

    internal static void AFailingAnswerIsReportedNotThrownIntoTheEngine()
    {
        using var harness = new EngineHarness(MultiplayerProcessKind.Interactive, options => options.CallbackFailures = EngineCallbackFailureMode.Report);
        var host = new CounterSampleMod();
        Require(host.BindMultiplayer(harness.Host.CreateFacade(Owner)), "the host binds");
        var (client, _, _) = harness.AddClient("guest");
        var guest = new CounterSampleMod();
        Require(guest.BindMultiplayer(client.CreateFacade(Owner)), "the guest binds");
        harness.Flush();

        var answered = 0;
        guest.SubmitIncrement(new CounterRequest { Amount = 1 }, _ => throw new InvalidOperationException("The mod's own bug."));
        guest.SubmitIncrement(new CounterRequest { Amount = 1 }, _ => answered++);
        harness.Flush();
        var failure = harness.Diagnostics.Reports.SingleOrDefault(report => report.Kind == EngineDiagnosticKind.CallbackFailed);
        That(failure != null && failure.Message == "A command answer for 'increment' threw." && failure.Exception is InvalidOperationException,
            "a throwing answer is reported, naming the command: " + failure);
        That(answered == 1 && host.LastPresentedValue == 2, "and the session carries on: the next answer still runs");
    }

    /// <summary>
    /// Two players composed as the game composes them, on the deterministic network with the real transport: a joined
    /// player's answer arrives a round trip later in one of its own frames, and never after its mod stops.
    /// </summary>
    internal static void AJoinedPlayersAnswerCrossesTheNetworkOnce()
    {
        using var rig = new ControlRig(4211);
        var hostLifetime = new FakeModLifetime();
        var host = new CounterSampleMod();
        hostLifetime.Track(Require(host.BindMultiplayer(OwnerSession(rig.Host, hostLifetime)), "the host binds"));
        rig.StartHosting(new InternalHostSettings("Answers", 4));

        var peer = rig.AddPeer("guest");
        var guestLifetime = new FakeModLifetime();
        var guest = new CounterSampleMod();
        guestLifetime.Track(Require(guest.BindMultiplayer(OwnerSession(peer, guestLifetime)), "the guest binds"));
        rig.JoinAndWait(peer);
        That(rig.Run(() => peer.Session.Snapshot.State == MultiplayerSessionState.Ready), "the guest is in the host's session");

        var mainThread = Environment.CurrentManagedThreadId;
        var answers = new List<string>();
        guest.SubmitIncrement(new CounterRequest { Amount = 1 }, answer =>
            answers.Add(answer.Result.Value!.Value + (Environment.CurrentManagedThreadId == mainThread ? " on the main thread" : " elsewhere")));
        That(answers.Count == 0, "the answer is a network away");
        That(rig.Run(() => answers.Count == 1), "the host's answer reaches the guest in a later frame");
        That(answers.Single() == "1 on the main thread" && host.LastPresentedValue == 1, "with the host's result: " + answers.Single());

        guest.SubmitIncrement(new CounterRequest { Amount = 1 }, answer => answers.Add("late " + answer.Result.ErrorCode));
        guestLifetime.Dispose();
        rig.Run(() => false, 1_000_000);
        That(answers.Count == 1, "an answer on its way when the mod stopped never runs: " + string.Join(", ", answers));
        hostLifetime.Dispose();
    }

    private static IMultiplayerSession OwnerSession(ControlPeer peer, IModLifetime lifetime) =>
        (IMultiplayerSession)peer.Provider.CreateOwnerFacade(typeof(IMultiplayerSession), Owner, lifetime);

    private static T Require<T>(OperationResult<T> result, string message) where T : notnull
    {
        That(result.Succeeded, message + " (" + result.ErrorCode + ": " + result.ErrorMessage + ")");
        return result.Value!;
    }
}
