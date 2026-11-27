using System.Collections.Generic;

namespace TopiaForge.Multiplayer.Transport.Streams
{
    /// <summary>
    /// One unreliable, sequenced channel (Realtime or Voice). Frames wait in a short queue for the pacer; when it
    /// overflows the oldest frame goes. The receiver delivers only frames newer than the last one it delivered.
    /// </summary>
    internal sealed class DatagramChannel
    {
        internal const int MaximumQueued = 64;

        private readonly Queue<byte[]> queued = new Queue<byte[]>();
        private ulong lastDelivered;
        private bool anyDelivered;

        internal DatagramChannel(byte number)
        {
            Number = number;
        }

        internal byte Number { get; }

        internal ulong NextSequence { get; private set; }

        internal int QueuedCount => queued.Count;

        internal void Enqueue(byte[] payload)
        {
            if (queued.Count == MaximumQueued) queued.Dequeue();
            queued.Enqueue(payload);
        }

        internal byte[] PeekQueued() => queued.Peek();

        /// <summary>Removes the frame at the head of the queue and returns the sequence it travels with.</summary>
        internal ulong TakeQueued()
        {
            queued.Dequeue();
            return NextSequence++;
        }

        internal void ClearQueued() => queued.Clear();

        /// <summary>Gets whether a received frame is newer than everything delivered on this channel, and records it if so.</summary>
        internal bool ShouldDeliver(ulong sequence)
        {
            if (anyDelivered && sequence <= lastDelivered) return false;
            anyDelivered = true;
            lastDelivered = sequence;
            return true;
        }
    }
}
