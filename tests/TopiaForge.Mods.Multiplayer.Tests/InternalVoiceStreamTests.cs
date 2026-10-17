extern alias provider;

using System.Reflection;
using System.Runtime.CompilerServices;
using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Voice;
using static TopiaForge.Mods.Multiplayer.Tests.ControlVoiceSteps;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;
using ProviderVoice = provider::TopiaForge.Multiplayer.ProviderVoiceService;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The voice SPI's playback streams, statistics, threads and privacy, over real providers on the deterministic network.
/// A stream is the voice service's own and ends for good; asking again for one the session has no use for costs nothing;
/// every counter is mapped by what it means; every member but a stream's read belongs to the main thread; and nothing the
/// voice SPI names or answers is shaped like an address.
/// </summary>
internal static class InternalVoiceStreamTests
{
    private const double Heard = -35;
    private static readonly PackageSpec Sandbox = PackageSpec.Implicit("io.example.sandbox", "0.2.0", 'a');

    internal static void PlaybackStreamsAreTheServicesOwnAndEndForGood()
    {
        using var rig = ControlRig.WithVoice(336, Sandbox);
        Host(rig);
        var (sam, mic) = Join(rig, "sam", Sandbox);
        var (lee, ears) = Join(rig, "lee", Sandbox);
        var voice = ears.Service;
        RunUntilListed(rig, lee, "sam");
        var samId = IdOf(lee, "sam");
        var stream = voice.GetPlaybackStream(samId);
        That(stream is VoicePlaybackStream && ReferenceEquals(stream, lee.Provider.Voice!.GetPlaybackStream(samId)),
            "the SPI hands out the voice service's own stream, never a wrapper");
        That(voice.TryGetPlaybackStream(samId, out var again) && ReferenceEquals(again, stream), "asking again gives the same stream");
        That(stream.Speaker.Equals(samId) && stream.SampleRate == 48_000 && !stream.IsEnded, "Sam's stream, at 48 kHz");
        ears.Listen("sam", stream);
        mic.Talk(Speech(1, 71));
        RunFor(rig, 1_400_000);
        That(ears.LevelSince("sam", 0) > Heard, "Lee hears Sam on it");

        // Sam leaves and comes back within the grace: the host kept Sam's record, so the stream is the same and plays on.
        sam.Control.Leave();
        That(rig.Run(() => sam.Control.Role == InternalSessionRole.Solo), "Sam leaves");
        RunFor(rig, 500_000);
        That(!stream.IsEnded && ReferenceEquals(voice.GetPlaybackStream(samId), stream), "within the grace the session still lists Sam, and the stream stays");
        rig.JoinAndWait(sam);
        RunUntilListed(rig, lee, "sam");
        That(IdOf(lee, "sam").Equals(samId) && !stream.IsEnded, "Sam comes back as the same participant, to the same stream");
        var mark = ears.Heard("sam").Count;
        mic.Talk(Speech(1, 73));
        RunFor(rig, 1_400_000);
        That(ears.LevelSince("sam", mark) > Heard, "and Lee hears Sam on it again");

        // Sam leaves for good: the stream ends once the record goes, and asking again gives nothing and costs nothing
        // until the session lists Sam again.
        sam.Control.Leave();
        That(rig.Run(() => stream.IsEnded, 40_000_000, 20_000), "the stream ends once Sam's record goes, after the grace");
        mark = ears.Heard("sam").Count;
        RunFor(rig, 200_000);
        That(ears.Heard("sam").Skip(mark).All(sample => sample == 0), "and plays silence");
        var streams = lee.Provider.Voice!.GetStatistics().Speakers.Count;
        That(!voice.TryGetPlaybackStream(samId, out var none) && none == null, "asking for Sam, whom the session no longer lists, gives nothing");
        var bytes = AllocationPasses.Fewest(() => TryEveryFrame(voice, samId, 1_000));
        That(bytes == 0, "asking a thousand times allocates nothing (" + bytes + " bytes)");
        RunFor(rig, 100_000);
        That(lee.Provider.Voice!.GetStatistics().Speakers.Count == streams, "and makes no stream");

        rig.JoinAndWait(sam);
        RunUntilListed(rig, lee, "sam");
        var samBack = IdOf(lee, "sam");
        That(!samBack.Equals(samId), "Sam returns as a new participant, " + samBack);
        That(voice.TryGetPlaybackStream(samBack, out var renewed) && renewed != null && !ReferenceEquals(renewed, stream) && !renewed.IsEnded && stream.IsEnded,
            "once the session lists Sam again, asking gives a new stream, and the old one stays ended");
        ears.Listen("sam-back", renewed!);
        mic.Talk(Speech(1, 72));
        RunFor(rig, 1_400_000);
        That(ears.LevelSince("sam-back", 0) > Heard, "Lee hears Sam on the new one");
        That(ears.Heard("sam").Skip(mark).All(sample => sample == 0), "and never on the old one");
    }

