using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Runs a gamemode-v6 <c>participant-lifecycle</c> case twice: on the runtime's session orchestrator, in the case's
    /// realm, and on the testing kit's <see cref="GamemodeSessionRunner"/>, which authors test their gamemodes with. Both
    /// must give the case's result, so the kit can never drift from what the game does.
    /// </summary>
    internal static class ParticipantLifecycleFixtureRunner
    {
        // A code the SDK does not define, as an author's cast can make one; the game reports it as External.
        private const ModErrorCode UnknownCode = (ModErrorCode)99;

        private static readonly HashSet<string> ScenarioFields = new HashSet<string>(StringComparer.Ordinal)
        {
            "role", "factory", "serverStart", "participantStart", "participantStop", "serverStop"
        };

        public static JsonElement Snapshot(JsonElement body, string path)
        {
            var scenario = new Scenario(body.GetProperty("scenario"), path);
            var root = Directory.CreateTempSubdirectory("TopiaForgeLifecycleFixture-");
            try
            {
                var runtime = OnRuntime(scenario, root.FullName);
                var kit = OnTestingKit(scenario);
                if (!DeclarationDigest.Equal(runtime, kit))
                    throw new InvalidOperationException(path + ": the testing kit's runner gives " + kit.GetRawText()
                        + " where the runtime gives " + runtime.GetRawText());
                return runtime;
            }
            finally
            {
                try { root.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }

        private static JsonElement OnRuntime(Scenario scenario, string root)
        {
            var calls = new List<string>();
            var pending = new TaskCompletionSource<OperationResult<IGamemodeParticipantController>>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var test = GamemodeParticipantRealmTests.InRealm(Path.Combine(root, "runtime"), scenario.Realm,
                scenario.Participants ? typeof(SessionTestParticipantFactory) : typeof(SessionTestFactory));
            SessionFixture.Start = (_, _) => scenario.StartServer(calls);
            SessionFixture.StartParticipant = (_, _) => scenario.StartParticipant(calls, pending);
            using var cancellation = new CancellationTokenSource();
            var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "lifecycle-fixture", cancellation.Token);
            if (scenario.CancelledWhilePending)
            {
                test.Until(() => calls.Contains("participant.start"));
                cancellation.Cancel();
                test.Wait(launch);
                pending.SetResult(OperationResult<IGamemodeParticipantController>.Success(scenario.Participant(calls)));
            }
            else
            {
                test.Wait(launch);
            }

            var stop = "not-running";
            if (launch.Result.Succeeded)
            {
                var stopped = test.Hosted.ShutdownAsync();
                test.Wait(stopped);
                stop = stopped.Result.Succeeded ? "succeeded" : "failed";
            }
            else
            {
                test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
            }

            return Result(launch.Result, calls, stop);
        }

        private static JsonElement OnTestingKit(Scenario scenario)
        {
            var calls = new List<string>();
            var pending = new TaskCompletionSource<OperationResult<IGamemodeParticipantController>>();
            IGamemodeFactory gamemode = scenario.Participants
                ? new ScenarioParticipantGamemode(scenario, calls, pending)
                : new ScenarioServerGamemode(scenario, calls);
            using var runner = new GamemodeSessionRunner(gamemode, scenario.Role);
            using var cancellation = new CancellationTokenSource();
            var start = runner.StartAsync(cancellation.Token);
            if (scenario.CancelledWhilePending)
            {
                cancellation.Cancel();
                pending.SetResult(OperationResult<IGamemodeParticipantController>.Success(scenario.Participant(calls)));
            }

            var launched = start.GetAwaiter().GetResult();
            var stop = "not-running";
            if (launched.Succeeded)
            {
                try
                {
                    runner.Stop();
                    stop = "succeeded";
                }
                catch (InvalidOperationException)
                {
                    stop = "failed";
                }
                catch (AggregateException)
                {
                    stop = "failed";
                }
            }

            return Result(launched, calls, stop);
        }

        private static JsonElement Result(OperationResult<bool> launch, List<string> calls, string stop)
        {
            var output = new
            {
                outcome = "accept",
                normalized = new
                {
                    launch = new
                    {
                        outcome = launch.Succeeded ? "succeeded" : "failed",
                        code = launch.Succeeded ? "none" : Camel(launch.ErrorCode.ToString()),
                        message = launch.Succeeded ? string.Empty : launch.ErrorMessage
                    },
                    calls = calls.ToArray(),
                    stop
                }
            };
            using var document = JsonDocument.Parse(JsonSerializer.Serialize(output));
            return document.RootElement.Clone();
        }

        private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name.Substring(1);

        /// <summary>One case's gamemode: what each controller does as it starts and stops.</summary>
        private sealed class Scenario
        {
            internal Scenario(JsonElement raw, string path)
            {
                foreach (var property in raw.EnumerateObject())
                    if (!ScenarioFields.Contains(property.Name))
                        throw new InvalidOperationException(path + " has an unknown scenario field '" + property.Name + "'.");
                var role = Read(raw, "role", path, "single-player", "host", "client");
                Role = role == "client" ? GamemodeSessionRole.Client : role == "host" ? GamemodeSessionRole.Host : GamemodeSessionRole.SinglePlayer;
                Realm = role == "client" ? NetRealm.Client : role == "host" ? NetRealm.ListenHost : NetRealm.Offline;
                Participants = Read(raw, "factory", path, "participant", "server-only") == "participant";
                ServerStart = Read(raw, "serverStart", path, "succeeds", "fails", "fails-without-reason", "fails-with-an-unknown-code", "throws");
                ParticipantStart = Read(raw, "participantStart", path, "succeeds", "fails", "fails-without-reason",
                    "fails-with-an-unknown-code", "throws", "throws-without-reason", "cancelled-while-pending");
                ParticipantStopThrows = Read(raw, "participantStop", path, "succeeds", "throws") == "throws";
                ServerStopThrows = Read(raw, "serverStop", path, "succeeds", "throws") == "throws";
            }

            internal GamemodeSessionRole Role { get; }
            internal NetRealm Realm { get; }
            internal bool Participants { get; }
            internal string ServerStart { get; }
            internal string ParticipantStart { get; }
            internal bool ParticipantStopThrows { get; }
            internal bool ServerStopThrows { get; }
            internal bool CancelledWhilePending => ParticipantStart == "cancelled-while-pending";

            internal Task<OperationResult<IGamemodeController>> StartServer(List<string> calls)
            {
                calls.Add("server.start");
                switch (ServerStart)
                {
                    case "throws": throw new InvalidOperationException("The arena is missing.");
                    case "fails":
                        return Task.FromResult(OperationResult<IGamemodeController>.Failure(ModErrorCode.InvalidState, "The arena has no spawn."));
                    case "fails-without-reason":
                        return Task.FromResult(OperationResult<IGamemodeController>.Failure(ModErrorCode.InvalidState, string.Empty));
                    case "fails-with-an-unknown-code":
                        return Task.FromResult(OperationResult<IGamemodeController>.Failure(UnknownCode, "The arena has no spawn."));
                    default:
                        return Task.FromResult(OperationResult<IGamemodeController>.Success(
                            new ScenarioController(calls, "server.stop", ServerStopThrows ? "The round would not end." : null)));
                }
            }

            internal Task<OperationResult<IGamemodeParticipantController>> StartParticipant(
                List<string> calls, TaskCompletionSource<OperationResult<IGamemodeParticipantController>> pending)
            {
                calls.Add("participant.start");
                switch (ParticipantStart)
                {
                    case "throws": throw new InvalidOperationException("The tool bar exploded.");
                    case "throws-without-reason": throw new InvalidOperationException(string.Empty);
                    case "fails":
                        return Task.FromResult(OperationResult<IGamemodeParticipantController>.Failure(ModErrorCode.Unavailable,
                            "The tool bar has no slot for this player."));
                    case "fails-without-reason":
                        return Task.FromResult(OperationResult<IGamemodeParticipantController>.Failure(ModErrorCode.Unavailable, string.Empty));
                    case "fails-with-an-unknown-code":
                        return Task.FromResult(OperationResult<IGamemodeParticipantController>.Failure(UnknownCode,
                            "The tool bar has no slot for this player."));
                    case "cancelled-while-pending": return pending.Task;
                    default: return Task.FromResult(OperationResult<IGamemodeParticipantController>.Success(Participant(calls)));
                }
            }

            internal IGamemodeParticipantController Participant(List<string> calls) =>
                new ScenarioController(calls, "participant.stop", ParticipantStopThrows ? "The tool bar would not close." : null);

            private static string Read(JsonElement raw, string field, string path, params string[] allowed)
            {
                var value = raw.TryGetProperty(field, out var element) ? element.GetString() : null;
                if (value == null || !allowed.Contains(value, StringComparer.Ordinal))
                    throw new InvalidOperationException(path + " requires scenario." + field + " to be one of " + string.Join(", ", allowed) + ".");
                return value;
            }
        }

        private sealed class ScenarioController : IGamemodeController, IGamemodeParticipantController
        {
            private readonly List<string> calls;
            private readonly string label;
            private readonly string? failure;

            internal ScenarioController(List<string> calls, string label, string? failure)
            {
                this.calls = calls;
                this.label = label;
                this.failure = failure;
            }

            public void Dispose()
            {
                calls.Add(label);
                if (failure != null) throw new InvalidOperationException(failure);
            }
        }

        private sealed class ScenarioServerGamemode : IGamemodeFactory
        {
            private readonly Scenario scenario;
            private readonly List<string> calls;

            internal ScenarioServerGamemode(Scenario scenario, List<string> calls)
            {
                this.scenario = scenario;
                this.calls = calls;
            }

            public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken cancellationToken) =>
                scenario.StartServer(calls);
        }

        private sealed class ScenarioParticipantGamemode : IGamemodeParticipantFactory
        {
            private readonly Scenario scenario;
            private readonly List<string> calls;
            private readonly TaskCompletionSource<OperationResult<IGamemodeParticipantController>> pending;

            internal ScenarioParticipantGamemode(Scenario scenario, List<string> calls,
                TaskCompletionSource<OperationResult<IGamemodeParticipantController>> pending)
            {
                this.scenario = scenario;
                this.calls = calls;
                this.pending = pending;
            }

            public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken cancellationToken) =>
                scenario.StartServer(calls);

            public Task<OperationResult<IGamemodeParticipantController>> StartParticipantAsync(
                IGamemodeParticipantSession participant, CancellationToken cancellationToken) =>
                scenario.StartParticipant(calls, pending);
        }
    }
}
