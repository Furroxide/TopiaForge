extern alias provider;

using provider::TopiaForge.Multiplayer.Engine;
using provider::TopiaForge.Multiplayer.Voice;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using static TopiaForge.Mods.Multiplayer.Tests.CommunicationRoutingTests;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class CommunicationPlaybackTests
{
    internal static void Run()
    {
        ReceivedGlobalSpeech();
        PresentationOrdering();
        BufferedTransitions();
        Console.WriteLine("CommunicationPlaybackTests: 3 groups passed (real codec global route, presentation ordering/reset, four buffered host/remote transitions).");
    }

    private static void ReceivedGlobalSpeech()
    {
        using var rig = new VoiceServiceRig();
        var host = rig.AddHost();
        var listener = rig.AddClient("listener");
        using var lifetime = new FakeModLifetime();
        var members = new[] { Member("host", 1), Member("listener", 1) };
        using var route = host.Engine.RouteCommunications(lifetime,
            new CommunicationPolicy(members, new[] { new CommunicationRoute(1, 1) })).Value!;
        var stream = listener.Listen("host");
        host.Source = VoiceSource.Speech(2, seed: 71);
        rig.StepFor(500_000);
        That(((IInternalRoutedVoiceStream)stream).NonSpatial && listener.Heard("host").Any(sample => sample != 0),
            "received global flag reaches the real decoded stream and audio is present");
        route.Dispose();
        rig.StepFor(500_000);
        That(stream.IsEnded && !((IInternalRoutedVoiceStream)listener.Listen("host")).NonSpatial,
            "ordinary frames restore positional playback on a fresh stream");
    }

    private static void BufferedTransitions()
    {
        foreach (var hostPlayback in new[] { true, false })
            foreach (var initialGlobal in new[] { true, false })
            {
                using var rig = new VoiceServiceRig();
                var host = rig.AddHost();
                var speaker = rig.AddClient("speaker");
                var remote = rig.AddClient("listener");
                var listener = hostPlayback ? host : remote;
                using var lifetime = new FakeModLifetime();
                var members = new[] { Member("host", 1), Member("speaker", 1), Member("listener", 1) };
                var positions = new Positions();
                positions.Values["host"] = positions.Values["listener"] = new Vec3(50, 0, 0);
                positions.Values["speaker"] = new Vec3(0, 0, 0);
                using var source = host.Engine.SetAvatarSource(positions);
                CommunicationPolicy Policy(bool global) => new(members, new[] { new CommunicationRoute(1, 1, global ? 0 : 100) });
                using var route = host.Engine.RouteCommunications(lifetime, Policy(initialGlobal)).Value!;
                var old = listener.Listen("speaker");
                speaker.Source = VoiceSource.Speech(4, seed: 72);
                rig.StepFor(500_000);
                That(old.NonSpatial == initialGlobal && listener.Heard("speaker").Any(sample => sample != 0),
                    "authorized speech at 50 m reaches real host/remote decoder before transition");
                listener.StopListening("speaker");
                rig.StepFor(40_000);
                That(old.Buffered > 0, "decoded audio waits during presentation transition");
                listener.WorkerStalled = true;
                route.SetPolicy(Policy(!initialGlobal));
                rig.StepFor(100_000);
                var next = listener.Listen("speaker");
                That(old.IsEnded && !ReferenceEquals(old, next) && old.NonSpatial == initialGlobal && next.NonSpatial != initialGlobal,
                    "transition retires old presentation and gives the emitter a fresh stream");
                var audio = Enumerable.Repeat(1f, 480).ToArray();
                old.Read(audio, 0, audio.Length);
                That(audio.All(sample => sample == 0), "retired stream cannot play buffered audio under either presentation");
                // An already-running worker may finish writing after retirement; those samples must stay silent too.
                old.Write(Enumerable.Repeat((short)1000, 960).ToArray());
                old.Read(audio, 0, audio.Length);
                That(audio.All(sample => sample == 0), "late worker writes never revive a retired emitter");
                That(next.Buffered == 0, "replacement has no old decoded audio while worker is stalled");
                listener.WorkerStalled = false;
                rig.StepFor(400_000);
                That(listener.Heard("speaker").TakeLast(4800).Any(sample => sample != 0),
                    "fresh authorized speech resumes after jitter-buffer retirement");
                listener.Voice!.SetListenerMutes(new[] { new ParticipantId("speaker") });
                rig.StepFor(300_000);
                route.SetPolicy(Policy(initialGlobal));
                rig.StepFor(300_000);
                That(listener.Heard("speaker").TakeLast(4800).All(sample => sample == 0),
                    "presentation transition does not bypass listener mute");
                speaker.Voice!.Enabled = false;
                listener.Voice.SetListenerMutes(Array.Empty<ParticipantId>());
                rig.StepFor(300_000);
                route.SetPolicy(Policy(!initialGlobal));
                rig.StepFor(300_000);
                That(listener.Heard("speaker").TakeLast(4800).All(sample => sample == 0),
                    "presentation transition does not restart a disabled microphone");
            }
    }

    private static void PresentationOrdering()
    {
        var stream = new VoicePlaybackStream(new ParticipantId("speaker"));
        VoiceFrame Frame(ulong generation, ulong sequence, bool global) => new(new ParticipantId("speaker"), generation,
            sequence, sequence, global ? VoiceFrameFlags.NonSpatial : VoiceFrameFlags.None, new ArraySegment<byte>(new byte[] { 1 }), 0);
        stream.Route(Frame(2, 5, true));
        stream.Route(Frame(2, 4, false));
        stream.Route(Frame(1, 100, false));
        That(stream.NonSpatial, "late frames cannot change current routing presentation");
        stream.Route(Frame(3, 1, false));
        That(!stream.NonSpatial, "new stream generation replaces presentation");
        stream.Route(Frame(3, 2, true));
        stream.ResetRouting();
        That(stream.NonSpatial, "session reset preserves buffered presentation until fresh audio arrives");
        stream.Route(Frame(1, 1, true));
        That(stream.NonSpatial, "new session can restart speaker numbering");
    }
}
