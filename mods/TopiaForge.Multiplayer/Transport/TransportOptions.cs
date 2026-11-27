using System;
using TopiaForge.Multiplayer.Engine;
using TopiaForge.Multiplayer.Transport.Crypto;

namespace TopiaForge.Multiplayer.Transport
{
    /// <summary>The randomness a transport draws on: connection ids, ephemeral keys and cookie secrets.</summary>
    internal interface ITransportRandom
    {
        void Fill(byte[] buffer);
    }

    /// <summary>Production randomness: the platform's cryptographic generator.</summary>
    internal sealed class CryptoTransportRandom : ITransportRandom
    {
        internal static readonly CryptoTransportRandom Instance = new CryptoTransportRandom();

        public void Fill(byte[] buffer) => CryptoRandom.Fill(buffer);
    }

    internal enum TransportDiagnosticKind
    {
        /// <summary>A connection completed its handshake.</summary>
        Established = 0,

        /// <summary>A connection ended; the message says why.</summary>
        Closed = 1,

        /// <summary>A peer broke the protocol after authenticating; its connection was closed.</summary>
        ProtocolViolation = 2,

        /// <summary>A handshake was refused or abandoned.</summary>
        HandshakeFailed = 3,

        /// <summary>A connection moved to a path it had just validated.</summary>
        PathChanged = 4
    }

    /// <summary>Receives transport events. A report names a connection by its local number, never by path or address.</summary>
    internal interface ITransportDiagnostics
    {
        void Report(TransportDiagnosticKind kind, long connectionNumber, string message);
    }

    /// <summary>Tuning for one endpoint. The defaults are the plan's: they are what production uses.</summary>
    internal sealed class TransportOptions
    {
        internal const int MaximumDatagramBytes = 1200;

        internal ITransportRandom Random { get; set; } = CryptoTransportRandom.Instance;

        internal ITransportDiagnostics? Diagnostics { get; set; }

        /// <summary>Gets or sets whether every Initiation must carry a cookie, not only under load.</summary>
        internal bool AlwaysRequireCookie { get; set; }

        /// <summary>
        /// Gets or sets the player's "hide my IP" choice, which is on unless the player allowed direct play: direct play
        /// is never a silent default, so a caller sets this false only for a player who chose it. The endpoint then uses
        /// only relayed and LAN paths: it never sends a transport datagram to a direct Internet path, and drops every one
        /// that arrives from such a path. STUN and TURN traffic still reaches the NAT layer, which needs it to reach the relay.
        /// </summary>
        internal bool RelayOnly { get; set; } = true;

        /// <summary>
        /// Gets or sets whether peers may open connections to this endpoint. Only a host accepts them; a joining player
        /// or a LAN browser refuses every Initiation, so nobody else can connect to it or learn its key.
        /// </summary>
        internal bool AcceptsInbound { get; set; } = true;

        internal int MaximumConnections { get; set; } = 64;

        internal int MaximumHalfOpen { get; set; } = 256;

        /// <summary>Gets or sets how many half-open handshakes make the endpoint demand cookies.</summary>
        internal int CookieHalfOpenThreshold { get; set; } = 64;

        internal int HandshakesPerSecondPerSource { get; set; } = 10;

        internal int HandshakesPerSecond { get; set; } = 200;

        internal long HandshakeTimeoutMicros { get; set; } = 5_000_000;

        internal long InitialRetransmitMicros { get; set; } = 250_000;

        internal long KeepaliveMicros { get; set; } = 1_000_000;

        internal long StallMicros { get; set; } = 3_000_000;

        internal long IdleTimeoutMicros { get; set; } = 10_000_000;

        internal long MaximumAckDelayMicros { get; set; } = 25_000;

        internal long InitialRttMicros { get; set; } = 250_000;

        /// <summary>Gets or sets the sustained outbound rate per connection: 256 kbit/s.</summary>
        internal int SustainedBytesPerSecond { get; set; } = 32 * 1024;