    internal static void AStreamNeverPlaysSomeoneElseAfterAJoin()
    {
        // Every host process numbers its players itself. Xan is p1 with the first process, and Lee holds the stream the SPI
        // handed out for Xan. The host crashes and starts again under the same identity, at the same address, and Yul joins
        // the new process first, as its p1. Lee's link reconnects to the new process, which has no record of Lee, so the
        // engine lets go of the list that named Xan, and the stream ends at that join, before the new process lists its
        // players. It never plays Yul, who has Xan's id now; asking again gives Yul a stream of their own.
        using var rig = ControlRig.WithVoice(341, Sandbox);
        Host(rig);
        var (xan, xanMic) = Join(rig, "xan", Sandbox);
        var (lee, ears) = Join(rig, "lee", Sandbox);
        RunUntilListed(rig, lee, "xan");
        var p1 = IdOf(lee, "xan");
        var stream = ears.Listen("xan", p1);
        xanMic.Talk(Speech(1, 95));
        RunFor(rig, 1_400_000);
        That(ears.LevelSince("xan", 0) > Heard, "Lee hears Xan, " + p1 + ", on the stream the SPI handed out");

        xan.Control.Leave();
        That(rig.Run(() => xan.Control.Role == InternalSessionRole.Solo), "Xan leaves, still listed for the grace");

        // Lee's game stalls across the restart, so its reconnect cannot run before Yul joins, however long the new process
        // takes to load its identity.
        That(rig.Peers.Remove(lee), "Lee's frames stall");
        var restarted = rig.RestartHost(Sandbox);
        restarted.Runtime.PreferredDisplayName = "Hoster";
        rig.StartHosting(new InternalHostSettings("Voice host", 8));
        var (_, yulMic) = Join(rig, "yul", Sandbox);
        RunUntilListed(rig, restarted, "yul");
        That(IdOf(restarted, "yul").Equals(p1), "the restarted host gives Yul Xan's id, " + p1);
        That(!stream.IsEnded && lee.Session.Snapshot.TryGetParticipant(p1, out var stale) && stale!.DisplayName == "Xan",
            "Lee has not noticed yet: its list still names Xan as " + p1);
        yulMic.Talk(Speech(30, 96));
        rig.Peers.Add(lee);

        That(rig.Run(() => lee.Control.GetStatus().JoinPhase == InternalJoinPhase.Reconnecting, 20_000_000, 20_000), "Lee's link to the crashed process goes");
        var quietFrom = ears.Heard("xan").Count;
        That(rig.Run(() => lee.Control.GetStatus().JoinPhase == InternalJoinPhase.InSession && lee.Session.Snapshot.State == MultiplayerSessionState.Ready &&
                           lee.Session.Snapshot.TryGetParticipant(p1, out var listed) && listed!.DisplayName == "Yul", 20_000_000),
            "Lee reconnects to the restarted process, which lists Yul as " + p1);
        That(stream.IsEnded, "the stream handed out for Xan ended at that join");
        RunFor(rig, 2_000_000);
        That(ears.Heard("xan").Skip(quietFrom).All(sample => sample == 0), "and never played Yul, who talks as " + p1);
        That(ears.Service.TryGetPlaybackStream(p1, out var yulStream) && yulStream != null && !ReferenceEquals(yulStream, stream) && !yulStream.IsEnded,
            "asking again for " + p1 + " gives a new stream");
        ears.Listen("yul", yulStream!);
        RunFor(rig, 1_500_000);
        That(ears.LevelSince("yul", 0) > Heard, "which plays Yul");
    }

