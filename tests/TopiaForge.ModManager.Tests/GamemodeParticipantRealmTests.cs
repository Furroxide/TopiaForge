using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The participant realm (WS3.9): by the realm a session starts in, the orchestrator starts the gamemode's server
    /// controller where the server runs and then this game's own player's participant controller, in a scope of its own;
    /// it stops them in the reverse order, and a participant that fails ends the session as a failed server start does.
    /// </summary>
    internal static class GamemodeParticipantRealmTests
    {
        internal static void Run(string root)
        {
            RolesStartWhatRunsThere(root + "-roles");
            AGamemodeWithoutTheFactoryIsUnchanged(root + "-server-only");
            AParticipantThatFailsToStartEndsTheSession(root + "-start-failure");
            AParticipantThatThrowsWhileStoppingNeverKeepsTheServerRunning(root + "-stop-failure");
            CancellationWhileTheParticipantStarts(root + "-cancel");
            TheParticipantRunsInItsOwnScope(root + "-scope");
            AParticipantsInputActsForItsPlayer(root + "-intent");
            AScopeThatResolvesTheSessionRetiresOnlyWhatItMade(root + "-retirement");
            Console.WriteLine("GamemodeParticipantRealmTests passed.");
        }

        private static void RolesStartWhatRunsThere(string root)
        {
            foreach (var realm in Realms)
            {
                using var test = InRealm(root + realm, realm);
                var calls = Record(test);
                var phases = new List<SessionPhase>();
                test.Hosted.StateChanged += state => phases.Add(state.Phase);
                test.Launch();
                var started = realm == NetRealm.Client ? new[] { "participant.start" } : new[] { "server.start", "participant.start" };
                That(calls.SequenceEqual(started), realm + " starts " + string.Join(", ", started) + " (" + string.Join(", ", calls) + ")");
                That(phases.SequenceEqual(new[] { SessionPhase.Preparing, SessionPhase.LoadingWorld, SessionPhase.StartingMode, SessionPhase.Running }),
                    realm + ": the participant starts within StartingMode, and the session runs only once it has");
                var expectedScopes = realm == NetRealm.Client
                    ? new[] { ModExecutionContext.WorldOwner, ModExecutionContext.Participant }
                    : new[] { ModExecutionContext.GamemodeServer, ModExecutionContext.Participant };
                That(test.ScopeProfiles.Select(profile => profile.Context).SequenceEqual(expectedScopes),
                    realm + ": the session's scopes are " + string.Join(", ", expectedScopes) + " (" + string.Join(", ", test.ScopeProfiles) + ")");
                That(test.Parent.ActiveChildScopeCount == 2, realm + ": the participant has a scope of its own beside the session's");

                var stop = SessionFixture.ParticipantSession!.StopAsync();
                test.Wait(stop);
                test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
                var stopped = realm == NetRealm.Client
                    ? new[] { "participant.stop", "participant.scope" }
                    : new[] { "participant.stop", "server.stop", "participant.scope", "server.scope" };
                That(stop.Result.Succeeded && calls.Skip(started.Length).SequenceEqual(stopped),
                    realm + ": stopping disposes the participant before the server controller, and closes its scope first ("
                    + string.Join(", ", calls) + ")");
                That(test.Parent.ActiveChildScopeCount == 0 && test.Outcomes.Single(outcome => outcome.Kind == "session").Status == "succeeded",
                    realm + ": every scope is released and the session ends cleanly");
            }
        }

        private static void AGamemodeWithoutTheFactoryIsUnchanged(string root)
        {
            foreach (var realm in Realms)
            {
                using var test = InRealm(root + realm, realm, typeof(SessionTestFactory));
                var calls = Record(test);
                var constructed = 0;
                SessionFixture.FactoryConstructor = () => constructed++;
                test.Launch();
                var serverRuns = realm != NetRealm.Client;
                That(calls.SequenceEqual(serverRuns ? new[] { "server.start" } : Array.Empty<string>()),
                    realm + ": a gamemode without a participant factory starts its server where the server runs, and nothing else");
                That(constructed == (serverRuns ? 1 : 0),
                    realm + ": it is constructed only where its server runs, so a joined player runs none of its code (" + constructed + ")");
                That(test.ScopeProfiles.Count == 1 && test.Parent.ActiveChildScopeCount == 1 && SessionFixture.ParticipantSession == null,
                    realm + ": it gets no participant scope");
                test.Wait(test.Hosted.ShutdownAsync());
                That(calls.SequenceEqual(serverRuns ? new[] { "server.start", "server.stop", "server.scope" } : Array.Empty<string>()),
                    realm + ": and stops as before");
            }
        }

        private static void AParticipantThatFailsToStartEndsTheSession(string root)
        {
            foreach (var (name, realm) in new[] { ("result", NetRealm.ListenHost), ("throw", NetRealm.Offline), ("client", NetRealm.Client) })
            {
                using var test = InRealm(root + name, realm);
                var calls = Record(test);
                SessionFixture.StartParticipant = (participant, _) =>
                {
                    calls.Add("participant.start");
                    participant.Lifetime.Defer(() => calls.Add("participant.scope"));
                    if (name == "throw") throw new InvalidOperationException("The tool bar has no slot for this player.");
                    return Task.FromResult(OperationResult<IGamemodeParticipantController>.Failure(ModErrorCode.Unavailable,
                        "The tool bar has no slot for this player."));
                };
                var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "participant-" + name);
                test.Wait(launch);
                var code = name == "throw" ? ModErrorCode.External : ModErrorCode.Unavailable;
                That(!launch.Result.Succeeded && launch.Result.ErrorCode == code && launch.Result.ErrorMessage ==
                    GamemodeSessionOrchestrator.ParticipantStartFailure + "The tool bar has no slot for this player.",
                    name + ": the launch fails with the participant's own reason, said to be the participant's (" + launch.Result.ErrorMessage + ")");
                test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
                var expected = realm == NetRealm.Client
                    ? new[] { "participant.start", "participant.scope" }
                    : new[] { "server.start", "participant.start", "server.stop", "participant.scope", "server.scope" };
                That(calls.SequenceEqual(expected), name + ": the server controller that started is disposed and every scope closes ("
                    + string.Join(", ", calls) + ")");
                That(test.Parent.ActiveChildScopeCount == 0 && !test.Native.IsSceneBusy, name + ": nothing of the session is left");
                var outcome = test.Outcomes.Single(value => value.Kind == "session");
                That(outcome.Status == "failed" && test.Outcomes.Single(value => value.Kind == "launch").Error!.Message == launch.Result.ErrorMessage,
                    name + ": the session ends as a failed server start does, with the reason in its launch outcome");
            }

            using var serverFails = InRealm(root + "-server", NetRealm.ListenHost);
            var serverCalls = Record(serverFails);
            SessionFixture.Start = (_, _) =>
            {
                serverCalls.Add("server.start");
                return Task.FromResult(OperationResult<IGamemodeController>.Failure(ModErrorCode.InvalidState, "The arena has no spawn."));
            };
            var failed = serverFails.Hosted.StartAsync(serverFails.Plan.Descriptor, "server-fails");
            serverFails.Wait(failed);
            serverFails.Until(() => serverFails.Hosted.Current.Phase == SessionPhase.Idle);
            That(!failed.Result.Succeeded && failed.Result.ErrorMessage == "The arena has no spawn." && serverCalls.SequenceEqual(new[] { "server.start" }),
                "a server that fails to start keeps its own reason, and the participant never starts");
            That(serverFails.ScopeProfiles.Count == 1, "and no participant scope is made");
        }

        private static void AParticipantThatThrowsWhileStoppingNeverKeepsTheServerRunning(string root)
        {
            using var test = InRealm(root, NetRealm.ListenHost);
            var calls = Record(test);
            SessionFixture.StartParticipant = (participant, _) =>
            {
                calls.Add("participant.start");
                return Task.FromResult(OperationResult<IGamemodeParticipantController>.Success(new SessionTestParticipantController(() =>
                {
                    calls.Add("participant.stop");
                    throw new InvalidOperationException("The tool bar would not close.");
                })));
            };
            test.Launch();
            var stopped = test.Hosted.ShutdownAsync();
            test.Wait(stopped);
            That(calls.SequenceEqual(new[] { "server.start", "participant.start", "participant.stop", "server.stop", "server.scope" }),
                "a participant that throws while stopping is still followed by the server controller's stop (" + string.Join(", ", calls) + ")");
            That(!stopped.Result.Succeeded && stopped.Result.ErrorMessage.Contains("The tool bar would not close.", StringComparison.Ordinal),
                "and the failure is reported, not hidden");
            That(test.Outcomes.Single(outcome => outcome.Kind == "session").Status == "failed" && test.Parent.ActiveChildScopeCount == 0,
                "the session's terminal outcome says it failed, and every scope is still released");
        }

        private static void CancellationWhileTheParticipantStarts(string root)
        {
            using var test = InRealm(root, NetRealm.Offline);
            var calls = Record(test);
            var pending = new TaskCompletionSource<OperationResult<IGamemodeParticipantController>>(TaskCreationOptions.RunContinuationsAsynchronously);
            SessionFixture.StartParticipant = (_, _) =>
            {
                calls.Add("participant.start");
                return pending.Task;
            };
            using var cancellation = new CancellationTokenSource();
            var launch = test.Hosted.StartAsync(test.Plan.Descriptor, "cancel-participant", cancellation.Token);
            test.Until(() => calls.Contains("participant.start"));
            cancellation.Cancel();
            test.Wait(launch);
            That(launch.Result.ErrorCode == ModErrorCode.Cancelled && test.Hosted.Current.Phase == SessionPhase.Stopping,
                "cancelling the caller while the participant starts settles the launch, and the session waits for the participant");
            var disposed = 0;
            pending.SetResult(OperationResult<IGamemodeParticipantController>.Success(new SessionTestParticipantController(() =>
            {
                disposed++;
                calls.Add("participant.stop");
            })));
            test.Until(() => test.Hosted.Current.Phase == SessionPhase.Idle);
            That(disposed == 1 && calls.SequenceEqual(new[] { "server.start", "participant.start", "participant.stop", "server.stop", "server.scope" }),
                "a participant that returns after the session began stopping is disposed once, before the server controller ("
                + string.Join(", ", calls) + ")");
            That(test.Outcomes.Single(outcome => outcome.Kind == "session").Status == "cancelled", "and the session ends as cancelled");
            SessionFixture.StartParticipant = (participant, _) =>
            {
                SessionFixture.ParticipantSession = participant;
                return Task.FromResult(OperationResult<IGamemodeParticipantController>.Success(new SessionTestParticipantController()));
            };
            test.Launch("successor");
            That(test.Hosted.Current.Phase == SessionPhase.Running, "a successor starts cleanly");
        }

        private static void TheParticipantRunsInItsOwnScope(string root)
        {
            foreach (var realm in Realms)
            {
                using var test = InRealm(root + realm, realm);
                test.Launch();
                var participant = SessionFixture.ParticipantSession!;
                var seam = ((IInternalReplicationContext)participant.Context).Replication;
                That(seam.ExecutionContext == InternalExecutionContext.Participant && seam.Realm == (InternalNetRealm)(int)realm,
                    realm + ": the participant's services carry the Participant execution context, in the session's realm");
                That(participant.Context.Identity.Id == test.Parent.Identity.Id && !ReferenceEquals(participant.Context, test.Parent)
                     && ReferenceEquals(participant.Lifetime, participant.Context.Lifetime),
                    realm + ": its scope keeps the package's identity, apart from the package context, with the lifetime its context exposes");
                var route = seam.Route(InternalIntent.InputActiveFrame);
                That(route == (realm == NetRealm.Client ? InternalRoute.Forwarded : InternalRoute.Canonical),
                    realm + ": a player's action in the participant is canonical where the server runs and forwarded on a joined player (" + route + ")");
                That(seam.TimeAuthority == (realm == NetRealm.Offline ? InternalTimeAuthority.Apply : InternalTimeAuthority.NotAuthoritative),
                    realm + ": online, the participant never controls time; only the gamemode server does");
                if (realm != NetRealm.Client)
                {
                    var session = SessionFixture.ModeSession!;
                    That(!ReferenceEquals(session.Context, participant.Context) && !ReferenceEquals(session.Lifetime, participant.Lifetime)
                         && ((IInternalReplicationContext)session.Context).Replication.ExecutionContext == InternalExecutionContext.GamemodeServer,
                        realm + ": the server controller keeps its own scope, a gamemode server");
                    That(participant.SessionId == session.SessionId && participant.GamemodeId == session.GamemodeId
                         && participant.WorldId == session.WorldId && ReferenceEquals(participant.World, session.World)
                         && participant.CancellationToken == session.CancellationToken,
                        realm + ": both see one session: its identity, readiness and cancellation");
                }

                var restarted = participant.RestartAsync();
                test.Wait(restarted);
                That(restarted.Result.Succeeded && SessionFixture.ParticipantSession!.SessionId != participant.SessionId && participant.Lifetime.IsStopping,
                    realm + ": the participant's bound restart replaces the session, and its own scope has stopped");
            }
        }

        /// <summary>
        /// Intent tracking (WS3.4) keeps its rules for the participant's scope: an action the participant registers makes its
        /// mod's frame input-active, as every session scope's does, and on a joined player that frame's work is forwarded.
        /// </summary>
        private static void AParticipantsInputActsForItsPlayer(string root)
        {
            const string arena = "io.example.arena";
            using var world = new IntentWorld(root, InternalSessionRole.Client, arena);
            var package = world.Contexts[0];
            using var dispatcher = new HostDispatcher();
            var scope = world.Scope(package, GamemodeSessionOrchestrator.ParticipantScopeId("session-1"), dispatcher, ModExecutionContext.Participant);
            var tools = scope.Context.Input.RegisterAction(new InputActionDefinition("tools", "Tools", new[] { InputBinding.Key("F5") })).Value!;
            var trace = new List<string>();
            var seam = ((IInternalReplicationContext)scope.Context).Replication;
            scope.Context.Events.SubscribeUpdate(_ => trace.Add(MultiplayerIntentServicesTests.Current(scope.Context) + " " + seam.RouteCurrentIntent()));
            world.Frame(trace, "quiet");
            world.Host.Action(arena, "tools").Hold(true);
            world.Frame(trace, "tools");
            That(trace.SequenceEqual(new[] { "frame:quiet", "none Forwarded", "frame:tools", "InputActiveFrame/local Forwarded" }),
                "a participant's action makes its frame the local player's, and on a joined player its work is forwarded ("
                + string.Join(", ", trace) + ")");
            That(package.HasActiveInput, "the mod's activity counts the participant's action");
            scope.BeginStop();
            scope.Dispose();
            That(!package.HasActiveInput, "and the participant's scope ends the action with it");
            GC.KeepAlive(tools);
        }

        /// <summary>
        /// DevEx audit B1, at the runtime: the mod's own context and the session's scopes each resolve the session, so each
        /// gets a facade of the mod. Ending the session retires what the gamemode's scopes made and keeps the mod's drone.
        /// </summary>
        private static void AScopeThatResolvesTheSessionRetiresOnlyWhatItMade(string root)
        {
            using var rig = MultiplayerTestRig.CreateStandalone();
            var registry = new ModServiceRegistry();
            var provider = DispatchProxy.Create<IMultiplayerSession, RigSessionProvider>();
            ((RigSessionProvider)(object)provider).Rig = rig;
            That(registry.RegisterExtension(MultiplayerModule.Id, provider, ExtensionCardinality.Singleton).Succeeded,
                "the test rig stands in for the multiplayer provider");
            var test = InRealm(root, NetRealm.Offline, configure: manifest => manifest.Dependencies[MultiplayerModule.Id] = ">=1.0.0",
                registry: registry, available: new[] { Manifest(MultiplayerModule.Id) });
            var mod = test.Parent.RequireMultiplayer();
            That(mod.RegisterObjectType(MarkerType.Definition).Succeeded, "the mod registers its object type");
            var drone = Spawned(mod, 1, "the mod spawns its drone");
            IReplicatedObject<Marker, Marker>? round = null, toolbar = null;
            SessionFixture.Start = (session, _) =>
            {
                round = Spawned(session.Context.RequireMultiplayer(), 2, "the gamemode server spawns its round");
                return Task.FromResult(OperationResult<IGamemodeController>.Success(new SessionTestController()));
            };
            SessionFixture.StartParticipant = (participant, _) =>
            {
                toolbar = Spawned(participant.Context.RequireMultiplayer(), 3, "the host's participant spawns its tool bar");
                return Task.FromResult(OperationResult<IGamemodeParticipantController>.Success(new SessionTestParticipantController()));
            };
            test.Launch();
            That(mod.GetObjects(MarkerType.Type).Count == 3, "the mod's session lists its drone, the round and the tool bar");

            test.Wait(test.Hosted.ShutdownAsync());
            That(drone.IsSpawned && !round!.IsSpawned && !toolbar!.IsSpawned,
                "ending the session retires what its scopes made, and the mod's drone stays while the mod runs");
            That(mod.GetObjects(MarkerType.Type).Single().State.Value == 1, "one object before the session and one after it, the mod's own");

            test.Dispose();
            That(!drone.IsSpawned, "the mod's own drone goes when the mod itself stops");
        }

        /// <summary>A session fixture whose composition root is in <paramref name="realm"/>, with a participant factory unless told otherwise.</summary>
        internal static SessionFixture InRealm(string root, NetRealm realm, Type? factory = null, Action<ModManifest>? configure = null,
            ModServiceRegistry? registry = null, IEnumerable<ModManifest>? available = null)
        {
            var (runtime, control, _) = Runtime(() => SessionPhase.Idle);
            control.Role = realm == NetRealm.ListenHost ? InternalSessionRole.ListenHost
                : realm == NetRealm.Client ? InternalSessionRole.Client : InternalSessionRole.Solo;
            runtime.Connect();
            Frame(runtime);
            That(runtime.Realm == realm, "the composition root is " + realm);
            return new SessionFixture(root, replication: new MultiplayerReplication(runtime), configure: configure, registry: registry, available: available)
            {
                FactoryType = factory ?? typeof(SessionTestParticipantFactory)
            };
        }

        /// <summary>Records the start and stop of each controller and the close of each scope, in order.</summary>
        private static List<string> Record(SessionFixture test)
        {
            var calls = new List<string>();
            SessionFixture.Start = (session, _) =>
            {
                calls.Add("server.start");
                SessionFixture.ModeSession = session;
                session.Lifetime.Defer(() => calls.Add("server.scope"));
                return Task.FromResult(OperationResult<IGamemodeController>.Success(new SessionTestController(() => calls.Add("server.stop"))));
            };
            SessionFixture.StartParticipant = (participant, _) =>
            {
                calls.Add("participant.start");
                SessionFixture.ParticipantSession = participant;
                participant.Lifetime.Defer(() => calls.Add("participant.scope"));
                return Task.FromResult(OperationResult<IGamemodeParticipantController>.Success(
                    new SessionTestParticipantController(() => calls.Add("participant.stop"))));
            };
            return calls;
        }

        private static IReplicatedObject<Marker, Marker> Spawned(IMultiplayerSession session, int value, string what)
        {
            var spawned = session.SpawnObject(MarkerType.Type, new Marker(value));
            That(spawned.Succeeded, what + ": " + spawned.ErrorMessage);
            return spawned.Value!;
        }

        /// <summary>Stands in for the multiplayer provider: each consuming context gets a rig facade bound to its own lifetime.</summary>
        public class RigSessionProvider : DispatchProxy, IOwnerBoundExtensionFactory
        {
            internal MultiplayerTestRig? Rig;

            public object CreateOwnerFacade(Type contractType, string ownerModId, IModLifetime lifetime)
            {
                var rig = Rig ?? throw new InvalidOperationException("The provider needs its rig.");
                var session = rig.CreateOwnerSession(rig.Server, ownerModId);
                lifetime.Track((IDisposable)session);
                return session;
            }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
                throw new InvalidOperationException("Mods reach the session through their own facade, never the provider: " + targetMethod?.Name);
        }

        private sealed class Marker
        {
            public Marker(int value) => Value = value;

            public int Value { get; }
        }

        private static class MarkerType
        {
            internal static readonly ReplicatedObjectType<Marker, Marker> Type = new ReplicatedObjectType<Marker, Marker>("marker");

            internal static readonly ReplicatedObjectTypeDefinition<Marker, Marker> Definition = new ReplicatedObjectTypeDefinition<Marker, Marker>(
                Type, Codec.Instance, Codec.Instance, (_, state, _) => OperationResult<Marker>.Success(state));
        }

        private sealed class Codec : IMultiplayerCodec<Marker>
        {
            internal static readonly Codec Instance = new Codec();

            public int MaximumEncodedBytes => 4;

            public OperationResult<byte[]> Encode(Marker value) => OperationResult<byte[]>.Success(BitConverter.GetBytes(value.Value));

            public OperationResult<Marker> Decode(byte[] bytes) => bytes.Length == 4
                ? OperationResult<Marker>.Success(new Marker(BitConverter.ToInt32(bytes, 0)))
                : OperationResult<Marker>.Failure(ModErrorCode.InvalidArgument, "A marker is four bytes.");
        }
    }
}
