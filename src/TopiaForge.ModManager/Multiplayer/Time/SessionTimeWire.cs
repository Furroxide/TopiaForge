using System;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The one message of the session time channel (WS3.13), which flows from the host to its players only:
    /// <c>[version 1][type 1][scale][mode]</c>, seven bytes, the scale an IEEE 754 single in little-endian order and the mode
    /// an <see cref="InternalTimeMode"/>. A reader takes exactly that and refuses anything else: another length, version or
    /// type, a scale that is not finite or lies outside zero to one, an undefined mode, or a mode that disagrees with its
    /// scale. Unity-free. See docs/internal/MultiplayerRuntimeIntegration.md, section 17.
    /// </summary>
    internal static class SessionTimeWire
    {
        /// <summary>The format's version; a newer one is refused, never guessed at.</summary>
        internal const byte Version = 1;

        /// <summary>The message type that carries the session's time.</summary>
        internal const byte TimeType = 1;

        /// <summary>The length of every message: version, type, four bytes of scale and the mode.</summary>
        internal const int Length = 7;

        /// <summary>Writes <paramref name="time"/> into the first <see cref="Length"/> bytes of <paramref name="buffer"/>.</summary>
        internal static void Write(byte[] buffer, InternalSessionTime time)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (buffer.Length < Length) throw new ArgumentException("A session time message needs seven bytes.", nameof(buffer));
            var bits = (uint)BitConverter.SingleToInt32Bits(time.Scale);
            buffer[0] = Version;
            buffer[1] = TimeType;
            buffer[2] = (byte)bits;
            buffer[3] = (byte)(bits >> 8);
            buffer[4] = (byte)(bits >> 16);
            buffer[5] = (byte)(bits >> 24);
            buffer[6] = (byte)time.Mode;
        }

        /// <summary>Reads one message; false, with real time, for anything that is not exactly a valid session time.</summary>
        internal static bool TryRead(byte[]? payload, out InternalSessionTime time)
        {
            time = default;
            if (payload == null || payload.Length != Length || payload[0] != Version || payload[1] != TimeType) return false;
            var bits = payload[2] | (payload[3] << 8) | (payload[4] << 16) | (payload[5] << 24);
            var scale = BitConverter.Int32BitsToSingle(bits);
            return InternalSessionTime.TryCreate(scale, (InternalTimeMode)payload[6], out time);
        }
    }
}