    internal static void AskingAgainPastTheBoundCostsNothing()
    {
        using var rig = ControlRig.WithVoice(337, Sandbox);
        Host(rig);
        var (lee, ears) = Join(rig, "lee", Sandbox);
        Join(rig, "sam", Sandbox);
        var voice = ears.Service;
        RunUntilListed(rig, lee, "sam");
        var bound = lee.Provider.Engine.Options.MaximumParticipants;
        var held = lee.Provider.Voice!.GetStatistics().Speakers.Count;

        // Strangers the session does not list each take a stream for the grace while there is room, as voice that comes
        // before its speaker's listing does, until the bound refuses.
        var strangers = new List<ParticipantId>();
        IVoicePlaybackStream? refused = null;
        for (var index = 0; index < 64 && refused == null; index++)
        {
            var stranger = new ParticipantId("stranger-" + index);
            var given = voice.GetPlaybackStream(stranger);
            if (given.IsEnded) refused = given;
            else strangers.Add(stranger);
        }

        That(refused != null && held + strangers.Count == bound, "the bound, " + bound + " streams, refuses the next stranger with an ended stream");
        var outside = new ParticipantId("stranger-refused");
        var perCall = AllocationPasses.Fewest(() => GetEveryFrame(voice, outside, 8)) / 8;
        That(perCall >= VoicePlaybackStream.Capacity * sizeof(float), "past the bound, every GetPlaybackStream makes a new ring (" + perCall + " bytes a call)");
        var asked = AllocationPasses.Fewest(() => TryEveryFrame(voice, outside, 1_000));
        That(asked == 0 && !voice.TryGetPlaybackStream(outside, out var nothing) && nothing == null,
            "while TryGetPlaybackStream gives nothing and allocates nothing (" + asked + " bytes)");

        // A speaker the session lists still gets a stream at the bound, ahead of the strangers.
        That(voice.TryGetPlaybackStream(IdOf(lee, "sam"), out var samStream) && samStream != null && !samStream.IsEnded,
            "Sam, whom the session lists, still gets a stream");

        // After the grace the strangers' streams end, and asking for them again makes none.
        RunFor(rig, 2_100_000);
        That(strangers.All(stranger => !voice.TryGetPlaybackStream(stranger, out _)), "after the grace no stranger has a stream, and asking makes none");
        That(lee.Provider.Voice!.GetStatistics().Speakers.Count == held + 1, "Sam's stream is all that is left beside the ones Lee had");
    }

