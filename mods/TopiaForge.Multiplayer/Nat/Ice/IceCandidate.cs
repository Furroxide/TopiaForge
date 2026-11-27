using System;
using System.Net;
using TopiaForge.Multiplayer.Transport;

namespace TopiaForge.Multiplayer.NatTraversal.Ice
{
    /// <summary>Where a candidate address came from (RFC 8445, section 5.1.1). Peer-reflexive ones are learned, never signaled.</summary>
    internal enum IceCandidateKind
    {
        Host = 0,
        ServerReflexive = 1,
        Relayed = 2,
        PeerReflexive = 3
    }

    /// <summary>
    /// One transport address a side can be reached at. Candidates are network-layer data: the signaling wire carries
    /// them, gated by the privacy rules, and nothing else ever does. <see cref="ToString"/> names only the kind.
    /// </summary>
    internal sealed class IceCandidate
    {
        internal const uint HostTypePreference = 126;
        internal const uint PeerReflexiveTypePreference = 110;
        internal const uint ServerReflexiveTypePreference = 100;
        internal const uint RelayedTypePreference = 0;

        internal IceCandidate(IceCandidateKind kind, IPEndPoint endpoint, uint priority, int order = 0)
        {
            Kind = kind;
            EndPoint = new IPEndPoint(NatAddresses.Unmap(endpoint.Address), endpoint.Port);
            Priority = priority;
            Order = order;
        }

        internal IceCandidateKind Kind { get; private set; }

        internal IPEndPoint EndPoint { get; }

        internal uint Priority { get; private set; }

        /// <summary>
        /// Takes the kind and priority the remote signals for an address this side first learned from a check: a
        /// peer-reflexive candidate that turns out to be, say, the remote's new relay candidate is ranked as one from now on.
        /// </summary>
        internal void Promote(IceCandidateKind kind, uint priority)
        {
            if (Kind != IceCandidateKind.PeerReflexive || kind == IceCandidateKind.PeerReflexive) return;
            Kind = kind;
            Priority = priority;
        }

        /// <summary>Gets when this side learned the candidate, counting from zero: a later relay candidate replaces an earlier one.</summary>
        internal int Order { get; }

        /// <summary>
        /// The RFC 8445 priority: type preference in the top byte, then a 16-bit local preference, then 256 minus the
        /// component, which is always 1 here. Host candidates rank above server-reflexive ones, and relayed ones last.
        /// </summary>
        internal static uint ComputePriority(IceCandidateKind kind, int localPreference)
        {
            if (localPreference < 0 || localPreference > 0xFFFF) throw new ArgumentOutOfRangeException(nameof(localPreference));
            uint type;
            switch (kind)
            {
                case IceCandidateKind.Host:
                    type = HostTypePreference;
                    break;
                case IceCandidateKind.PeerReflexive:
                    type = PeerReflexiveTypePreference;
                    break;
                case IceCandidateKind.ServerReflexive:
                    type = ServerReflexiveTypePreference;
                    break;
                default:
                    type = RelayedTypePreference;
                    break;
            }

            return (type << 24) | ((uint)localPreference << 8) | 255u;
        }

        /// <summary>The RFC 8445 pair priority from the controlling side's candidate priority G and the controlled side's D.</summary>
        internal static ulong PairPriority(uint controlling, uint controlled)
        {
            var low = Math.Min(controlling, controlled);
            var high = Math.Max(controlling, controlled);
            return ((ulong)low << 32) + (2UL * high) + (controlling > controlled ? 1UL : 0UL);
        }

        internal bool SameAddress(IPEndPoint endpoint) => NatAddresses.SameEndPoint(EndPoint, endpoint);

        /// <summary>A candidate holds an address, so it prints only its kind.</summary>
        public override string ToString() => Kind + " candidate";
    }

    /// <summary>
    /// ICE credentials: a username fragment and a password (RFC 8445, section 5.3). Each side picks its own, the
    /// signaling grammar carries them, and every connectivity check proves knowledge of the other side's password.
    /// Fragments are 4 to 32 and passwords 22 to 64 characters of A-Z, a-z, 0-9, + and /.
    /// </summary>
    internal sealed class IceCredentials
    {
        internal const int UfragLength = 8;
        internal const int PasswordLength = 24;
        private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

        internal IceCredentials(string ufrag, string password)
        {
            if (!IsValid(ufrag, 4, 32)) throw new ArgumentException("A username fragment is 4 to 32 ICE characters.", nameof(ufrag));
            if (!IsValid(password, 22, 64)) throw new ArgumentException("An ICE password is 22 to 64 ICE characters.", nameof(password));
            Ufrag = ufrag;
            Password = password;
            Key = Stun.StunCredentials.ShortTermKey(password);
        }

        internal string Ufrag { get; }

        internal string Password { get; }

        /// <summary>Gets the short-term key MESSAGE-INTEGRITY uses with this password.</summary>
        internal byte[] Key { get; }

        /// <summary>Draws fresh credentials: 48 and 144 bits of randomness, six bits a character.</summary>
        internal static IceCredentials Generate(ITransportRandom random)
        {
            if (random == null) throw new ArgumentNullException(nameof(random));
            return new IceCredentials(Draw(random, UfragLength), Draw(random, PasswordLength));
        }

        internal static bool TryCreate(string? ufrag, string? password, out IceCredentials credentials)
        {
            credentials = null!;
            if (!IsValid(ufrag, 4, 32) || !IsValid(password, 22, 64)) return false;
            credentials = new IceCredentials(ufrag!, password!);
            return true;
        }

        internal static bool IsValid(string? text, int minimum, int maximum)
        {
            if (text == null || text.Length < minimum || text.Length > maximum) return false;
            foreach (var character in text)
            {
                if (Alphabet.IndexOf(character) < 0) return false;
            }

            return true;
        }

        /// <summary>Credentials are secrets for the session's checks, so they never print.</summary>
        public override string ToString() => "ICE credentials";

        private static string Draw(ITransportRandom random, int length)
        {
            var bytes = new byte[length];
            random.Fill(bytes);
            var characters = new char[length];
            for (var index = 0; index < length; index++) characters[index] = Alphabet[bytes[index] & 63];
            return new string(characters);
        }
    }
}
