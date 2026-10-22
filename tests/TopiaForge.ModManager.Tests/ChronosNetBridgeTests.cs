using System;
using System.Collections.Generic;
using TopiaForge.Chronos;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// Chronos' owner facade and bridge in a session (WS3.13), over real replication seams: who may freeze, slow or step time
    /// in each realm, what a change of realm ends, the clock that yields to the game's own pause menu only offline, the
    /// host's time a joined player follows, and the time a host publishes.
    /// </summary>
    internal static class ChronosNetBridgeTests
    {
        private const string Consumer = "io.example.zombies";
        private const string ChronosId = "io.github.furroxide.topiaforge.chronos";

        internal static void Run()
        {
            OnlyTheServerControllerChangesTimeOnline();
            ARealmChangeEndsWhatLostItsAuthority();
            TheClockYieldsToTheGamesPauseOnlyOffline();
            AJoinedPlayerFollowsTheHostsTime();
            AHostPublishesWhatItsLeasesDerive();
        }

        private static void OnlyTheServerControllerChangesTimeOnline()
        {
            var (services, control, runtime) = RuntimeReplicationTests.Services();
            var lifetime = new OwnerModLifetime();
            var bridge = new ChronosNetBridge(services.Bind(ChronosId, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), lifetime));
            var backend = new TimeControlService();
            foreach (var (role, realm) in new[]
                     {
                         (InternalSessionRole.Solo, NetRealm.Offline), (InternalSessionRole.ListenHost, NetRealm.ListenHost), (InternalSessionRole.Client, NetRealm.Client)
                     })
            {
                control.Role = role;
                Frame(runtime);
                bridge.Update();
                foreach (var networkClass in Classes)
                {
                    foreach (var context in Contexts)
                    {
                        var profile = new ModNetProfile(networkClass, context);
                        var seam = services.Bind(Consumer, profile, lifetime);
                        var time = backend.ForConsumer(Consumer, lifetime, bridge, seam);
                        var allowed = ExecutionRouting.TimeControl(realm, profile) == TimeControlRoute.Apply;
                        var reason = networkClass == ModNetworkClass.ClientLocal && realm != NetRealm.Offline ? ChronosNetRules.ClientLocalRefusal
                            : realm == NetRealm.Client ? ChronosNetRules.ClientRefusal : ChronosNetRules.HostRefusal;
                        var label = profile + " in " + realm;
                        That(allowed == (realm == NetRealm.Offline || (realm == NetRealm.ListenHost && context == ModExecutionContext.GamemodeServer &&
                                                                       networkClass != ModNetworkClass.ClientLocal && networkClass != ModNetworkClass.Standalone)),
                            label + ": the table allows time control offline, and online only from the host's gamemode server");
                        var calls = backend.Calls;
                        Expect(time.Freeze("freeze"), allowed, reason, label + " freezes");
                        Expect(time.Slow("slow", 0.5f), allowed, reason, label + " slows");
                        Expect(time.SetDriver("driver", new FixedDriver()), allowed, reason, label + " sets a driver");
                        Expect(time.Step(0.1f), allowed, reason, label + " steps");
                        Expect(time.StepFixed(2), allowed, reason, label + " steps fixed updates");
                        Expect(time.BeginTurnBased("turns", new TurnSchedulerOptions()), allowed, reason, label + " begins turn-based play");
                        That(backend.Calls == calls + (allowed ? 3 : 0), label + ": a refusal never reaches the service");
                        That(time.ExemptPlayer("run").Succeeded && backend.Calls == calls + (allowed ? 4 : 1),
                            label + ": exempting its own player changes no one's time, so it is never refused");
                        That(!allowed || time.Slow("bad", 2f).ErrorCode == ModErrorCode.InvalidArgument, label + ": bad arguments are still refused as such");
                    }
                }
            }

            static void Expect<T>(OperationResult<T> result, bool allowed, string reason, string label) where T : notnull
            {
                if (allowed)
                {
                    That(result.Succeeded, label + " (" + result.ErrorMessage + ")");
                    return;
                }

                That(result.ErrorCode == ModErrorCode.NotAuthoritative && result.ErrorMessage == reason, label + ": refused as not authoritative (" + result.ErrorMessage + ")");
            }
        }

        private static void ARealmChangeEndsWhatLostItsAuthority()
        {
            var (services, control, runtime) = RuntimeReplicationTests.Services();
            var lifetime = new OwnerModLifetime();
            var bridge = new ChronosNetBridge(services.Bind(ChronosId, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), lifetime));
            var backend = new TimeControlService();
            ITimeControlService For(ModExecutionContext context) =>
                backend.ForConsumer(Consumer, lifetime, bridge, services.Bind(Consumer, new ModNetProfile(ModNetworkClass.Shared, context), lifetime));
            var (server, tools, process, world) = (For(ModExecutionContext.GamemodeServer), For(ModExecutionContext.Participant),
                For(ModExecutionContext.Process), For(ModExecutionContext.WorldOwner));

            // Single player: every part of the mod may hold time.
            var round = server.Freeze("round").Value!;
            var shop = tools.Slow("shop", 0.5f).Value!;
            var hud = process.SetDriver("hud", new FixedDriver()).Value!;
            var arena = world.Freeze("arena").Value!;
            var run = tools.ExemptPlayer("run").Value!;
            var turns = new RecordingScheduler();
            bridge.Track(turns, services.Bind(Consumer, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Participant), lifetime));
            That(!bridge.Update() && round.IsActive && shop.IsActive && hud.IsActive && arena.IsActive && bridge.FollowedLeases == 4,
                "offline nothing changes, and the bridge follows the four leases that could lose their authority");

            // Opening the game to other players ends what only single player allowed; the server controller keeps its round.
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            That(bridge.Update() && bridge.Realm == InternalNetRealm.ListenHost, "the bridge sees the promotion");
            That(round.IsActive && !shop.IsActive && !hud.IsActive && !arena.IsActive && run.IsActive && turns.Disposals == 1,
                "the participant's slow, the process's driver, the world's freeze and the participant's turns end; the round and the exemption stay");
            That(bridge.FollowedLeases == 1 && !bridge.Update(), "it follows the round alone, and only a change of realm ends anything");

            // Stopping hosting changes nothing: offline every part may hold time again.
            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            That(bridge.Update() && round.IsActive, "a host that stops hosting keeps its round");

            // Joining another host ends everything this game held: it follows the host's time now.
            var local = tools.Freeze("local").Value!;
            var serverTurns = new RecordingScheduler();
            bridge.Track(serverTurns, services.Bind(Consumer, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), lifetime));
            control.Role = InternalSessionRole.Client;
            Frame(runtime);
            That(bridge.Update() && !round.IsActive && !local.IsActive && serverTurns.Disposals == 1 && bridge.FollowedLeases == 0,
                "a joined player ends every lease and scheduler it held, the gamemode server's included");

            // Leases released by their holders are let go of as new ones come, so following them stays bounded.
            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            bridge.Update();
            for (var index = 0; index < 100; index++) tools.Slow("blink", 0.5f).Value!.Release();
            That(bridge.FollowedLeases == 1, "released leases are let go of (" + bridge.FollowedLeases + " followed)");

            // A holder without the runtime's seam is single player, and nothing about it is followed.
            var single = new ChronosNetBridge(services.Bind(ChronosId, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), lifetime));
            var unseen = backend.ForConsumer(Consumer, lifetime, single, null).Freeze("test double").Value!;
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            That(single.Update() && unseen.IsActive && single.FollowedLeases == 0, "a lease from a context without the seam is never ended by a realm change");
            var detached = new ChronosNetBridge(null);
            That(!detached.Update() && detached.YieldsToNativePause && !detached.IsFollowing && detached.FollowedTime.Equals(InternalSessionTime.Realtime),
                "and a Chronos without the seam is single player throughout");
        }

        private static void TheClockYieldsToTheGamesPauseOnlyOffline()
        {
            var (services, control, runtime) = RuntimeReplicationTests.Services();
            var bridge = new ChronosNetBridge(services.Bind(ChronosId, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), new OwnerModLifetime()));
            That(bridge.YieldsToNativePause && !bridge.IsFollowing, "offline Chronos yields to the game's own pause menu, as it always has");
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            bridge.Update();
            That(!bridge.YieldsToNativePause && !bridge.IsFollowing, "a host's clock never yields to one player's menu");
            control.Role = InternalSessionRole.Solo;
            Frame(runtime);
            control.Role = InternalSessionRole.Client;
            Frame(runtime);
            bridge.Update();
            That(!bridge.YieldsToNativePause && bridge.IsFollowing, "nor a joined player's, which follows its host");
        }

        private static void AJoinedPlayerFollowsTheHostsTime()
        {
            var rig = new TimeRig(1);
            var player = rig.Players[0];
            var bridge = new ChronosNetBridge(player.Seam());
            rig.Host.Seam().PublishSessionTime(new InternalSessionTime(0f, InternalTimeMode.Paused));
            rig.StartHosting();
            rig.Network.Join(player.Control);
            player.Frame();
            bridge.Update();
            That(bridge.IsFollowing && bridge.FollowedTime.Equals(InternalSessionTime.Realtime),
                "joined, a player runs at real time until the host's time arrives");
            rig.Frames(1);
            That(bridge.FollowedTime.IsFrozen && bridge.FollowedTime.Mode == InternalTimeMode.Paused, "then follows the host's freeze, the moment it arrives");
            bridge.Publish(1f, TimeMode.Realtime);
            That(rig.Host.Time.Current.IsFrozen, "a joined player never publishes over its host's time");
        }

        private static void AHostPublishesWhatItsLeasesDerive()
        {
            var (services, control, runtime) = RuntimeReplicationTests.Services();
            var bridge = new ChronosNetBridge(services.Bind(ChronosId, new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), new OwnerModLifetime()));
            var clock = runtime.SessionTime;
            bridge.Publish(0.5f, TimeMode.Slowed);
            That(clock.Published.Scale == 0.5f && clock.Published.Mode == InternalTimeMode.Slowed, "offline the time is kept for a later promotion");

            // The bridge publishes only what changed: another writer's time stands until Chronos' own changes.
            clock.Publish(InternalSessionTime.Realtime);
            bridge.Publish(0.5f, TimeMode.Slowed);
            That(clock.Published.Equals(InternalSessionTime.Realtime), "the same derived time is not published again");
            bridge.Publish(float.NaN, TimeMode.Realtime);
            That(clock.Published.Equals(InternalSessionTime.Realtime), "a driver's scale that is not a number is never published");

            // A change of realm publishes again, so a new host starts from Chronos' own time.
            control.Role = InternalSessionRole.ListenHost;
            Frame(runtime);
            bridge.Update();
            bridge.Publish(0.5f, TimeMode.Slowed);
            That(clock.Current.Scale == 0.5f, "after a promotion the host's time is published again");
            bridge.Publish(0f, TimeMode.Paused);
            That(clock.Current.IsFrozen, "and every change after it");
        }

        private sealed class FixedDriver : ITimeDriver
        {
            public float ComputeScale(in TimeSignal signal) => 0.5f;
        }

        private sealed class RecordingScheduler : ITurnScheduler
        {
            internal int Disposals { get; private set; }

            public TurnState State => TurnState.Idle;

            public TurnActorId? CurrentActor => null;

            public int ActorCount => 0;

            public OperationResult<bool> Register(TurnActorId actor, float speed) => OperationResult<bool>.Success(true);

            public OperationResult<bool> Unregister(TurnActorId actor) => OperationResult<bool>.Success(true);

            public OperationResult<bool> BeginAction() => OperationResult<bool>.Success(true);

            public OperationResult<bool> EndAction() => OperationResult<bool>.Success(true);

            public void Tick(float controlDeltaTime)
            {
            }

            public void Dispose() => Disposals++;
        }
    }
}

namespace TopiaForge.Chronos
{
    // The owner facade the bridge tests drive, over the recording service backend the scoped-facade tests already compile.
    internal sealed partial class TimeControlService
    {
        internal ITimeControlService ForConsumer(string consumerId, IModLifetime lifetime, ChronosNetBridge net, IRuntimeReplication? consumer) =>
            new OwnerFacade(this, consumerId, lifetime, net, consumer);
    }
}
