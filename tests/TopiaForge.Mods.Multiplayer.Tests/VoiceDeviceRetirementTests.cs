extern alias provider;

using TopiaForge.ModManager;
using TopiaForge.ModManager.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using static TopiaForge.Mods.Multiplayer.Tests.ControlVoiceSteps;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class VoiceDeviceRetirementTests
{
    private static readonly PackageSpec Package = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a');

    internal static void Run()
    {
        var failures = new List<string>();
        foreach (var hostSpeaker in new[] { true, false })
        {
            using var rig = ControlRig.WithVoice(347, Package);
            Host(rig);
            var (remote, _) = Join(rig, "sam", Package);
            var (listener, ears) = Join(rig, "lee", Package);
            var speaker = hostSpeaker ? rig.Host : remote;
            var name = hostSpeaker ? "Hoster" : "Sam";
            RunUntilListed(rig, listener, "sam");
            var speakerId = speaker.IdOf(name);
            var listenerId = listener.IdOf("Lee");
            ears.Listen("private", speakerId);
            var microphone = new BufferedMicrophone();
            var input = new HeldKeys();
            input.Hold(VoicePushToTalk.DefaultKey);
            using var runtime = new VoiceChatRuntime(() => speaker.Control, () => speaker.Session.Snapshot,
                new MultiplayerMicrophoneArbiter(), microphone, input, new ListeningEmitters(rig.Network));
            speaker.ModsUpdate = () => runtime.Tick(0.005f);
            runtime.VoiceEnabled = true;
            RunFor(rig, 20_000);
            That(!microphone.IsOpen, "without current consent the device stays closed");
            runtime.ConsentCurrent = true;
            RunFor(rig, 30_000);
            That(microphone.IsOpen && runtime.Transmitting, "consented held push-to-talk opens capture");
            using var lifetime = new FakeModLifetime();
            var members = rig.Host.Session.Snapshot.Participants.Select(participant =>
                new CommunicationMember(participant.Id, participant.Id.Equals(speakerId) ? 1 : 2)).ToArray();
            using var route = rig.Host.Provider.Engine.RouteCommunications(lifetime,
                new CommunicationPolicy(members, Array.Empty<CommunicationRoute>())).Value!;
            var signal = Speech(0.02, 81);
            That(signal.Length == 960, "exactly 20 ms of unread device PCM");
            microphone.Record(signal);
            var submitted = runtime.Capture.SubmittedSamples;
            var oldToken = speaker.Session.CurrentSessionToken;
            var workerSteps = speaker.Provider.Voice!.Worker.Steps;
            rig.Host.Provider.Engine.ReplaceSession(new MultiplayerSessionId("device-replacement"), new SessionSeed(47));
            var acknowledged = false;
            for (var step = 0; step < 40; step++)
            {
                // Provider pumps precede microphone reads. Keep the speaker worker paused until replacement is ready.
                foreach (var peer in rig.Peers.Prepend(rig.Host))
                {
                    peer.Control.Pump();
                    if (!ReferenceEquals(peer, speaker) || speaker.Session.CurrentSessionToken != oldToken)
                        peer.ModsUpdate?.Invoke();
                    peer.Control.Flush();
                    if (!ReferenceEquals(peer, speaker)) peer.Provider.Voice!.Worker.Step(rig.Network.Now);
                    peer.AfterFrame?.Invoke();
                }
                acknowledged = speaker.Session.CurrentSessionToken != oldToken &&
                    speaker.Session.Snapshot.State == MultiplayerSessionState.Ready &&
                    rig.Host.Provider.Engine.IsParticipantReady(listenerId);
                if (acknowledged) break;
                rig.Network.RunFor(5_000);
            }
            That(acknowledged, "formerly excluded listener acknowledges the replacement");
            That(speaker.Provider.Voice.Worker.Steps == workerSteps, "worker stayed paused across unread device capture");
            speaker.Provider.Voice.Worker.Step(rig.Network.Now);
            RunFor(rig, 400_000);
            if (runtime.Capture.SubmittedSamples != submitted || ears.Heard("private").Any(sample => sample != 0))
                failures.Add((hostSpeaker ? "host" : "client") + " unread private device PCM reached replacement capture/playback: submitted=" +
                    (runtime.Capture.SubmittedSamples - submitted) + ", audible=" + ears.Heard("private").Count(sample => sample != 0));
            That(runtime.ConsentCurrent && runtime.VoiceEnabled && microphone.IsOpen && runtime.Transmitting,
                "capture resumes automatically with existing consent and held push-to-talk");
            var freshFrom = ears.Heard("private").Count;
            for (var frame = 0; frame < 10; frame++)
            {
                microphone.Record(Speech(0.02, (uint)(82 + frame)));
                RunFor(rig, 20_000);
            }
            RunFor(rig, 300_000);
            That(ears.Heard("private").Skip(freshFrom).Any(sample => sample != 0), "fresh replacement PCM reaches the listener");
            runtime.ConsentCurrent = false;
            RunFor(rig, 20_000);
            That(!microphone.IsOpen && !runtime.Transmitting, "withdrawn consent closes capture and stops transmission");
        }
        That(failures.Count == 0, string.Join("; ", failures));
        Console.WriteLine("Voice device retirement: host/client unread 20 ms private PCM discarded with worker paused; acknowledgements, consent and fresh capture passed.");
    }

    // Models the unread clip, rather than calling SubmitCapture directly. Closing a recording loses its device backlog.
    private sealed class BufferedMicrophone : IVoiceMicrophone
    {
        private readonly Queue<float> unread = new();
        public bool IsOpen { get; private set; }
        public int SampleRate => 48_000;
        public IReadOnlyList<string> ListDevices() => Array.Empty<string>();
        internal void Record(float[] samples)
        {
            That(IsOpen, "PCM was recorded while the microphone was open");
            foreach (var sample in samples) unread.Enqueue(sample);
        }
        public VoiceMicrophoneOpen Open(string? device) { unread.Clear(); IsOpen = true; return VoiceMicrophoneOpen.Opened; }
        public int Read(float[] buffer, int offset, int count)
        {
            var take = IsOpen ? Math.Min(count, unread.Count) : 0;
            for (var index = 0; index < take; index++) buffer[offset + index] = unread.Dequeue();
            return take;
        }
        public void Close() { unread.Clear(); IsOpen = false; }
        public void Abandon() { unread.Clear(); IsOpen = false; }
        public void EndAbandoned() { }
    }
}