        /// <summary>Gets or sets the burst rate per connection: 512 kbit/s, for up to two seconds.</summary>
        internal int BurstBytesPerSecond { get; set; } = 64 * 1024;

        internal long BurstMicros { get; set; } = 2_000_000;

        /// <summary>
        /// Gets or sets the voice share of each connection: 96 kbit/s, the host's downstream cap. A client's endpoint
        /// sets 4 KiB/s, the 32 kbit/s upstream cap.
        /// </summary>
        internal int VoiceBytesPerSecond { get; set; } = 12 * 1024;

        /// <summary>Gets or sets the Realtime share of each connection: 64 kbit/s.</summary>
        internal int RealtimeBytesPerSecond { get; set; } = 8 * 1024;

        /// <summary>Gets the Session share each connection keeps whatever the datagram classes do: 96 kbit/s.</summary>
        internal const int SessionGuaranteeBytesPerSecond = 12 * 1024;

        /// <summary>Gets or sets the outbound budget all connections share: 4,000 kbit/s, the default host uplink.</summary>
        internal long UplinkBytesPerSecond { get; set; } = 500_000;

        /// <summary>Gets the most bytes a session frame's length prefix takes.</summary>
        internal const int FramePrefixBytes = 5;

        /// <summary>Gets or sets the largest session frame either side sends or accepts: the engine's bound.</summary>
        internal int MaximumFrameBytes { get; set; } = ProtocolLimits.MaximumFrameBytes;

        internal int StreamWindowBytes { get; set; } = 2 * 1024 * 1024;

        internal long ProcessReassemblyBytes { get; set; } = 32L * 1024 * 1024;

        internal long SendBacklogBytes { get; set; } = 16L * 1024 * 1024;

        internal void Validate()
        {
            if (Random == null) throw new ArgumentException("A random source is required.", nameof(Random));
            if (MaximumConnections < 1 || MaximumHalfOpen < 1 || CookieHalfOpenThreshold < 0) throw new ArgumentOutOfRangeException(nameof(MaximumConnections));
            if (HandshakesPerSecondPerSource < 1 || HandshakesPerSecond < 1) throw new ArgumentOutOfRangeException(nameof(HandshakesPerSecond));
            if (HandshakeTimeoutMicros <= 0 || InitialRetransmitMicros <= 0 || KeepaliveMicros <= 0) throw new ArgumentOutOfRangeException(nameof(HandshakeTimeoutMicros));
            if (StallMicros <= 0 || IdleTimeoutMicros <= StallMicros) throw new ArgumentOutOfRangeException(nameof(IdleTimeoutMicros));
            if (MaximumAckDelayMicros <= 0 || InitialRttMicros <= 0) throw new ArgumentOutOfRangeException(nameof(InitialRttMicros));
            if (SustainedBytesPerSecond < 1200 || BurstBytesPerSecond < SustainedBytesPerSecond || BurstMicros < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(BurstBytesPerSecond));
            }

            // The datagram classes are capped so Session always keeps its guaranteed share of the sustained rate.
            if (VoiceBytesPerSecond < 0 || RealtimeBytesPerSecond < 0 ||
                SustainedBytesPerSecond - VoiceBytesPerSecond - RealtimeBytesPerSecond < SessionGuaranteeBytesPerSecond)
            {
                throw new ArgumentOutOfRangeException(nameof(VoiceBytesPerSecond));
            }

            if (UplinkBytesPerSecond < BurstBytesPerSecond) throw new ArgumentOutOfRangeException(nameof(UplinkBytesPerSecond));

            // A frame completes only inside the window, so the window must hold the largest frame and its prefix.
            if (MaximumFrameBytes < 1 || StreamWindowBytes < MaximumFrameBytes + FramePrefixBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(StreamWindowBytes));
            }

            if (ProcessReassemblyBytes < StreamWindowBytes || SendBacklogBytes < StreamWindowBytes)
            {
                throw new ArgumentOutOfRangeException(nameof(ProcessReassemblyBytes));
            }
        }
    }
}
