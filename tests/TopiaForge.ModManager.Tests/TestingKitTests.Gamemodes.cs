using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
        private static void TestParticipantSessionFake()
        {
            using var context = new FakeModContext();
            using var stopping = new CancellationTokenSource();
            var world = new WorldReadiness(new WorldSceneIdentity(7, "Arena"), TransformState.Identity);
            var participant = new FakeGamemodeParticipantSession(context, world, "session-1", worldId: "arena.world",
                cancellationToken: stopping.Token);
            Assert(participant.SessionId == "session-1" && participant.WorldId == "arena.world" && ReferenceEquals(participant.World, world)
                   && ReferenceEquals(participant.Lifetime, context.Lifetime) && participant.CancellationToken == stopping.Token,
                "a participant session fake carries explicit identity, readiness, cancellation and its context's lifetime");
            participant.OnRestart = _ => Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Conflict, "Busy."));
            Assert(participant.StopAsync().Result.Succeeded && participant.RestartAsync().Result.ErrorCode == ModErrorCode.Conflict
                   && participant.StopRequests == 1 && participant.RestartRequests == 1,
                "bound operations are captured and answer deterministically");
            stopping.Cancel();
            Assert(participant.ReturnToMainMenuAsync().Result.ErrorCode == ModErrorCode.Cancelled && participant.MainMenuRequests == 1,
                "a stopping session refuses its bound operations as cancelled");
        }

        private static void TestGamemodeSessionRunnerRoles()
        {
            foreach (var role in new[] { GamemodeSessionRole.SinglePlayer, GamemodeSessionRole.Host, GamemodeSessionRole.Client })
            {
                var gamemode = new ProbeGamemode();
                var runner = new GamemodeSessionRunner(gamemode, role);
                Assert(!ReferenceEquals(runner.ServerContext, runner.ParticipantContext)
                       && runner.ParticipantContext.Identity.Id == runner.ServerContext.Identity.Id,
                    role + ": the participant gets a context of its own, for the same mod");
                var started = runner.StartAsync().GetAwaiter().GetResult();
                var expected = role == GamemodeSessionRole.Client
                    ? new[] { "participant.start" } : new[] { "server.start", "participant.start" };
                Assert(started.Succeeded && runner.IsRunning && gamemode.Calls.SequenceEqual(expected),
                    role + ": the runner starts " + string.Join(", ", expected) + " (" + string.Join(", ", gamemode.Calls) + ")");
                Assert((runner.Session == null) == (role == GamemodeSessionRole.Client) && runner.Participant != null
                       && (runner.Controller == null) == (role == GamemodeSessionRole.Client) && runner.ParticipantController != null,
                    role + ": a joined player has no server controller, every role has the participant");
                Assert(runner.ParticipantContext.Ui.Toasts.Count == 1 && runner.ServerContext.Ui.Toasts.Count == 0,
                    role + ": the participant's HUD is made through its own context");
                runner.Stop();
                var stopped = role == GamemodeSessionRole.Client
                    ? new[] { "participant.stop", "participant.scope" }
                    : new[] { "participant.stop", "server.stop", "participant.scope", "server.scope" };
                Assert(gamemode.Calls.Skip(expected.Length).SequenceEqual(stopped) && !runner.IsRunning,
                    role + ": stopping disposes the participant first and then the server, then their contexts ("
                    + string.Join(", ", gamemode.Calls) + ")");
                runner.ParticipantContext.AssertNoLeaks();
                runner.ServerContext.AssertNoLeaks();
                Assert(runner.Participant!.CancellationToken.IsCancellationRequested, role + ": the session's token is cancelled");
            }

            var serverOnly = new ProbeGamemode();
            using (var runner = new GamemodeSessionRunner(new ServerOnlyGamemode(serverOnly), GamemodeSessionRole.Host))
            {
                Assert(runner.StartAsync().Result.Succeeded && runner.Participant == null && serverOnly.Calls.SequenceEqual(new[] { "server.start" }),
                    "a gamemode without a participant factory starts only its server");
            }

            Assert(serverOnly.Calls.SequenceEqual(new[] { "server.start", "server.stop", "server.scope" }), "and Dispose stops it");
            using var shared = new FakeModContext();
            AssertThrows<ArgumentException>(() => new GamemodeSessionRunner(new ProbeGamemode(), GamemodeSessionRole.Host, shared, shared),
                "the participant cannot share the server's context, since the game never shares their scopes");
        }

        private static void TestGamemodeSessionRunnerFailures()
        {
            var failing = new ProbeGamemode
            {
                ParticipantFailure = OperationResult<IGamemodeParticipantController>.Failure(
                    ModErrorCode.Unavailable, "The tool bar has no slot for this player.")
            };
            var runner = new GamemodeSessionRunner(failing, GamemodeSessionRole.Host);
            var result = runner.StartAsync().Result;
            Assert(!result.Succeeded && result.ErrorCode == ModErrorCode.Unavailable
                   && result.ErrorMessage == "The gamemode's participant controller failed to start: The tool bar has no slot for this player.",
                "a participant that fails ends the session with its reason, said to be the participant's (" + result.ErrorMessage + ")");
            Assert(failing.Calls.SequenceEqual(new[] { "server.start", "participant.start", "server.stop", "participant.scope", "server.scope" })
                   && !runner.IsRunning && runner.CleanupFailures.Count == 0,
                "and the server controller that started is disposed with both contexts (" + string.Join(", ", failing.Calls) + ")");
            AssertThrows<InvalidOperationException>(runner.Stop, "a session that never ran cannot be stopped");

            var verbose = new ProbeGamemode
            {
                ParticipantFailure = OperationResult<IGamemodeParticipantController>.Failure(ModErrorCode.Unavailable, new string('x', 9000))
            };
            var bounded = new GamemodeSessionRunner(verbose).StartAsync().Result;
            Assert(bounded.ErrorCode == ModErrorCode.Unavailable && bounded.ErrorMessage.Length == 4096
                   && bounded.ErrorMessage.StartsWith("The gamemode's participant controller failed to start: xxx", StringComparison.Ordinal)
                   && bounded.ErrorMessage.EndsWith("x\u2026", StringComparison.Ordinal),
                "a reason longer than the game shows is cut as the game cuts it: 4,096 characters, the last an ellipsis ("
                + bounded.ErrorMessage.Length + ")");

            var throwing = new ProbeGamemode { ParticipantThrows = true };
            var thrown = new GamemodeSessionRunner(throwing).StartAsync().Result;
            Assert(thrown.ErrorCode == ModErrorCode.External && thrown.ErrorMessage.EndsWith("The tool bar exploded.", StringComparison.Ordinal),
                "a participant that throws ends the session as an external failure");

            var stubborn = new ProbeGamemode { ParticipantStopThrows = true };
            var stopping = new GamemodeSessionRunner(stubborn, GamemodeSessionRole.Host);
            Assert(stopping.StartAsync().Result.Succeeded, "the stubborn gamemode starts");
            AssertThrows<InvalidOperationException>(stopping.Stop, "a participant that throws while stopping is reported");
            Assert(stubborn.Calls.SequenceEqual(new[] { "server.start", "participant.start", "participant.stop", "server.stop", "participant.scope", "server.scope" }),
                "and never keeps the server controller or the contexts from stopping (" + string.Join(", ", stubborn.Calls) + ")");

            var slow = new ProbeGamemode { PendingParticipant = new TaskCompletionSource<OperationResult<IGamemodeParticipantController>>() };
            using var cancellation = new CancellationTokenSource();
            var waiting = new GamemodeSessionRunner(slow);
            var start = waiting.StartAsync(cancellation.Token);
            Assert(!start.IsCompleted, "the participant's start is still pending");
            cancellation.Cancel();
            slow.PendingParticipant.SetResult(OperationResult<IGamemodeParticipantController>.Success(new ProbeParticipant(slow.Calls, throws: false)));
            var cancelled = start.Result;
            Assert(cancelled.ErrorCode == ModErrorCode.Cancelled && cancelled.ErrorMessage == "Session startup was cancelled.",
                "cancelling while the participant starts ends the session as cancelled");
            Assert(slow.Calls.SequenceEqual(new[] { "server.start", "participant.start", "participant.stop", "server.stop", "server.scope" }),
                "and the participant that returned late is disposed before the server (" + string.Join(", ", slow.Calls) + ")");
        }

        /// <summary>A gamemode with a server and a participant controller, each of which records its life.</summary>
        private sealed class ProbeGamemode : IGamemodeParticipantFactory
        {
            internal List<string> Calls { get; } = new List<string>();
            internal OperationResult<IGamemodeParticipantController>? ParticipantFailure { get; set; }
            internal bool ParticipantThrows { get; set; }
            internal bool ParticipantStopThrows { get; set; }
            internal TaskCompletionSource<OperationResult<IGamemodeParticipantController>>? PendingParticipant { get; set; }

            public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken cancellationToken)
            {
                Calls.Add("server.start");
                session.Lifetime.Defer(() => Calls.Add("server.scope"));
                return Task.FromResult(OperationResult<IGamemodeController>.Success(new ProbeServer(Calls)));
            }

            public Task<OperationResult<IGamemodeParticipantController>> StartParticipantAsync(
                IGamemodeParticipantSession participant, CancellationToken cancellationToken)
            {
                Calls.Add("participant.start");
                if (ParticipantThrows) throw new InvalidOperationException("The tool bar exploded.");
                if (PendingParticipant != null) return PendingParticipant.Task;
                participant.Lifetime.Defer(() => Calls.Add("participant.scope"));
                participant.Context.Ui.ShowToast("Tools ready.");
                return Task.FromResult(ParticipantFailure ?? OperationResult<IGamemodeParticipantController>.Success(
                    new ProbeParticipant(Calls, ParticipantStopThrows)));
            }
        }

        private sealed class ServerOnlyGamemode : IGamemodeFactory
        {
            private readonly ProbeGamemode probe;
            internal ServerOnlyGamemode(ProbeGamemode probe) => this.probe = probe;
            public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken cancellationToken) =>
                probe.StartAsync(session, cancellationToken);
        }

        private sealed class ProbeServer : IGamemodeController
        {
            private readonly List<string> calls;
            internal ProbeServer(List<string> calls) => this.calls = calls;
            public void Dispose() => calls.Add("server.stop");
        }

        private sealed class ProbeParticipant : IGamemodeParticipantController
        {
            private readonly List<string> calls;
            private readonly bool throws;
            internal ProbeParticipant(List<string> calls, bool throws) { this.calls = calls; this.throws = throws; }
            public void Dispose()
            {
                calls.Add("participant.stop");
                if (throws) throw new InvalidOperationException("The tool bar would not close.");
            }
        }
    }
}
