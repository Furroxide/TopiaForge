using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.UnityUi;
using TopiaForge.RobotKit;

namespace TopiaForge.ModManager.Tests
{
    // Public player UI and command services over the production runtime; only native adapters use the existing kit.
    internal static class GuestBrainUiAuthorityTests
    {
        internal static void Run()
        {
            RemoteDismissal();
            NestedCommandAndUi();
        }

        private static void RemoteDismissal()
        {
            using var fixture = new Fixture();
            var observed = new List<Observation>();
            var remote = fixture.HostUi.Player(fixture.Guest.Control.Id).Ui.CreateSurface(
                new UiSurfaceRequest("remote-close", "Robot", "Close this window")).Value!;
            ((IUiSurfaceDismissalSource)remote).Dismissed += () => observed.Add(fixture.Observe());
            Check(fixture.Rig.Run(() => fixture.GuestUi.Ui.Surfaces.Count == 1), "remote dismissal surface arrives");
            fixture.GuestUi.Ui.Surfaces[0].Hide(); // Actual kind-15 reply, token/revision and validated sender path.
            Check(fixture.Rig.Run(() => observed.Count == 1), "remote dismissal invokes subscriber once");
            observed[0].Verify(guest: true);

            // The same host-owned remote surface can also be hidden by trusted server code.
            remote.Show();
            remote.Hide();
            Check(observed.Count == 2, "host dismissal invokes subscriber");
            observed[1].Verify(guest: false);
            var local = fixture.HostUi.Player(fixture.Rig.Host.Control.Id).Ui.CreateSurface(
                new UiSurfaceRequest("local-close", "Robot", "Host window")).Value!;
            ((IUiSurfaceDismissalSource)local).Dismissed += () => observed.Add(fixture.Observe());
            local.Hide();
            Check(observed.Count == 3, "native host UI dismissal invokes subscriber");
            observed[2].Verify(guest: false);
        }

        private static void NestedCommandAndUi()
        {
            using var fixture = new Fixture();
            var observed = new List<Observation>();
            var intents = fixture.Rig.Host.Runtime.Intents;
            var registry = new ModServiceRegistry();
            using var lifetime = new OwnerModLifetime();
            ICommandService service = new OwnerCommandService(Fixture.ServiceMod, lifetime,
                new OwnerFacadeStoppingTests.Logger(), registry, intents);
            var ranWithNoVisibleIntent = false;
            var localUiIntent = false;
            Check(service.Register(new CommandDefinition("robot", "Control robot"), _ =>
            {
                ranWithNoVisibleIntent = intents.VisibleTo(Fixture.ServiceMod) == null;
                observed.Add(fixture.Observe());
                var control = new UiButton("nested", "Robot", () =>
                {
                    localUiIntent = intents.VisibleTo(Fixture.ServiceMod)?.IsLocalPlayer == true;
                    observed.Add(fixture.Observe());
                });
                // The kit's actual activation hook replaces the record with the control owner's local UI intent.
                using (TopiaForgeIntent.PlayerAction(Fixture.ServiceMod)) control.Activated();
                return OperationResult<string>.Success("done");
            }).Succeeded, "public command registered");
            OperationResult<string>? commandResult = null;
            var hook = new MultiplayerIntentKitHook(intents);
            TopiaForgeIntent.Install(hook);
            try
            {
                var surface = fixture.HostUi.Player(fixture.Guest.Control.Id).Ui.CreateSurface(new UiSurfaceRequest(
                    "remote-control", "Robot", "", content: new UiColumn(new UiButton("run", "Run", () =>
                    {
                        // The forwarded UI belongs to mod A. A shared service owned by B executes B's command.
                        service.TryExecute("robot", Array.Empty<string>(), out commandResult);
                    })))).Value!;
                Check(fixture.Rig.Run(() => fixture.GuestUi.Ui.Surfaces.Count == 1), "remote controls arrive");
                var button = (UiButton)((UiColumn)fixture.GuestUi.Ui.Surfaces[0].Content!).Children[0];
                button.Activated(); // Kind-14 -> production ReceiveReply -> public command -> kit activation.
                Check(fixture.Rig.Run(() => observed.Count == 2), "both nested callbacks completed");
                Check(commandResult?.Succeeded == true && ranWithNoVisibleIntent && localUiIntent,
                    "actual dispatch clears cross-mod intent and actual kit activation installs local intent");
                foreach (var observation in observed) observation.Verify(guest: true);
                observed.Clear();
                Check(service.TryExecute("robot", Array.Empty<string>(), out commandResult) && commandResult!.Succeeded,
                    "trusted command works after outer forwarded callback ends");
                Check(observed.Count == 2, "trusted nested command/UI both completed");
                foreach (var observation in observed) observation.Verify(guest: false);
                lifetime.Dispose();
                Check(!service.TryExecute("robot", Array.Empty<string>(), out commandResult) &&
                    commandResult!.ErrorCode == ModErrorCode.Cancelled && observed.Count == 2,
                    "stopped command lifetime cannot invoke native effects");
                fixture.HostUi.Dispose();
                button.Activated();
                fixture.Rig.Frames(4);
                Check(observed.Count == 2, "retired UI lifetime cannot invoke stale callbacks");
                GC.KeepAlive(surface);
            }
            finally { TopiaForgeIntent.Uninstall(hook); }
        }

