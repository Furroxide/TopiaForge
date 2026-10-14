extern alias provider;

using System.Diagnostics;
using provider::TopiaForge.Multiplayer.Engine;
using provider::TopiaForge.Multiplayer.Voice;
using TopiaForge.Mods;
using Provider = provider::TopiaForge.Multiplayer.MultiplayerProvider;

namespace TopiaForge.Mods.Multiplayer.Tests;

/// <summary>
/// The voice service in the provider: off unless live multiplayer is switched on, a worker that really runs on its own
/// thread and stops, and an encoder loss hint that follows the transport's counters.
/// </summary>
internal static class VoiceProviderTests
{
    private const string LiveSwitch = "TOPIAFORGE_LIVE_MULTIPLAYER";

    internal static void TheLiveSwitchTurnsVoiceOn()
    {
        var previous = Environment.GetEnvironmentVariable(LiveSwitch);
        try
        {
            foreach (var value in new[] { null, "0", "true" })
            {
                Environment.SetEnvironmentVariable(LiveSwitch, value);
                using var off = new Provider("voice.off-test", clockMicros: () => 0);
                Assert(off.Voice == null && !off.Engine.IsVoiceEnabled, "with the switch at '" + value + "' there is no voice, codec or worker");
            }

            Environment.SetEnvironmentVariable(LiveSwitch, "1");
            var on = new Provider("voice.on-test");
            try
            {
                var voice = on.Voice;
                Assert(voice != null && on.Engine.IsVoiceEnabled && voice.Worker.IsRunning, "with it at 1, the engine has voice and the worker runs");
                Assert(!voice!.Enabled, "but voice stays off until the player turns it on");
                on.Pump();
                Assert(voice.TransmitState == VoiceTransmitState.Idle, "so nothing is sent, and nothing claims otherwise");
                voice.Enabled = true;
                on.Pump();
                Assert(voice.TransmitState == VoiceTransmitState.NoListeners, "turned on, a solo session has nobody to hear, which is a state, not an error");
                var worker = voice.Worker;
                on.Dispose();
                Assert(!worker.IsRunning, "disposing the provider stops the worker");
            }
            finally
            {
                on.Dispose();
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(LiveSwitch, previous);
        }
    }

    internal static void TheWorkerCodesOnItsOwnThread()
    {
        // A real worker thread on the real clock: capture handed in on this thread is encoded on the worker's, sent by
        // this thread's pumps, and reaches the host. This test alone waits on the wall clock, for at most five seconds.
        var codec = new OpusVoiceCodec();
        var options = new EngineOptions { StepRateHz = 60, ParticipantIdForIdentity = identity => new ParticipantId(identity), Voice = new VoiceOptions(codec) };
        var network = new VirtualEngineTransport(new VirtualLinkConditions { LatencyTicks = 1 });
        Func<long> clock = Provider.MonotonicMicros;
        var host = SessionEngine.CreateHost(options, MultiplayerProcessKind.Interactive, new MultiplayerSessionId("thread"), new SessionSeed(1),
            new ParticipantId("host"), "Host", clock());
        var client = SessionEngine.CreateClient(options, new MultiplayerSessionId("thread"), clock());
        var pair = network.Connect("a");
        host.Accept(pair.HostEnd);
        client.Connect(pair.ClientEnd, "a");
        var voice = new VoiceService(client, codec, clock) { Enabled = true };
        voice.SetTransmitting(true);
        try
        {
            ulong tick = 0;
            void Pump()
            {
                tick++;
                network.Now = tick;
                network.DeliverDue(tick);
                var now = clock();
                host.Pump(now);
                client.Pump(now);
                voice.Pump(now);
            }

            for (var index = 0; index < 20 && client.Snapshot.State != MultiplayerSessionState.Ready; index++) Pump();
            Assert(client.Snapshot.State == MultiplayerSessionState.Ready, "the client joins");
            var speech = VoiceSource.Speech(0.5).Signal;
            var chunk = new float[speech.Length];
            var submitted = 0;
            var heard = 0;
            var elapsed = Stopwatch.StartNew();
            while (heard < 10 && elapsed.ElapsedMilliseconds < 5_000)
            {
                var due = (int)Math.Min(speech.Length, elapsed.ElapsedTicks * 48_000 / Stopwatch.Frequency);
                if (due > submitted)
                {
                    Array.Copy(speech, submitted, chunk, 0, due - submitted);
                    voice.SubmitCapture(chunk, due - submitted, 48_000);
                    submitted = due;
                }

                Pump();
                while (host.TryReceiveVoiceFrame(out _)) heard++;
                Thread.Sleep(5);
            }

            var statistics = voice.GetStatistics();
            Assert(heard >= 10, "the frames the worker encoded reach the host (" + heard + " in " + elapsed.ElapsedMilliseconds + " ms)");
            Assert(voice.Worker.IsRunning && statistics.WorkerFaults == 0 && statistics.WorkerSteps > 0, "the worker runs, without faults");
            var worker = voice.Worker;
            voice.Dispose();
            Assert(!worker.IsRunning, "and stops when the service is disposed");
        }
        finally
        {
            voice.Dispose();
            client.Shutdown(MultiplayerSessionEndReason.Left);
            host.Shutdown(MultiplayerSessionEndReason.HostEnded);
        }
    }

    internal static void TheLossHintFollowsTheTransport()
    {
        var estimator = new VoiceLossEstimator();
        Assert(estimator.ExpectedLossPercent == OpusVoiceCodec.DefaultExpectedLossPercent && !estimator.HasMeasurement,
            "before a window completes, the codec's default hint stands");
        estimator.Report(1_000, 10);
        estimator.Report(1_100, 12);
        Assert(!estimator.HasMeasurement, "a hundred packets are not a window");
        estimator.Report(1_250, 22);
        Assert(estimator.ExpectedLossPercent == 5, "4.8% lost over 250 packets rounds up to 5");
        estimator.Report(1_500, 72);
        Assert(estimator.ExpectedLossPercent == 20, "a worse link raises the hint at once");
        estimator.Report(1_750, 72);
        Assert(estimator.ExpectedLossPercent == 10, "a clean window halves it");
        estimator.Report(2_000, 200);
        Assert(estimator.ExpectedLossPercent == VoiceLossEstimator.MaximumHintPercent, "the hint never passes 25%");
        estimator.Report(100, 0);
        Assert(estimator.ExpectedLossPercent == VoiceLossEstimator.MaximumHintPercent, "counters that start again begin a new window");

        // The service hands the hint to its encoder on the worker.
        using var rig = new VoiceServiceRig();
        rig.AddHost();
        var a = rig.AddClient("a");
        a.Voice!.ReportTransportCounters(0, 0);
        a.Voice.ReportTransportCounters(300, 60);
        rig.Step();
        Assert(a.Voice.ExpectedLossPercent == 20 && a.Voice.Worker.Transmitter.Encoder.ExpectedLossPercent == 20,
            "20% loss on the link sets the encoder's FEC hint to 20%");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed: " + message);
    }
}
