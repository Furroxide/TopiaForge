using System.Collections.Generic;

namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>One datagram ready to send. Its buffer is pooled: hand it back with the endpoint's <c>Release</c>.</summary>
    internal readonly struct OutgoingDatagram
    {
        internal OutgoingDatagram(TransportPath path, byte[] buffer, int length)
        {
            Path = path;
            Buffer = buffer;
            Length = length;
        }

        internal TransportPath Path { get; }

        internal byte[] Buffer { get; }

        internal int Length { get; }
    }

    /// <summary>Datagram-sized buffers reused across sends, so a busy connection does not feed the collector.</summary>
    internal sealed class DatagramPool
    {
        private const int Retained = 256;
        private readonly Stack<byte[]> free = new Stack<byte[]>();

        internal byte[] Rent() => free.Count > 0 ? free.Pop() : new byte[TransportOptions.MaximumDatagramBytes];

        internal void Return(byte[] buffer)
        {
            if (buffer != null && buffer.Length == TransportOptions.MaximumDatagramBytes && free.Count < Retained) free.Push(buffer);
        }
    }
}
