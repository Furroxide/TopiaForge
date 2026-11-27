using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Voice
{
    /// <summary>
    /// A double-buffered hand-off between the main thread and the voice worker. The producer adds to the writable batch
    /// while holding <see cref="Gate"/>, briefly; the consumer swaps the whole batch out with <see cref="Take"/>, works on
    /// it without the lock, clears it, and gives it back with <see cref="Return"/>. Both batches are allocated once, so
    /// steady traffic allocates nothing. The audio thread never touches either.
    /// </summary>
    internal sealed class VoiceExchange<TBatch>
        where TBatch : class
    {
        private readonly object gate = new object();
        private TBatch writable;
        private TBatch? spare;

        internal VoiceExchange(TBatch first, TBatch second)
        {
            writable = first ?? throw new ArgumentNullException(nameof(first));
            spare = second ?? throw new ArgumentNullException(nameof(second));
        }

        /// <summary>Gets the lock the producer holds while it adds to <see cref="Writable"/>.</summary>
        internal object Gate => gate;

        /// <summary>Gets the batch being written. Touch it only while holding <see cref="Gate"/>.</summary>
        internal TBatch Writable => writable;

        /// <summary>Takes everything written so far, or null when the consumer has not given the other batch back.</summary>
        internal TBatch? Take()
        {
            lock (gate)
            {
                if (spare == null) return null;
                var full = writable;
                writable = spare;
                spare = null;
                return full;
            }
        }

        /// <summary>Gives back a batch the consumer has finished with and cleared.</summary>
        internal void Return(TBatch batch)
        {
            if (batch == null) throw new ArgumentNullException(nameof(batch));
            lock (gate) spare = batch;
        }
    }

    /// <summary>One capture in a <see cref="VoiceInboundBatch"/>, or the marker that the talker stopped transmitting.</summary>
    internal struct VoiceCaptureEntry
    {
        internal int Offset;
        internal int Count;
        internal int SampleRate;
        internal long EndMicros;
        internal long Generation;
        internal bool EndOfCapture;

        /// <summary>Whether the talker transmitted while these samples were captured; otherwise they are only metered.</summary>
        internal bool Transmit;
    }

    /// <summary>One received voice frame in a <see cref="VoiceInboundBatch"/>; its payload sits in the batch.</summary>
    internal struct VoiceInboundFrame
    {
        internal ParticipantId Speaker;

        /// <summary>The generation of the speaker's stream, as the host stamped it; a change restarts their jitter buffer.</summary>
        internal ulong Stream;

        internal ulong Sequence;
        internal ulong Timestamp;
        internal VoiceFrameFlags Flags;
        internal int PayloadOffset;
        internal int PayloadLength;
        internal long ArrivalMicros;
    }

    internal enum VoiceControlKind
    {
        /// <summary>Starts playing a speaker into a stream.</summary>
        Attach = 0,

        /// <summary>Forgets what a speaker left buffered, as after a mute.</summary>
        Reset = 1,

        /// <summary>Stops playing a speaker who left.</summary>
        Remove = 2,

        /// <summary>Drops every speaker's jitter buffer and buffered audio, as when the session changes.</summary>
        ResetAll = 3
    }

    internal readonly struct VoiceControl
    {
        internal VoiceControl(VoiceControlKind kind, ParticipantId speaker, VoicePlaybackStream? stream, int frameIndex)
        {
            Kind = kind;
            Speaker = speaker;
            Stream = stream;
            FrameIndex = frameIndex;
        }

        internal VoiceControlKind Kind { get; }
        internal ParticipantId Speaker { get; }
        internal VoicePlaybackStream? Stream { get; }

        /// <summary>
        /// Gets how many of its batch's frames were queued before it. The worker applies it after those frames and before
        /// the rest, so a reset drops what came before it and nothing that came after.
        /// </summary>
        internal int FrameIndex { get; }
    }

    /// <summary>
    /// What the main thread hands the worker: captured audio, and received frames and control in the order they were
    /// queued.
    /// </summary>
    internal sealed class VoiceInboundBatch
    {
        /// <summary>Room for one second of 48 kHz capture between two worker steps.</summary>
        internal const int CaptureSampleCapacity = 48_000;

        internal const int CaptureEntryCapacity = 128;

        /// <summary>Room for twice what the engine's receive queue holds.</summary>
        internal const int FrameCapacity = 512;

        private readonly float[] captureSamples = new float[CaptureSampleCapacity];
        private readonly VoiceCaptureEntry[] captures = new VoiceCaptureEntry[CaptureEntryCapacity];
        private readonly VoiceInboundFrame[] frames = new VoiceInboundFrame[FrameCapacity];
        private readonly byte[] payloads = new byte[FrameCapacity * VoicePacketBytes];
        private readonly List<VoiceControl> controls = new List<VoiceControl>(32);
        private int captureSampleCount;

        private const int VoicePacketBytes = OpusVoiceCodec.MaximumPacketLength;

        internal int CaptureCount { get; private set; }

        internal int FrameCount { get; private set; }

        internal IReadOnlyList<VoiceControl> Controls => controls;

        internal ref VoiceCaptureEntry Capture(int index) => ref captures[index];

        internal Span<float> CaptureSamples(in VoiceCaptureEntry entry) => captureSamples.AsSpan(entry.Offset, entry.Count);

        internal ref VoiceInboundFrame Frame(int index) => ref frames[index];

        internal ReadOnlySpan<byte> Payload(in VoiceInboundFrame frame) => payloads.AsSpan(frame.PayloadOffset, frame.PayloadLength);

        /// <summary>
        /// Queues captured samples, and whether the talker transmitted them. One entry is always left free, so the end of
        /// a spurt that these samples opened always fits after them.
        /// </summary>
        internal bool TryAddCapture(float[] samples, int offset, int count, int sampleRate, long endMicros, bool transmit, long generation = 0)
        {
            if (CaptureCount >= CaptureEntryCapacity - 1 || captureSampleCount + count > CaptureSampleCapacity) return false;
            Array.Copy(samples, offset, captureSamples, captureSampleCount, count);
            captures[CaptureCount++] = new VoiceCaptureEntry
            {
                Offset = captureSampleCount,
                Count = count,
                SampleRate = sampleRate,
                EndMicros = endMicros,
                Generation = generation,
                Transmit = transmit
            };
            captureSampleCount += count;
            return true;
        }

        /// <summary>Queues the end of transmission, after every sample queued so far.</summary>
        internal bool TryAddEndOfCapture(long micros, long generation = 0)
        {
            if (CaptureCount == CaptureEntryCapacity) return false;
            captures[CaptureCount++] = new VoiceCaptureEntry { EndMicros = micros, Generation = generation, EndOfCapture = true };
            return true;
        }

        internal bool TryAddFrame(in VoiceFrame frame)
        {
            var length = frame.Payload.Count;
            if (FrameCount == FrameCapacity || length < 1 || length > VoicePacketBytes) return false;
            var offset = FrameCount * VoicePacketBytes;
            Buffer.BlockCopy(frame.Payload.Array!, frame.Payload.Offset, payloads, offset, length);
            frames[FrameCount++] = new VoiceInboundFrame
            {
                Speaker = frame.Speaker,
                Stream = frame.Stream,
                Sequence = frame.Sequence,
                Timestamp = frame.Timestamp,
                Flags = frame.Flags,
                PayloadOffset = offset,
                PayloadLength = length,
                ArrivalMicros = frame.ArrivalMicros
            };
            return true;
        }

        /// <summary>Queues control for a speaker, after every frame queued so far.</summary>
        internal void AddControl(VoiceControlKind kind, ParticipantId speaker = default, VoicePlaybackStream? stream = null) =>
            controls.Add(new VoiceControl(kind, speaker, stream, FrameCount));

        internal void Clear()
        {
            CaptureCount = 0;
            captureSampleCount = 0;
            FrameCount = 0;
            controls.Clear();
        }
    }

    /// <summary>One encoded frame in a <see cref="VoiceOutboundBatch"/>; its payload sits in the batch.</summary>
    internal struct VoiceOutboundPacket
    {
        internal ulong Timestamp;
        internal VoiceFrameFlags Flags;
        internal int Length;
        internal long CapturedMicros;
        internal long Generation;
    }

    /// <summary>What the worker hands the main thread: encoded frames for the engine, in capture order.</summary>
    internal sealed class VoiceOutboundBatch
    {
        /// <summary>Room for a little over two seconds of frames between two main-thread pumps.</summary>
        internal const int PacketCapacity = 128;

        private const int VoicePacketBytes = OpusVoiceCodec.MaximumPacketLength;

        private readonly VoiceOutboundPacket[] packets = new VoiceOutboundPacket[PacketCapacity];
        private readonly byte[] payloads = new byte[PacketCapacity * VoicePacketBytes];

        internal int Count { get; private set; }

        internal ref VoiceOutboundPacket Packet(int index) => ref packets[index];

        internal ReadOnlySpan<byte> Payload(int index) => payloads.AsSpan(index * VoicePacketBytes, packets[index].Length);

        internal bool TryAdd(ulong timestamp, VoiceFrameFlags flags, ReadOnlySpan<byte> payload, long capturedMicros, long generation = 0)
        {
            if (Count == PacketCapacity || payload.Length < 1 || payload.Length > VoicePacketBytes) return false;
            payload.CopyTo(payloads.AsSpan(Count * VoicePacketBytes));
            packets[Count++] = new VoiceOutboundPacket
            {
                Timestamp = timestamp,
                Flags = flags,
                Length = payload.Length,
                CapturedMicros = capturedMicros,
                Generation = generation
            };
            return true;
        }

        internal void Clear() => Count = 0;
    }
}
