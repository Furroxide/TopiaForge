using System;
using System.IO;
using System.Threading;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager.Tests
{
    internal static class CommunicationRuntimeTests
    {
        internal static void Run()
        {
            ContextAuthority();
            ValidatedPresence();
            GlobalPlayback();
            Console.WriteLine("CommunicationRuntimeTests: 3 groups passed (context authority, validated presence, global playback).");
        }

        private static void ContextAuthority()
        {
            using var dispatcher = new HostDispatcher();
            var rig = new PresenceRig();
            rig.HostWith(1);
            var policy = new CommunicationPolicy(Array.Empty<CommunicationMember>(), Array.Empty<CommunicationRoute>());
            foreach (var node in new[] { rig.Host, rig.Players[0] })
            {
                var paths = new ManagerPaths(Path.Combine(Path.GetTempPath(), "TopiaForgeCommunication-" + Guid.NewGuid().ToString("N")));
                var manifest = new ModManifest { SchemaVersion = 6, Id = "communication.test", Name = "Communication", Version = "1.0.0", EntryAssembly = "Test.dll", EntryType = "Test.Entry" };
                var parent = new ModContext(manifest, paths, "package", new OwnerFacadeStoppingTests.Logger(),
                    new ModServiceRegistry(), null, null, replicationServices: node.Replication);
                try
                {
                    That(parent.RouteCommunications(policy).ErrorCode == ModErrorCode.NotAuthoritative, "ordinary root cannot acquire routing");
                    foreach (var execution in new[] { ModExecutionContext.Participant, ModExecutionContext.WorldOwner, ModExecutionContext.GamemodeServer })
                    {
                        var id = execution.ToString();
                        var creation = parent.CreateChildScopeAsync(id, CancellationToken.None, () => { },
                            new NativeTransitionAccessSlot(id, id, () => true), dispatcher, execution);
                        HostDispatcherTests.Pump(dispatcher, creation);
                        using var scope = creation.Result;
                        var result = scope.Context.RouteCommunications(policy);
                        var expected = execution == ModExecutionContext.GamemodeServer && ReferenceEquals(node, rig.Host)
                            ? ModErrorCode.Unavailable : ModErrorCode.NotAuthoritative;
                        // The loopback control deliberately has no routing seam; only an authoritative scope reaches it.
                        That(result.ErrorCode == expected, "routing authority follows execution context and realm");
                        scope.BeginStop();
                        That(scope.Context.RouteCommunications(policy).ErrorCode == ModErrorCode.Cancelled, "stopping scope cannot reacquire");
                    }
                }
                finally { parent.DisposeLifetime(); }
            }
        }

        private static void ValidatedPresence()
        {
            var rig = new PresenceRig();
            rig.HostWith(1);
            rig.Frames(20);
            var player = rig.Players[0];
            var source = (IInternalCommunicationPositionSource)rig.Host.Presence;
            That(source.TryGetCommunicationPosition(player.Id, out _) && source.TryGetCommunicationPosition(rig.Host.Id, out _), "host exposes fresh validated remote and local positions");
            That(!((IInternalCommunicationPositionSource)player.Presence).TryGetCommunicationPosition(player.Id, out _), "client pose cannot act as routing authority");
            player.Paused = true;
            rig.Clock.Advance(PresencePolicy.DefaultStaleAfterMicros + 1);
            That(!source.TryGetCommunicationPosition(player.Id, out _), "stale peer position fails closed");
            rig.Host.World = "replacement";
            That(!source.TryGetCommunicationPosition(rig.Host.Id, out _), "world changes invalidate local position before the next frame");
            rig.Host.World = null;
            rig.Frames(2);
            That(!source.TryGetCommunicationPosition(rig.Host.Id, out _), "menu has no communication position");
        }

        private static void GlobalPlayback()
        {
            var speaker = new ParticipantId("p2");
            var voice = new FakeVoiceService();
            var stream = new FakePlaybackStream(speaker);
            voice.Streams.Add(speaker, stream);
            voice.Speaking.Add(speaker);
            var emitters = new FakeEmitters();
            var playback = new VoicePlayback(emitters) { Positions = new Positions() };
            var snapshot = VoiceRig.Session("communication", VoiceRig.Player("p2", "Sam", "speaker"));
            playback.Tick(voice, snapshot);
            That(emitters.Made[0].Position.HasValue, "ordinary voice is positional");
            stream.NonSpatial = true;
            playback.Tick(voice, snapshot);
            That(!emitters.Made[0].Position.HasValue, "global broadcast unplaces existing emitter");
            stream.NonSpatial = false;
            playback.Tick(voice, snapshot);
            That(emitters.Made[0].Position.HasValue, "returning to proximity restores positional playback");
        }

        private sealed class Positions : IVoicePositions
        {
            public bool TryGetPosition(ParticipantId speaker, out VoicePosition position)
            { position = new VoicePosition(100, 0, 0); return true; }
        }

        private static void That(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    }
}