        private sealed class Fixture : IDisposable
        {
            internal const string ServiceMod = "io.example.robot-service";
            internal readonly WorldRig Rig = new WorldRig();
            internal readonly WorldPeer Guest;
            internal readonly PlayersTestServices HostUi, GuestUi;
            private readonly FirstPartyRobots robots;
            private readonly AuthorityMod server;
            private readonly IRobotAgent robot;

            internal Fixture()
            {
                Rig.UseScene(scene => scene.AddScene("Yard"));
                HostUi = Install(Rig.Host);
                Rig.Host.World.Proxies.IsSharedMod = mod => mod == WorldPeer.ModId || mod == ServiceMod || mod == "io.example.robot-provider";
                Rig.OpenToMultiplayer();
                Guest = Rig.AddPlayer("guest-ui");
                GuestUi = Install(Guest);
                robots = new FirstPartyRobots(Rig.Host);
                _ = new FirstPartyRobots(Guest);
                Rig.JoinAndSynchronize(Guest);
                server = new AuthorityMod(Rig.Host, ModExecutionContext.GamemodeServer, ServiceMod);
                robot = robots.Provider.Spawn(server.Seam, new RobotAgentSpawnRequest(Vec3.Zero,
                    interaction: RobotInteractionOptions.DisableNativeTalk())).Value!;
            }

            private PlayersTestServices Install(WorldPeer peer)
            {
                peer.Runtime.Disconnect();
                var backend = new FakePresenceBackend();
                var presence = MultiplayerPresence.Install(peer.Runtime, peer.Replication, backend, Rig.Clock.Read);
                var players = MultiplayerPlayers.Install(peer.Runtime, presence);
                peer.Runtime.Connect();
                return new PlayersTestServices(players, peer.Control, backend, WorldPeer.ModId);
            }

