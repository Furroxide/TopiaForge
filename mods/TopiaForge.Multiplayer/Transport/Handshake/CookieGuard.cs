using System;
using System.Security.Cryptography;
using TopiaForge.Multiplayer.Transport.Packets;

namespace TopiaForge.Multiplayer.Transport.Handshake
{
    /// <summary>
    /// Stateless proof that an initiator can receive at the path it claims. A cookie is the first 16 bytes of
    /// HMAC-SHA256 over the protocol version, the time slot, the initiator's connection id and the path key, under a
    /// secret that rotates every two minutes. The previous secret stays valid for one slot, so a cookie issued just
    /// before a rotation still works.
    /// </summary>
    internal sealed class CookieGuard
    {
        internal const long SlotMicros = 120_000_000;

        private readonly ITransportRandom random;
        private byte[] currentSecret;
        private byte[] previousSecret;
        private long currentSlot;

        internal CookieGuard(ITransportRandom random, long now)
        {
            this.random = random;
            currentSlot = now / SlotMicros;
            currentSecret = NewSecret();
            previousSecret = NewSecret();
        }

        internal byte[] Issue(TransportPath path, ulong initiatorConnectionId, long now)
        {
            Rotate(now);
            return Compute(currentSecret, currentSlot, path, initiatorConnectionId);
        }

        internal bool Verify(byte[] cookie, TransportPath path, ulong initiatorConnectionId, long now)
        {
            if (cookie == null || cookie.Length != HandshakePackets.CookieSize) return false;
            Rotate(now);
            return FixedTimeEquals(cookie, Compute(currentSecret, currentSlot, path, initiatorConnectionId)) ||
                FixedTimeEquals(cookie, Compute(previousSecret, currentSlot - 1, path, initiatorConnectionId));
        }

        /// <summary>Zeroes both secrets. The endpoint calls this as it is disposed, and uses the guard no more.</summary>
        internal void Wipe()
        {
            Array.Clear(currentSecret, 0, currentSecret.Length);
            Array.Clear(previousSecret, 0, previousSecret.Length);
        }

        /// <summary>Moves to the current slot, zeroing each secret it discards.</summary>
        private void Rotate(long now)
        {
            var slot = now / SlotMicros;
            if (slot <= currentSlot) return;
            Array.Clear(previousSecret, 0, previousSecret.Length);
            if (slot == currentSlot + 1)
            {
                previousSecret = currentSecret;
            }
            else
            {
                Array.Clear(currentSecret, 0, currentSecret.Length);
                previousSecret = NewSecret();
            }

            currentSecret = NewSecret();
            currentSlot = slot;
        }

        private byte[] NewSecret()
        {
            var secret = new byte[32];
            random.Fill(secret);
            return secret;
        }

        private static byte[] Compute(byte[] secret, long slot, TransportPath path, ulong initiatorConnectionId)
        {
            var key = path.KeyBytes;
            var input = new byte[1 + 8 + 8 + key.Length];
            input[0] = HandshakePackets.Version;
            for (var index = 0; index < 8; index++)
            {
                input[1 + index] = (byte)((ulong)slot >> (8 * index));
                input[9 + index] = (byte)(initiatorConnectionId >> (8 * index));
            }

            Buffer.BlockCopy(key, 0, input, 17, key.Length);
            using (var hmac = new HMACSHA256(secret))
            {
                var mac = hmac.ComputeHash(input);
                var cookie = new byte[HandshakePackets.CookieSize];
                Buffer.BlockCopy(mac, 0, cookie, 0, cookie.Length);
                return cookie;
            }
        }

        private static bool FixedTimeEquals(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            var difference = 0;
            for (var index = 0; index < left.Length; index++) difference |= left[index] ^ right[index];
            return difference == 0;
        }
    }
}
