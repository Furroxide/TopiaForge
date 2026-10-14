using System;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Value checks for the session launch documents: request V5, progress and outcome V2, and the session channels. They
    /// apply the launcher's rules exactly. No refusal repeats the refused value, which may be an address.
    /// </summary>
    internal static class LaunchSessionValues
    {
        internal const int MinimumDisplayName = 2;
        internal const int MaximumDisplayName = 24;
        internal const int MaximumServerName = 48;
        internal const int MinimumPlayers = 2;
        internal const int MaximumPlayers = 16;
        internal const int MaximumProblems = 64;
        internal const int MaximumProblemLength = 256;
        internal const int MaximumDetailLength = 1024;
        internal const int DefaultGamePort = 27250;
        private const string InviteAlphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

        /// <summary>
        /// Display text of <paramref name="minimum"/> to <paramref name="maximum"/> UTF-16 code units: no surrounding white
        /// space, no control characters and no unpaired surrogates.
        /// </summary>
        internal static string DisplayText(string? value, string name, int minimum, int maximum)
        {
            if (value == null || value.Length < Math.Max(1, minimum) || value.Length > maximum
                || char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[value.Length - 1]))
                throw new ArgumentException("Invalid session display text.", name);
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (char.IsControl(character)) throw new ArgumentException("Invalid session display text.", name);
                if (char.IsHighSurrogate(character))
                {
                    if (index + 1 >= value.Length || !char.IsLowSurrogate(value[index + 1]))
                        throw new ArgumentException("Invalid session display text.", name);
                    index++;
                }
                else if (char.IsLowSurrogate(character))
                {
                    throw new ArgumentException("Invalid session display text.", name);
                }
            }

            return value;
        }

        /// <summary>A server id, descriptor hash or package digest: 64 lowercase hexadecimal digits.</summary>
        internal static string Sha256(string? value, string name)
        {
            if (value == null || value.Length != 64) throw new ArgumentException("Invalid session hash.", name);
            foreach (var character in value)
            {
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                    throw new ArgumentException("Invalid session hash.", name);
            }

            return value;
        }

        /// <summary>A canonical invite code: 10 upper-case Crockford base32 characters.</summary>
        internal static string InviteCode(string? value, string name)
        {
            if (value == null || value.Length != 10) throw new ArgumentException("Invalid invite code.", name);
            foreach (var character in value)
            {
                if (InviteAlphabet.IndexOf(character) < 0) throw new ArgumentException("Invalid invite code.", name);
            }

            return value;
        }

        /// <summary>
        /// A control-plane origin: <c>https://</c>, a lowercase DNS name and an optional port, nothing more. An IP literal is
        /// refused, so no address can pass as the service.
        /// </summary>
        internal static string ServiceOrigin(string? value, string name)
        {
            const string scheme = "https://";
            if (value == null || value.Length > 256 || !value.StartsWith(scheme, StringComparison.Ordinal))
                throw new ArgumentException("Invalid service origin.", name);
            var authority = value.Substring(scheme.Length);
            var colon = authority.IndexOf(':');
            var host = colon < 0 ? authority : authority.Substring(0, colon);
            if (colon >= 0 && !IsPortText(authority.Substring(colon + 1)))
                throw new ArgumentException("Invalid service origin.", name);
            if (host.Length == 0 || LaunchSessionHosts.TryNormalize(host) != host || IsAddressLiteral(host))
                throw new ArgumentException("Invalid service origin.", name);
            return value;
        }

        /// <summary>A direct target's host, in the one spelling the launcher writes.</summary>
        internal static string DirectHost(string? value, string name)
        {
            if (value == null || LaunchSessionHosts.TryNormalize(value) != value) throw new ArgumentException("Invalid direct host.", name);
            return value;
        }

        internal static int Ranged(int value, string name, int minimum, int maximum)
        {
            if (value < minimum || value > maximum) throw new ArgumentException("Session number out of range.", name);
            return value;
        }

        internal static string Choice(string? value, string name, string[] choices)
        {
            if (value == null || Array.IndexOf(choices, value) < 0) throw new ArgumentException("Unknown session value.", name);
            return value;
        }

        private static bool IsAddressLiteral(string canonicalHost)
        {
            if (canonicalHost.IndexOf(':') >= 0) return true;
            var parts = canonicalHost.Split('.');
            if (parts.Length != 4) return false;
            foreach (var part in parts)
            {
                if (part.Length == 0) return false;
                foreach (var character in part)
                {
                    if (character < '0' || character > '9') return false;
                }
            }

            return true;
        }

        private static bool IsPortText(string text)
        {
            if (text.Length == 0 || text.Length > 5 || (text.Length > 1 && text[0] == '0')) return false;
            var port = 0;
            foreach (var character in text)
            {
                if (character < '0' || character > '9') return false;
                port = (port * 10) + (character - '0');
            }

            return port >= 1 && port <= 65535;
        }
    }
}
