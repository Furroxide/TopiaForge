using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// A listen host's side of session follow (WS3.8): it announces what its game is in, its running world session with the
    /// launch it made, the level it is in outside one, or the main menu, to every player when that changes and when a stream
    /// starts again, catches up a player whose window was full, drops what players send, and at rest sends and allocates
    /// nothing.
    /// </summary>
    internal static class SessionFollowHostTests
    {
        internal static void Run(string root)
        {
            AnnouncesWhatTheHostIsIn(root + "-announces");
            AnArenaCarriesItsLevelAndSpawn(root + "-arena");
            AFullWindowIsCaughtUp(root + "-window");
            Console.WriteLine("SessionFollowHostTests passed.");
        }

        private static void AnnouncesWhatTheHostIsIn(string root)
        {
            using var rig = new HostRig(root);
            var guest = rig.AddGuest("guest");
            rig.Frame();
            That(rig.Follow.Host.IsActive && rig.Received(guest).Single().Kind == HostWorldKind.Menu, "a host at the main menu says so to a player that joins");

            rig.Test.Launch("host-world");
            rig.Frame();
            var world = rig.Received(guest).Single();
            var descriptor = rig.Test.Plan.Descriptor;
            That(world.Kind == HostWorldKind.World && world.SessionId == rig.Test.Hosted.Current.Identity!.SessionId
                && world.Request!.TargetId == descriptor.Request.TargetId && world.GamemodeId == descriptor.GamemodeId && world.WorldId == descriptor.WorldId
                && world.Transition == descriptor.Transition && world.Scene == null && !world.Spawn.HasValue,
                "once its world runs, the host announces its session id and the launch it made");

            var sent = rig.Follow.Host.Sent;
            for (var frame = 0; frame < 30; frame++) rig.Frame();
            That(rig.Follow.Host.Sent == sent && rig.Received(guest).Count == 0, "at rest it sends nothing");
            var allocated = Allocated(() => rig.Follow.Run(0.016f));
            That(allocated == 0, "and allocates nothing (" + allocated + " bytes)");

            var late = rig.AddGuest("late");
            rig.Frame();
            That(rig.Received(late).Single().SameAs(world) && rig.Received(guest).Single().SameAs(world),
                "a stream that starts again re-announces the world to everyone, and the late player hears it at once");
            rig.Net.Rejoin(guest);
            rig.Frame();
            That(rig.Received(guest).Single().SameAs(world), "and so does a player's rejoin");

            var menu = rig.Test.Hosted.ReturnToMainMenuAsync(rig.Test.Hosted.Current.Identity!.SessionId);
            rig.Until(() => menu.IsCompleted);
            rig.Scene.Activated(GameScenes.MainMenuSceneName);
            rig.Frame();
            That(rig.Received(guest).Single().Kind == HostWorldKind.Menu, "a host that returns to the main menu says so");

            rig.Scene.Activated("Downtown");
            rig.Frame();
            var level = rig.Received(guest).Single();
            That(level.Kind == HostWorldKind.Level && level.Scene == "Downtown", "a level the host is in outside a session is announced by its name");
            rig.Scene.Activated("LoadingScreenLoader");
            rig.Frame();
            That(rig.Received(guest).Count == 0, "a loading scene between levels changes nothing");

            rig.SendFromGuest(guest);
            rig.Frame();
            That(rig.Follow.Host.Dropped == 1, "what a player sends on the channel is taken and dropped");
        }

        private static void AnArenaCarriesItsLevelAndSpawn(string root)
        {
            using var rig = new HostRig(root, configure: manifest => manifest.Contributions!.LaunchTargets[0].Transition = ModTransitions.AdditiveArena);
            var spawn = new TransformState(new Vec3(3f, 0f, -7f), Quat.Identity, new Vec3(1f, 1f, 1f));
            SessionFixture.Load = (_, _) => Task.FromResult(OperationResult<IWorldInstance>.Success(new ArenaWorld(spawn)));
            var guest = rig.AddGuest("guest");
            rig.Test.Launch("host-arena");
            rig.Frame();
            var world = rig.Received(guest).Last();
            That(world.Kind == HostWorldKind.World && world.Transition == ModTransitions.AdditiveArena && world.Scene == "Downtown" && Equals(world.Spawn, spawn),
                "an additive arena names the level it is built over and where the host's player stood as it was built");
        }

        private static void AFullWindowIsCaughtUp(string root)
        {
            using var rig = new HostRig(root);
            rig.Net.HostWindowMessages = 1;
            var guest = rig.AddGuest("guest");
            rig.Frame();
            rig.Test.Launch("host-world");
            rig.Frame();
            var latest = rig.Follow.Host.Announced!;
            That(latest.Kind == HostWorldKind.World, "the host's world is the newest announcement");
            var first = rig.Received(guest);
            That(first.Count == 1 && first[0].Kind == HostWorldKind.Menu, "a player whose window was full missed the world");
            rig.CreditGuest(guest);
            rig.Frame();
            That(rig.Received(guest).Single().SameAs(latest), "once its window has room, it gets the newest announcement alone");
        }

        /// <summary>A world whose readiness says where its arena is: the level it is in, and the spawn it was built at.</summary>
        private sealed class ArenaWorld : IWorldInstance
        {
            internal ArenaWorld(TransformState spawn)
            {
                Readiness = new WorldReadiness(new WorldSceneIdentity(-31, "Downtown"), spawn);
            }

            public WorldReadiness Readiness { get; }

            public void Dispose()
            {
            }
        }

        /// <summary>A listen host's composition root, orchestrator and session follow, and its players' raw channels.</summary>
        private sealed class HostRig : IDisposable
        {
            private readonly Dictionary<string, LoopbackControl> guests = new Dictionary<string, LoopbackControl>();
            private readonly WorldWireReader reader = new WorldWireReader();

            internal HostRig(string root, Action<ModManifest>? configure = null)
            {
                Runtime = new MultiplayerRuntime(() => Net.Host, new MultiplayerRuntimeHost("2545", "0.2.0", Log), Log);
                Test = new SessionFixture(root, replication: new MultiplayerReplication(Runtime), configure: configure);
                Runtime.SessionPhaseSource = () => Test.Hosted.RealmPhase;
                Follow = new SessionFollow(Test.Hosted, Scene, new MultiplayerSceneTransitionAuthorityPolicy(() => null).GrantFollow, () => Runtime.Leave(),
                    () => new NetworkWorldProgress(NetworkWorldPhase.Hosting, 0, 0, 0), null, () => Runtime.Control == null, () => Runtime.LaunchedJoin,
                    Test.Host, Log);
                Runtime.AddObserver(Follow);
                Runtime.SessionFollow = Follow;
                Scene.Activated(GameScenes.MainMenuSceneName);
                That(Runtime.Connect(), "the host's runtime attaches");
                That(Net.Host.StartHosting(new InternalHostSettings("Follow host", 4)).Succeeded, "the loopback host hosts");
                Frame();
                That(Runtime.Realm == NetRealm.ListenHost, "the host is a listen host");
            }

            internal LoopbackNetPair Net { get; } = new LoopbackNetPair();

            internal RecordingRuntimeLog Log { get; } = new RecordingRuntimeLog();

            internal MultiplayerRuntime Runtime { get; }

            internal SessionFixture Test { get; }

            internal FollowedScene Scene { get; } = new FollowedScene();

            internal SessionFollow Follow { get; }

            internal LoopbackControl AddGuest(string id)
            {
                var guest = Net.AddPlayer(id);
                That(guest.OpenSystemChannel(InternalSystemChannelKind.SceneFollow).Succeeded, id + " opens its follow channel");
                guests[id] = guest;
                Net.Join(guest);
                guest.Pump();
                return guest;
            }

            /// <summary>Takes what the host sent <paramref name="guest"/> since the last look, decoded; each message must read.</summary>
            internal List<HostWorldState> Received(LoopbackControl guest)
            {
                guest.Pump();
                var channel = guest.Channel(InternalSystemChannelKind.SceneFollow)!;
                var received = new List<HostWorldState>();
                while (channel.TryReceive(out var message))
                {
                    That(SessionFollowWire.TryRead(reader, message.Payload, out var state), "every announcement reads");
                    received.Add(state!);
                }

                guest.Flush();
                return received;
            }

            /// <summary>Gives the host's window to <paramref name="guest"/> room again, as the guest's runtime would by reading.</summary>
            internal void CreditGuest(LoopbackControl guest)
            {
                guest.Pump();
                var channel = guest.Channel(InternalSystemChannelKind.SceneFollow)!;
                while (channel.TryReceive(out _))
                {
                }

                guest.Flush();
            }

            internal void SendFromGuest(LoopbackControl guest)
            {
                var channel = guest.Channel(InternalSystemChannelKind.SceneFollow)!;
                That(channel.Send(new ArraySegment<byte>(new byte[] { 1, 3 }), InternalSystemTarget.Host).Succeeded, "a player sends on the channel");
                guest.Flush();
            }

            internal void Frame()
            {
                Runtime.BeforeModsUpdate(0.016f);
                Test.Host.Drain();
                Runtime.AfterModsUpdate(0.016f);
                Test.Host.Drain();
            }

            internal void Until(Func<bool> done)
            {
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (!done() && DateTime.UtcNow < deadline)
                {
                    Frame();
                    Thread.Sleep(1);
                }

                That(done(), "timed out waiting for the host");
            }

            public void Dispose()
            {
                Runtime.Disconnect();
                Test.Dispose();
            }
        }
    }
}