    internal static void StatisticsAreMappedByMeaning()
    {
        // The mapping, field by field, over values that tell every source apart.
        var service = new VoiceServiceStatistics
        {
            Enabled = true,
            Mode = VoiceTransmitMode.VoiceActivation,
            State = VoiceTransmitState.MutedByHost,
            InputLevelDb = -33.5f,
            ExpectedLossPercent = 15,
            SubmittedSamples = 48_000,
            StaleSamplesDropped = 3,
            CaptureOverflowSamples = 5,
            FramesCaptured = 51,
            FramesEncoded = 40,
            DiscontinuousSuppressed = 6,
            EndsOfTalk = 2,
            StaleFramesDropped = 9,
            FramesDroppedOff = 11,
            Sent = 10,
            Held = 7,
            RateLimited = 1,
            ServerMuted = 2,
            NotConnected = 4,
            Invalid = 8,
            FramesReceived = 90,
            InboundOverflow = 13,
            FramesUnrouted = 17,
            UnlistedFramesDropped = 19,
            UnlistedStreamsEnded = 23
        };
        var mapped = ProviderVoice.ToStatistics(service);
        That(mapped.Mode == InternalVoiceTransmitMode.VoiceActivation && mapped.State == InternalVoiceTransmitState.MutedByHost && mapped.InputLevelDb == -33.5f &&
             mapped.ExpectedLossPercent == 15 && mapped.SubmittedSamples == 48_000 && mapped.FramesEncoded == 40 && mapped.FramesReceived == 90 &&
             mapped.Speakers.Count == 0, "the fields of the same meaning carry over");
        That(mapped.FramesSent == 17, "FramesSent is what the session took to send, at once and held to pair: 10 and 7 (" + mapped.FramesSent + ")");
        That(mapped.FramesRefused == 7, "FramesRefused totals the rate limits, the host's mute and no connection, 1, 2 and 4, without the invalid frames (" + mapped.FramesRefused + ")");
        foreach (var state in Enum.GetValues<VoiceTransmitState>())
        {
            That(ProviderVoice.ToState(state).ToString() == state.ToString(), state + " maps to its namesake");
        }

        foreach (var mode in Enum.GetValues<InternalVoiceTransmitMode>())
        {
            That(ProviderVoice.ToMode(ProviderVoice.FromMode(mode)) == mode && ProviderVoice.FromMode(mode).ToString() == mode.ToString(), mode + " maps both ways");
        }

        That(Enum.GetValues<VoiceTransmitState>().Length == Enum.GetValues<InternalVoiceTransmitState>().Length &&
             Enum.GetValues<VoiceTransmitMode>().Length == Enum.GetValues<InternalVoiceTransmitMode>().Length, "and neither side has a value the other lacks");

        // End to end: a talker's frames against the service's own counters.
        using var rig = ControlRig.WithVoice(338, Sandbox);
        Host(rig);
        var (sam, mic) = Join(rig, "sam", Sandbox);
        var (lee, ears) = Join(rig, "lee", Sandbox);
        RunUntilListed(rig, lee, "sam");
        RunUntilListed(rig, rig.Host, "sam");
        ears.Listen("sam", IdOf(lee, "sam"));
        mic.Talk(Speech(1, 81));
        RunFor(rig, 1_500_000);
        var talker = mic.Service.GetStatistics();
        var own = sam.Provider.Voice!.GetStatistics();
        That(talker.FramesEncoded == own.FramesEncoded && talker.FramesEncoded > 40 && talker.SubmittedSamples == own.SubmittedSamples &&
             talker.Mode == ProviderVoice.ToMode(own.Mode) && talker.State == ProviderVoice.ToState(own.State) && talker.InputLevelDb == own.InputLevelDb &&
             talker.ExpectedLossPercent == own.ExpectedLossPercent, "Sam's counters carry over");
        That(own.Held > 0 && own.Sent < own.FramesEncoded * 3 / 4,
            "the session held about half of Sam's frames to pair them (" + own.Sent + " at once, " + own.Held + " held, of " + own.FramesEncoded + ")");
        That(talker.FramesSent == own.Sent + own.Held && talker.FramesSent == talker.FramesEncoded && talker.FramesRefused == 0,
            "and took every frame Sam coded, which FramesSent says only by counting the held ones (" + talker.FramesSent + ")");

        // The host mutes Sam mid-sentence: Lee's stream runs dry without an end of talk, so it conceals, then fades.
        mic.Talk(Speech(3, 82));
        RunFor(rig, 1_000_000);
        That(rig.Host.Control.SetServerMute(IdOf(rig.Host, "sam"), true).Succeeded, "the host mutes Sam mid-sentence");
        RunFor(rig, 1_000_000);
        var listener = ears.Service.GetStatistics();
        var heard = lee.Provider.Voice!.GetStatistics();
        var spi = listener.Speakers.Single();
        var real = heard.Speakers.Single();
        That(real.Faded > 0 && real.Concealed > 0, "the cut leaves concealment that fades (" + real.Concealed + " concealed, " + real.Faded + " faded)");
        That(spi.Speaker.Equals(real.Speaker) && spi.IsSpeaking == real.IsSpeaking && spi.Decoded == real.Decoded && spi.Recovered == real.Recovered &&
             spi.Late == real.Late && spi.Underruns == real.Underruns && spi.TargetDelayMicros == real.TargetDelayMicros && spi.Decoded > 40,
            "Lee's counters for Sam carry over");
        That(spi.Concealed == real.Concealed + real.Faded, "and Concealed counts the faded frames too (" + spi.Concealed + ")");
        That(listener.FramesReceived == heard.FramesReceived && listener.FramesReceived > 0 && rig.Host.Control.GetStatistics().Voice!.FramesReceived > 0,
            "frames received carry over, and the control's statistics carry them as well");
    }

