using System;
using System.Text;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>Versioned, bounded chat frames. A request carries no claimed sender, command or formatting flags.</summary>
    internal static class ChatWire
    {
        internal const int MaximumCharacters = 256;
        internal const int MaximumBytes = 4 + 64 + MaximumCharacters * 3;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);

        internal static bool IsValid(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) || text!.Length > MaximumCharacters) return false;
            for (var index = 0; index < text.Length; index++)
            {
                if (!char.IsSurrogate(text[index])) continue;
                if (!char.IsHighSurrogate(text[index]) || index + 1 == text.Length || !char.IsLowSurrogate(text[++index])) return false;
            }
            return true;
        }

        internal static byte[] Write(string text, string sender = "")
        {
            if (!IsValid(text) || sender.Length > 64) throw new ArgumentException("Chat text is outside its bounds.");
            var name = Utf8.GetBytes(sender);
            if (name.Length > 64) throw new ArgumentException("Chat sender is outside its bounds.");
            var body = Utf8.GetBytes(text);
            var bytes = new byte[4 + name.Length + body.Length];
            bytes[0] = 1;
            bytes[1] = (byte)name.Length;
            bytes[2] = (byte)body.Length;
            bytes[3] = (byte)(body.Length >> 8);
            name.CopyTo(bytes, 4);
            body.CopyTo(bytes, 4 + name.Length);
            return bytes;
        }

        internal static bool TryRead(byte[] bytes, bool relay, out string sender, out string text)
        {
            sender = text = string.Empty;
            if (bytes == null || bytes.Length < 5 || bytes.Length > MaximumBytes || bytes[0] != 1) return false;
            var nameLength = bytes[1];
            var length = bytes[2] | (bytes[3] << 8);
            if (nameLength > 64 || (relay ? nameLength == 0 : nameLength != 0) || bytes.Length != 4 + nameLength + length) return false;
            try
            {
                sender = Utf8.GetString(bytes, 4, nameLength);
                text = Utf8.GetString(bytes, 4 + nameLength, length);
                return IsValid(text);
            }
            catch (DecoderFallbackException) { return false; }
        }
    }

    /// <summary>
    /// Three messages at once, then one every two seconds; a monotonic clock, no timer or frame allocation. A sender counts
    /// its own sends exactly. The host counts arrivals, which a link's delay can bunch, so it takes a message up to a second
    /// early: half a message of slack, and still never more than three at once. A client bounding relays allows a second
    /// more, as a relay crossed the host's link and then its own. Over time the rate is one every two seconds everywhere.
    /// </summary>
    internal struct ChatRateLimit
    {
        /// <summary>The host's slack for arrivals a link bunched: half a message, so a message may arrive a second early.</summary>
        internal const double HostSlack = 0.5;

        /// <summary>A client's slack for relays, which crossed two links: one message, so a relay may arrive two seconds early.</summary>
        internal const double RelaySlack = 1;

        private const double Burst = 3;
        private const double MicrosPerMessage = 2_000_000d;
        private double tokens;
        private long sampled;
        private bool started;

        /// <summary>Gets whether the allowance has refilled completely, so forgetting it changes nothing.</summary>
        internal bool IsExpired(long now, double slack) => started && now >= sampled && now - sampled >= (Burst + slack) * MicrosPerMessage;

        internal bool Take(long now, double slack = 0)
        {
            var capacity = Burst + slack;
            if (!started) { tokens = capacity; sampled = now; started = true; }
            if (now > sampled) { tokens = Math.Min(capacity, tokens + (now - sampled) / MicrosPerMessage); sampled = now; }
            if (tokens < 1) return false;
            tokens--;
            return true;
        }
    }
}