            internal Observation Observe()
            {
                var body = robots.Agents[robot.Id];
                var brains = body.BrainCalls; var talks = body.InteractionCalls;
                var brain = robot.SetBrainMode(RobotBrainMode.Autonomous);
                var talk = robot.SetInteraction(RobotInteractionOptions.NativeTalk());
                var count = robots.Agents.Count;
                var records = Rig.Host.Runtime.Spawns.Live().Count;
                var spawn = robots.Provider.Spawn(server.Seam, new RobotAgentSpawnRequest(Vec3.Zero,
                    brainMode: RobotBrainMode.Autonomous, interaction: RobotInteractionOptions.NativeTalk()));
                RobotAgentSpawnRequest? construction = null, replay = null;
                if (spawn.TryGetValue(out var made))
                {
                    var native = robots.Agents[made.Id];
                    construction = native.Request;
                    // Read synchronously before any tick/RefreshEntityDescription can repair the initial ledger.
                    var record = Rig.Host.Runtime.Spawns.Find(native);
                    if (record?.Description.Descriptor is InternalSpawnDescriptor descriptor)
                        replay = RobotReplicationWire.Spawn(descriptor.Payload).Request;
                }
                var spawned = robots.Agents.Count - count;
                var recorded = Rig.Host.Runtime.Spawns.Live().Count - records;
                var dormant = robots.Provider.Spawn(server.Seam, new RobotAgentSpawnRequest(Vec3.Zero));
                Check(dormant.TryGetValue(out var safe), "Dormant spawn still succeeds inside either callback");
                var dormantBody = robots.Agents[safe!.Id];
                var dormantRecord = Rig.Host.Runtime.Spawns.Find(dormantBody)!;
                var dormantReplay = RobotReplicationWire.Spawn(((InternalSpawnDescriptor)dormantRecord.Description.Descriptor!).Payload).Request;
                return new Observation(brain, talk, body.BrainCalls - brains, body.InteractionCalls - talks,
                    spawn, spawned, recorded, construction, replay, dormantBody.Request, dormantReplay);
            }

            public void Dispose() { server.Lifetime.Dispose(); GuestUi.Dispose(); HostUi.Dispose(); }
        }

        private sealed class Observation
        {
            private readonly OperationResult<bool> brain, talk;
            private readonly int brains, talks;
            private readonly RobotAgentSpawnRequest? construction, replay;
            private readonly RobotAgentSpawnRequest dormant, dormantReplay;
            private readonly OperationResult<IRobotAgent> spawn;
            private readonly int spawned, recorded;
            internal Observation(OperationResult<bool> brain, OperationResult<bool> talk, int brains, int talks,
                OperationResult<IRobotAgent> spawn, int spawned, int recorded,
                RobotAgentSpawnRequest? construction, RobotAgentSpawnRequest? replay,
                RobotAgentSpawnRequest dormant, RobotAgentSpawnRequest dormantReplay)
            {
                this.brain = brain; this.talk = talk; this.brains = brains; this.talks = talks;
                this.spawn = spawn; this.spawned = spawned; this.recorded = recorded;
                this.construction = construction; this.replay = replay;
                this.dormant = dormant; this.dormantReplay = dormantReplay;
            }

            internal void Verify(bool guest)
            {
                Check(guest ? brain.ErrorCode == ModErrorCode.NotAuthoritative && talk.ErrorCode == ModErrorCode.NotAuthoritative
                    : brain.Succeeded && talk.Succeeded, "wake result follows authenticated callback origin");
                Check(brains == (guest ? 0 : 1) && talks == (guest ? 0 : 1), "wake guard runs before either native adapter effect");
                Check(guest ? spawn.ErrorCode == ModErrorCode.NotAuthoritative && spawn.Value == null : spawn.Succeeded,
                    "host Autonomous spawn reports the authenticated callback restriction");
                Check(spawned == (guest ? 0 : 1) && recorded == (guest ? 0 : 1),
                    "Autonomous refusal occurs before construction and initial ledger insertion");
                foreach (var request in new[] { construction, replay })
                    Check(guest ? request == null : request != null && request.BrainMode == RobotBrainMode.Autonomous &&
                        request.Interaction.NativeTalkMode == RobotNativeTalkMode.Enabled,
                        "only trusted Autonomous requests reach construction or initial replay");
                foreach (var request in new[] { dormant, dormantReplay })
                    Check(request.BrainMode == RobotBrainMode.Dormant && request.Interaction.NativeTalkMode ==
                        (guest ? RobotNativeTalkMode.Disabled : RobotNativeTalkMode.Enabled),
                        "Dormant/default spawn preserves its existing canonical behavior");
            }
        }

        private static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException("Guest UI brain authority: " + message); }
    }
}