    internal static void TheVoiceSpiBelongsToTheMainThread()
    {
        using var rig = ControlRig.WithVoice(339, Sandbox);
        Host(rig);
        var (lee, ears) = Join(rig, "lee", Sandbox);
        var voice = ears.Service;
        RunUntilListed(rig, lee, "hoster");
        var hostId = IdOf(lee, "hoster");
        var stream = voice.GetPlaybackStream(hostId);
        var calls = new (string Name, Action Call)[]
        {
            ("Enabled", () => _ = voice.Enabled),
            ("Enabled=", () => voice.Enabled = false),
            ("Mode", () => _ = voice.Mode),
            ("Mode=", () => voice.Mode = InternalVoiceTransmitMode.VoiceActivation),
            ("ActivationThresholdDb", () => _ = voice.ActivationThresholdDb),
            ("ActivationThresholdDb=", () => voice.ActivationThresholdDb = -20f),
            ("InputLevelDb", () => _ = voice.InputLevelDb),
            ("TransmitState", () => _ = voice.TransmitState),
            ("SubmitCapture", () => voice.SubmitCapture(new float[480], 480, 48_000)),
            ("SetTransmitting", () => voice.SetTransmitting(true)),
            ("GetPlaybackStream", () => voice.GetPlaybackStream(hostId)),
            ("TryGetPlaybackStream", () => voice.TryGetPlaybackStream(hostId, out _)),
            ("IsSpeaking", () => voice.IsSpeaking(hostId)),
            ("SetListenerMutes", () => voice.SetListenerMutes(new[] { hostId })),
            ("GetListenerMutes", () => voice.GetListenerMutes(new List<ParticipantId>())),
            ("GetStatistics", () => voice.GetStatistics())
        };
        var declared = typeof(IInternalVoiceService).GetMethods().Select(method =>
            method.Name.StartsWith("get_", StringComparison.Ordinal) ? method.Name.Substring(4)
            : method.Name.StartsWith("set_", StringComparison.Ordinal) ? method.Name.Substring(4) + "="
            : method.Name).OrderBy(name => name, StringComparer.Ordinal);
        That(declared.SequenceEqual(calls.Select(call => call.Name).OrderBy(name => name, StringComparer.Ordinal)), "the calls cover every member of the voice SPI");

        // From another thread every member refuses, and changes nothing; a stream's read works there, even while the main
        // thread holds the voice service's gate and the stream itself, since it takes no lock and never waits.
        var ran = new List<string>();
        var read = 0;
        var reader = new Thread(() =>
        {
            foreach (var (name, call) in calls)
            {
                try
                {
                    call();
                    ran.Add(name + " ran");
                }
                catch (InvalidOperationException)
                {
                    // Refused, as it must be.
                }
                catch (Exception exception)
                {
                    ran.Add(name + " threw " + exception.GetType().Name);
                }
            }

            try
            {
                read = stream.Read(new float[960], 0, 960) + (stream.IsEnded ? 1 : 0) + (stream.Speaker.Equals(hostId) ? 0 : 1);
            }
            catch (Exception exception)
            {
                // A read that refuses the audio thread fails the test below, rather than the whole harness.
                ran.Add("Read threw " + exception.GetType().Name);
                read = -1;
            }
        });
        bool finished;
        lock (lee.Provider.Voice!.Inbound.Gate)
        {
            lock (stream)
            {
                reader.Start();
                finished = reader.Join(TimeSpan.FromSeconds(10));
            }
        }

        That(finished, "the other thread finishes: nothing it calls waits on the main thread's locks (" + string.Join(", ", ran) + ")");
        That(ran.All(entry => !entry.StartsWith("Read", StringComparison.Ordinal)) && read == 960,
            "a stream's read runs on the audio thread, past both locks (" + string.Join(", ", ran) + ")");
        That(ran.Count == 0, "while every other member refuses any thread but the main one (" + string.Join(", ", ran) + ")");
        That(voice.Enabled && voice.Mode == InternalVoiceTransmitMode.PushToTalk && voice.ActivationThresholdDb == -40f,
            "and the refused calls changed nothing");
    }

