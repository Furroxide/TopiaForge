using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// The voice worker: the one thread that encodes and decodes, so neither the game's main thread nor the audio
    /// thread ever runs the codec. Each <see cref="Step"/> takes what the main thread queued: capture for the talker,
    /// then received frames for each speaker's jitter buffer, with control for the speakers applied in the order it was
    /// queued among them. It then runs the 20 ms playout slots that have come due, which pull, decode and fill the
    /// playback streams.
    /// </summary>
    /// <remarks>
    /// <see cref="Start"/> runs <see cref="TryStep"/> on a background thread, woken by the main thread or by the next slot;
    /// ten failed steps in a row and it gives up. Tests call <see cref="Step"/> themselves against a virtual clock instead,
    /// so every run is the same, and <see cref="TryStep"/> to see a failure through. A step that
    /// comes up to <see cref="MaximumCatchUpSlots"/> slots late runs the slots it missed. One that comes later follows a
    /// stall: the missed slots are dropped rather than run in a burst, and every jitter buffer is trimmed to its target, as
    /// it is after any batch that leaves one deeper than it ever aims to be. Voice then plays at its usual delay at once.
    /// </remarks>
    internal sealed class VoiceWorker : IDisposable
    {
        /// <summary>The playout slot: one 20 ms frame.</summary>
        internal const long SlotMicros = 20_000;

        /// <summary>The most missed slots a late step still runs: 100 ms. A later step drops them all.</summary>
        internal const int MaximumCatchUpSlots = 5;

        /// <summary>The failed steps in a row after which the worker gives up and voice stops.</summary>
        internal const int FaultLimit = 10;

        private const int IdleWaitMilliseconds = 100;

        private readonly OpusVoiceCodec codec;
        private readonly Func<long> clock;
        private readonly VoiceExchange<VoiceInboundBatch> inbound;
        private readonly VoiceTransmitter transmitter;
        private readonly Dictionary<string, VoiceSpeakerChannel> channels = new Dictionary<string, VoiceSpeakerChannel>(StringComparer.Ordinal);
        private readonly List<VoiceSpeakerChannel> playing = new List<VoiceSpeakerChannel>();
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private Thread? thread;
        private volatile bool stopping;
        private volatile bool failed;
        private long nextSlotMicros = long.MinValue;
        private int consecutiveFaults;
        private int mode;
        private int thresholdBits = BitConverter.SingleToInt32Bits(VoiceActivityDetector.DefaultThresholdDb);
        private int lossPercent = OpusVoiceCodec.DefaultExpectedLossPercent;
        private int blocked;

        internal VoiceWorker(
            OpusVoiceCodec codec,
            VoiceExchange<VoiceInboundBatch> inbound,
            VoiceExchange<VoiceOutboundBatch> outbound,
            Func<long> clock)
        {
            this.codec = codec ?? throw new ArgumentNullException(nameof(codec));
            this.inbound = inbound ?? throw new ArgumentNullException(nameof(inbound));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            transmitter = new VoiceTransmitter(codec, outbound);
        }

        internal VoiceTransmitter Transmitter => transmitter;

        /// <summary>Gets whether the background thread runs.</summary>
        internal bool IsRunning => thread != null && !stopping && !failed;

        /// <summary>Gets whether the worker gave up after <see cref="FaultLimit"/> failed steps in a row.</summary>
        internal bool Failed => failed;

        /// <summary>Gets the last exception a step threw, for the one diagnostic the service reports.</summary>
        internal Exception? LastFault { get; private set; }

        internal long Steps { get; private set; }
        internal long Faults { get; private set; }
        internal long SlotsRun { get; private set; }
        internal long SlotsDropped { get; private set; }
        internal long FramesUnrouted { get; private set; }

        /// <summary>Gets how many speakers the worker plays. The worker's thread only.</summary>
        internal int SpeakerCount => playing.Count;

        /// <summary>Passes the main thread's settings; the worker applies them at its next step.</summary>
        internal void Configure(VoiceTransmitMode transmitMode, float thresholdDb, int expectedLossPercent, bool transmitBlocked)
        {
            Volatile.Write(ref mode, (int)transmitMode);
            Volatile.Write(ref thresholdBits, BitConverter.SingleToInt32Bits(VoiceActivityDetector.RequireThreshold(thresholdDb)));
            Volatile.Write(ref lossPercent, OpusVoiceCodec.RequireLossPercent(expectedLossPercent, nameof(expectedLossPercent)));
            Volatile.Write(ref blocked, transmitBlocked ? 1 : 0);
        }

        /// <summary>Starts the background thread.</summary>
        internal void Start()
        {
            if (thread != null) throw new InvalidOperationException("The voice worker is already running.");
            thread = new Thread(Run) { IsBackground = true, Name = "TopiaForge voice", Priority = ThreadPriority.AboveNormal };
            thread.Start();
        }

        /// <summary>Wakes the worker: the main thread queued capture or frames.</summary>
        internal void Signal()
        {
            if (thread != null && !stopping) wake.Set();
        }

        /// <summary>Does everything due at <paramref name="nowMicros"/> on the voice clock.</summary>
        internal void Step(long nowMicros)
        {
            Steps++;
            transmitter.Configure(
                (VoiceTransmitMode)Volatile.Read(ref mode),
                BitConverter.Int32BitsToSingle(Volatile.Read(ref thresholdBits)),
                Volatile.Read(ref lossPercent));
            transmitter.Blocked = Volatile.Read(ref blocked) != 0;
            var batch = inbound.Take();
            if (batch != null)
            {
                try
                {
                    for (var index = 0; index < batch.CaptureCount; index++)
                    {
                        ref var entry = ref batch.Capture(index);
                        if (entry.EndOfCapture) transmitter.EndCapture(entry.EndMicros);
                        else transmitter.Feed(batch.CaptureSamples(entry), entry.SampleRate, entry.EndMicros, entry.Transmit);
                    }

                    // Control and frames apply in the order they were queued: a reset drops the frames before it, such as
                    // the last of a session the player left, and keeps every frame after it.
                    var controls = batch.Controls;
                    var next = 0;
                    for (var index = 0; index < batch.FrameCount; index++)
                    {
                        while (next < controls.Count && controls[next].FrameIndex <= index) Apply(controls[next++]);
                        ref var frame = ref batch.Frame(index);
                        if (channels.TryGetValue(frame.Speaker.Value, out var channel)) channel.Push(frame, batch.Payload(frame));
                        else FramesUnrouted++;
                    }

                    while (next < controls.Count) Apply(controls[next++]);

                    // A batch that carries a stall's backlog leaves a buffer deeper than it ever aims to be.
                    if (batch.FrameCount > 0) TrimAll();
                }
                finally
                {
                    batch.Clear();
                    inbound.Return(batch);
                }
            }

            RunSlots(nowMicros);
        }

        /// <summary>
        /// Runs <see cref="Step"/> on the voice clock and contains whatever it throws: a fault is counted, and after
        /// <see cref="FaultLimit"/> in a row the worker gives up. Returns false once it has. The background thread runs
        /// this; tests may run it themselves.
        /// </summary>
        internal bool TryStep()
        {
            if (failed) return false;
            try
            {
                Step(clock());
                consecutiveFaults = 0;
                return true;
            }
            catch (Exception exception)
            {
                // Nothing may escape the thread: an unhandled exception would take the game down with it.
                Faults++;
                LastFault = exception;
                if (++consecutiveFaults < FaultLimit) return true;
                failed = true;
                return false;
            }
        }

        /// <summary>Stops the background thread and waits up to a second for it.</summary>
        public void Dispose()
        {
            if (stopping) return;
            stopping = true;
            if (thread == null)
            {
                wake.Dispose();
                return;
            }

            wake.Set();
            // A thread still inside a step after a second keeps its wake-up event; it exits at its next check.
            if (Thread.CurrentThread != thread && thread.Join(1_000)) wake.Dispose();
        }

        private void Apply(VoiceControl control)
        {
            var id = control.Speaker.Value;
            switch (control.Kind)
            {
                case VoiceControlKind.Attach:
                    if (control.Stream == null) return;
                    if (channels.TryGetValue(id, out var existing))
                    {
                        if (ReferenceEquals(existing.Stream, control.Stream)) return;
                        playing.Remove(existing);
                    }

                    var channel = new VoiceSpeakerChannel(codec, control.Stream);
                    channels[id] = channel;
                    playing.Add(channel);
                    return;
                case VoiceControlKind.Reset:
                    if (channels.TryGetValue(id, out var reset)) reset.Reset();
                    return;
                case VoiceControlKind.Remove:
                    if (channels.TryGetValue(id, out var removed))
                    {
                        channels.Remove(id);
                        playing.Remove(removed);
                    }

                    return;
                default:
                    foreach (var each in playing) each.Reset();
                    return;
            }
        }

        private void RunSlots(long nowMicros)
        {
            if (playing.Count == 0)
            {
                nextSlotMicros = long.MinValue;
                return;
            }

            if (nextSlotMicros == long.MinValue) nextSlotMicros = nowMicros;
            if (nowMicros < nextSlotMicros) return;
            var due = ((nowMicros - nextSlotMicros) / SlotMicros) + 1;
            if (due > MaximumCatchUpSlots)
            {
                // A stall: the missed slots' audio is too late to play, so none of it is pulled in a burst. Each jitter
                // buffer is trimmed to its target instead, and only the slot due now runs.
                SlotsDropped += due - 1;
                due = 1;
                TrimAll();
                nextSlotMicros = nowMicros + SlotMicros;
            }
            else
            {
                nextSlotMicros += due * SlotMicros;
            }

            for (var slot = 0; slot < due; slot++)
            {
                for (var index = 0; index < playing.Count; index++) playing[index].Slot(nowMicros);
            }

            SlotsRun += due;
        }

        private void TrimAll()
        {
            for (var index = 0; index < playing.Count; index++) playing[index].Trim();
        }

        private void Run()
        {
            while (!stopping && TryStep())
            {
                if (stopping) return;
                wake.WaitOne(WaitMilliseconds());
            }
        }

        private int WaitMilliseconds()
        {
            if (playing.Count == 0 || nextSlotMicros == long.MinValue) return IdleWaitMilliseconds;
            var micros = nextSlotMicros - clock();
            return micros <= 0 ? 0 : (int)Math.Min(IdleWaitMilliseconds, (micros + 999) / 1_000);
        }
    }
}
