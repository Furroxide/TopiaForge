using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModRuntime.Tests
{
    internal static partial class Program
    {
        private static void TestSpeechReadinessFollowsProvider(string root)
        {
            var network = new LoopbackNetPair();
            var clock = new WorldTestClock();
            var host = new PresenceGame(NewFixture(Path.Combine(root, "speech-admission-host"), "presence", PresenceModEntry), network.Host, clock, "Host");
            var player = new PresenceGame(NewFixture(Path.Combine(root, "speech-admission-player"), "presence", PresenceModEntry), network.AddPlayer("listener"), clock, "Listener");
            var pair = new[] { host, player };
            try
            {
                host.Start(); player.Start();
                Assert(host.Runtime.Multiplayer.OpenToMultiplayer(new InternalHostSettings("Speech", 4)).Succeeded, "speech admission host opens");
                PresenceFrames(pair, clock, 3);
                Assert(player.Runtime.Multiplayer.Join(new FakeJoinTarget(), new InternalJoinSettings()).Succeeded, "speech admission player joins");
                PresenceFrames(pair, clock, 120);
                var channel = player.Control.Channel(InternalSystemChannelKind.WorldReplication)!;
                var sent = channel.SentMessages;
                using var ordinary = player.Runtime.Replication.AddFactory("tests.ordinary", "ordinary", new AdmissionReplicaFactory());
                PresenceFrames(pair, clock, 120);
                Assert(channel.SentMessages == sent, "an ordinary replica factory does not advertise speech readiness");
                var handled = host.Runtime.NetworkWorld.Channel.Handled;
                using (var speech = player.Runtime.Replication.AddFactory("tests.speech", "speech", new AdmissionSpeechFactory()))
                {
                    PresenceFrames(pair, clock, 4);
                    Assert(channel.SentMessages > sent && host.Runtime.NetworkWorld.Channel.Handled > handled,
                        "installing a speech provider advertises readiness that the real host accepts");
                }
                sent = channel.SentMessages;
                PresenceFrames(pair, clock, 120);
                Assert(channel.SentMessages == sent, "disposing the speech provider stops readiness heartbeats");
                using var replacement = player.Runtime.Replication.AddFactory("tests.speech", "speech", new AdmissionSpeechFactory());
                PresenceFrames(pair, clock, 4);
                Assert(channel.SentMessages > sent, "a replacement speech provider resumes readiness without author work");
                Console.WriteLine("Speech readiness: absent, ordinary, installed, disposed and replacement providers passed.");
            }
            finally
            {
                foreach (var game in pair) game.Runtime.UnloadAll();
            }
        }

        private class AdmissionReplicaFactory : IInternalReplicaFactory
        {
            public OperationResult<IEntity> CreateReplica(InternalSpawnDescriptor descriptor) =>
                OperationResult<IEntity>.Failure(ModErrorCode.Unavailable, "This admission fixture builds no bodies.");
            public void ReleaseReplica(IEntity replica) { }
        }

        private sealed class AdmissionSpeechFactory : AdmissionReplicaFactory, IInternalSpeechProvider
        {
            public bool IsSpeechVisible(IEntity entity) => false;
            public OperationResult<string> VoiceIdentity(IEntity entity) =>
                OperationResult<string>.Failure(ModErrorCode.Unavailable, "This admission fixture synthesizes no speech.");
            public Task<OperationResult<bool>> PlaySpeechAsync(IEntity entity, string voice, string text, CancellationToken cancellationToken) =>
                throw new InvalidOperationException("Readiness must not synthesize speech.");
        }
    }
}