    internal static void TheVoiceSpiCarriesNoAddress()
    {
        // The voice SPI, with every SDK type its members name, transitively, and the provider's types behind it.
        var spi = Transitive(typeof(IInternalVoiceService));
        foreach (var expected in new[]
                 {
                     typeof(IInternalVoiceService), typeof(IVoicePlaybackStream), typeof(InternalVoiceStatistics), typeof(InternalVoiceSpeakerStatistics),
                     typeof(InternalVoiceTransmitMode), typeof(InternalVoiceTransmitState), typeof(ParticipantId)
                 })
        {
            That(spi.Contains(expected), "the scan covers " + expected.Name);
        }

        var behind = new[]
        {
            typeof(ProviderVoice), typeof(VoicePlaybackStream), typeof(VoiceService), typeof(VoiceServiceStatistics), typeof(VoiceSpeakerStatistics),
            typeof(VoiceSpeakerCounters), typeof(VoiceTransmitMode), typeof(VoiceTransmitState)
        };
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var offenders = new List<string>();
        foreach (var type in spi.Concat(behind))
        {
            if (RuntimePrivacyTests.IsAddressShaped(type.Name)) offenders.Add(type.Name);
            foreach (var member in type.GetMembers(All))
            {
                if (member.Name.StartsWith("<", StringComparison.Ordinal)) continue;
                if (RuntimePrivacyTests.IsAddressShaped(member.Name) || RuntimePrivacyTests.Types(member).Any(RuntimePrivacyTests.IsNetworkType)) offenders.Add(type.Name + "." + member.Name);
                foreach (var parameter in RuntimePrivacyTests.Parameters(member))
                {
                    if (parameter.Name != null && RuntimePrivacyTests.IsAddressShaped(parameter.Name)) offenders.Add(type.Name + "." + member.Name + "(" + parameter.Name + ")");
                }
            }
        }

        That(offenders.Count == 0, "no member, parameter or type of the voice SPI, or behind it, is shaped like an address (" + string.Join(", ", offenders) + ")");

        // What the voice SPI answers in a live session names participants the session lists, and nothing else.
        using var rig = ControlRig.WithVoice(340, Sandbox);
        Host(rig);
        var (_, mic) = Join(rig, "sam", Sandbox);
        var (lee, ears) = Join(rig, "lee", Sandbox);
        RunUntilListed(rig, lee, "sam");
        var stream = ears.Listen("sam", IdOf(lee, "sam"));
        mic.Talk(Speech(1, 91));
        RunFor(rig, 1_300_000);
        var texts = new List<string> { stream.Speaker.Value };
        foreach (var statistics in new[] { ears.Service.GetStatistics(), mic.Service.GetStatistics(), rig.Host.Control.GetStatistics().Voice! })
        {
            texts.AddRange(statistics.Speakers.Select(speaker => speaker.Speaker.Value));
        }

        var listed = lee.Session.Snapshot.Participants.Select(participant => participant.Id.Value)
            .Concat(rig.Host.Session.Snapshot.Participants.Select(participant => participant.Id.Value)).ToHashSet(StringComparer.Ordinal);
        That(texts.Count >= 3 && texts.All(listed.Contains) && texts.All(text => MultiplayerDiagnosticRedactor.Redact(text) == text),
            "every name the voice SPI answers is a listed participant's id, with nothing address-shaped (" + string.Join(", ", texts) + ")");
    }

    /// <summary>
    /// The SDK types <paramref name="root"/> names, transitively: every type of the root's assembly that a member's type,
    /// parameter or return value names, through arrays and generic arguments.
    /// </summary>
    private static HashSet<Type> Transitive(Type root)
    {
        const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        var found = new HashSet<Type>();
        var pending = new Queue<Type>(new[] { root });
        while (pending.Count > 0)
        {
            var type = pending.Dequeue();
            if (!found.Add(type)) continue;
            foreach (var member in type.GetMembers(All))
            {
                foreach (var named in RuntimePrivacyTests.Types(member).SelectMany(Unwrap))
                {
                    if (named.Assembly == root.Assembly && !found.Contains(named)) pending.Enqueue(named);
                }
            }
        }

        return found;
    }

    private static IEnumerable<Type> Unwrap(Type type)
    {
        if (type.HasElementType) return Unwrap(type.GetElementType()!);
        if (!type.IsGenericType) return new[] { type };
        return type.GetGenericArguments().SelectMany(Unwrap).Append(type.GetGenericTypeDefinition());
    }

    /// <summary>Asks for <paramref name="speaker"/>'s stream <paramref name="times"/> times and returns the bytes it allocated.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long TryEveryFrame(IInternalVoiceService voice, ParticipantId speaker, int times)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < times; index++) voice.TryGetPlaybackStream(speaker, out _);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>Gets <paramref name="speaker"/>'s stream <paramref name="times"/> times and returns the bytes it allocated.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long GetEveryFrame(IInternalVoiceService voice, ParticipantId speaker, int times)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < times; index++) voice.GetPlaybackStream(speaker);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }
}
