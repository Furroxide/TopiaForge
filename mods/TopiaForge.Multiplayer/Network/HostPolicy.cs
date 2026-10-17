using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// The identities a host has banned, kept in <c>multiplayer/bans.json</c>. A ban is keyed by the player's transport
    /// key, which is all a host knows about them; rotating that key escapes it, as the player-facing docs say.
    /// </summary>
    internal sealed class BanList
    {
        internal const string Schema = "topiaforge.bans/1";
        internal const string FilePath = "multiplayer/bans.json";
        internal const int MaximumEntries = 1024;
        /// <summary>The file's read bound: 1,024 bans with the longest names fit with room to spare.</summary>
        private const int MaximumBytes = 512 * 1024;

        private readonly List<Entry> entries = new List<Entry>();
        private readonly object gate = new object();

        internal int Count
        {
            get
            {
                lock (gate) return entries.Count;
            }
        }

        internal bool IsBanned(string identity)
        {
            lock (gate)
            {
                foreach (var entry in entries)
                {
                    if (entry.Identity == identity) return true;
                }
            }

            return false;
        }

        /// <summary>Bans <paramref name="identity"/>; returns false when it already was. The oldest ban gives way past the cap.</summary>
        internal bool Add(string identity, string displayName, long unixSeconds)
        {
            if (!WireText.IsHex(identity, 32)) throw new ArgumentException("An identity is a 32-byte key in lowercase hexadecimal.", nameof(identity));
            var name = WireText.IsDisplayText(displayName, ServerDescriptor.MaximumNameLength) ? displayName : "Player";
            lock (gate)
            {
                foreach (var entry in entries)
                {
                    if (entry.Identity == identity) return false;
                }

                entries.Add(new Entry(identity, name, Math.Max(0, unixSeconds)));
                if (entries.Count > MaximumEntries) entries.RemoveAt(0);
                return true;
            }
        }

        internal bool Remove(string identity)
        {
            lock (gate) return entries.RemoveAll(entry => entry.Identity == identity) > 0;
        }

        /// <summary>Gets a copy of the bans, oldest first.</summary>
        internal IReadOnlyList<BanRecord> Records()
        {
            lock (gate)
            {
                var copy = new BanRecord[entries.Count];
                for (var index = 0; index < copy.Length; index++) copy[index] = new BanRecord(entries[index].Identity, entries[index].Name, entries[index].BannedAt);
                return copy;
            }
        }

        internal byte[] Serialize()
        {
            var items = new List<JsonNode>();
            lock (gate)
            {
                foreach (var entry in entries)
                {
                    items.Add(new JsonObjectNode().Add("identity", entry.Identity).Add("name", entry.Name).Add("bannedAt", entry.BannedAt));
                }
            }

            return CanonicalJson.Serialize(new JsonObjectNode().Add("schema", Schema).Add("bans", new JsonArrayNode(items)));
        }

        /// <summary>Reads the file. An unreadable file yields no bans rather than an error, and the next save replaces it.</summary>
        internal static BanList Parse(byte[]? bytes)
        {
            var result = new BanList();
            if (bytes == null || !CanonicalJson.TryParse(bytes, MaximumBytes, out var node, out _)) return result;
            if (!(node is JsonObjectNode value) || !value.HasExactly("schema", "bans") ||
                !value.TryGet("schema", out var schema) || !(schema is JsonStringNode text) || text.Value != Schema ||
                !value.TryGet("bans", out var list) || !(list is JsonArrayNode array))
            {
                return result;
            }

            foreach (var item in array.Items)
            {
                if (!(item is JsonObjectNode entry) || !entry.HasExactly("identity", "name", "bannedAt")) continue;
                if (!entry.TryGet("identity", out var identity) || !(identity is JsonStringNode identityText) || !WireText.IsHex(identityText.Value, 32)) continue;
                if (!entry.TryGet("name", out var name) || !(name is JsonStringNode nameText)) continue;
                if (!entry.TryGet("bannedAt", out var at) || !(at is JsonIntegerNode atValue)) continue;
                result.Add(identityText.Value, nameText.Value, atValue.Value);
            }

            return result;
        }

        internal static async Task<BanList> LoadAsync(INetworkDataFiles files, CancellationToken cancellationToken) =>
            Parse(await files.ReadAsync(FilePath, cancellationToken).ConfigureAwait(false));

        private readonly struct Entry
        {
            internal Entry(string identity, string name, long bannedAt)
            {
                Identity = identity;
                Name = name;
                BannedAt = bannedAt;
            }

            internal string Identity { get; }
            internal string Name { get; }
            internal long BannedAt { get; }
        }
    }

    /// <summary>One ban: the banned transport key in hexadecimal, the name it had, and when.</summary>
    internal readonly struct BanRecord
    {
        internal BanRecord(string identity, string name, long bannedAt)
        {
            Identity = identity;
            Name = name;
            BannedAt = bannedAt;
        }

        internal string Identity { get; }
        internal string Name { get; }
        internal long BannedAt { get; }
    }

    /// <summary>
    /// A listen host's admission, ahead of the engine's own protocol and contract checks:
    /// <list type="bullet">
    /// <item>A banned identity is refused first.</item>
    /// <item>A password-protected session needs a proof bound to this link's handshake. Wrong proofs are limited per
    /// identity, per source network and for the whole host (<see cref="PasswordGuessLimiter"/>): one network gets five
    /// guesses before a lockout that grows to eight minutes, and the whole host fifteen, so guessing online takes days
    /// rather than minutes. A returning player, one the host already holds a place for, is never held back by them.</item>
    /// <item>The runtime's content admission decides last.</item>
    /// </list>
    /// Every refusal carries an <see cref="AdmissionReport"/> for the joining player.
    /// </summary>
    internal sealed class HostPolicy : IJoinPolicy
    {
        internal const int AttemptsPerMinute = PasswordGuessLimits.DefaultPerIdentity;

        private readonly BanList bans;
        private readonly byte[]? passwordKey;
        private readonly ILiveSessionContent content;
        private readonly Func<long> clock;

        internal HostPolicy(BanList bans, byte[]? passwordKey, ILiveSessionContent content, Func<long> clock, PasswordGuessLimits? limits = null)
        {
            this.bans = bans ?? throw new ArgumentNullException(nameof(bans));
            this.passwordKey = passwordKey == null ? null : (byte[])passwordKey.Clone();
            this.content = content ?? throw new ArgumentNullException(nameof(content));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            Guesses = new PasswordGuessLimiter(limits);
        }

        /// <summary>Gets the limiter that counts wrong password proofs; tests read its tables.</summary>
        internal PasswordGuessLimiter Guesses { get; }

        public JoinDecision Evaluate(JoinRequestContext context)
        {
            if (bans.IsBanned(context.PeerIdentity))
            {
                return JoinDecision.Reject(MultiplayerSessionEndReason.Banned, "You are banned from this session.", AdmissionReport.Encode(AdmissionCode.Banned));
            }

            var engine = DefaultJoinPolicy.Instance.Evaluate(context);
            if (!engine.Accepted)
            {
                var code = engine.Reason == MultiplayerSessionEndReason.ProtocolMismatch ? AdmissionCode.Protocol : AdmissionCode.Full;
                return JoinDecision.Reject(engine.Reason, engine.Message, AdmissionReport.Encode(code));
            }

            if (!JoinContent.TryDecode(context.Request.Content, out var proof, out var clientContent))
            {
                return JoinDecision.Reject(MultiplayerSessionEndReason.ProtocolMismatch, "The join request was malformed.", AdmissionReport.Encode(AdmissionCode.Protocol));
            }

            if (passwordKey != null)
            {
                var refusal = CheckPassword(context, proof);
                if (refusal.HasValue) return refusal.Value;
            }

            return Admit(clientContent);
        }

        private JoinDecision? CheckPassword(JoinRequestContext context, byte[]? proof)
        {
            var now = clock();
            var identity = context.PeerIdentity;

            // The network layer hands over only an opaque hash of where the link came from, never an address.
            var network = (context.Link as NetworkLink)?.SourceNetwork;

            // A player the host already holds a place for proved the password when it joined, so a lockout meant for
            // guessers never keeps it out; it still proves the password on this link.
            var refusal = context.Returning ? GuessRefusal.None : Guesses.Check(identity, network, now);
            switch (refusal)
            {
                case GuessRefusal.Host:
                    return TooManyAttempts("The host is turning passwords away for a moment after many wrong guesses; try again shortly.");
                case GuessRefusal.Network:
                    return TooManyAttempts("Too many wrong passwords from your network; try again in a few minutes.");
                case GuessRefusal.Identity:
                    return TooManyAttempts("Too many wrong passwords; wait a minute.");
            }

            if (proof == null)
            {
                return JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, "This session needs a password.", AdmissionReport.Encode(AdmissionCode.PasswordRequired));
            }

            if (PasswordProof.Verify(passwordKey!, context.Link.ChannelBinding, proof)) return null;
            if (refusal == GuessRefusal.None && !context.Returning) Guesses.RecordWrong(identity, network, now);
            return JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, "The password is wrong.", AdmissionReport.Encode(AdmissionCode.PasswordWrong));
        }

        /// <summary>
        /// Lets the runtime's content admission decide, and turns a refusal into the report the joining player reads: the
        /// content's code and every problem it listed, so the player learns what to install, not just that it failed.
        /// </summary>
        private JoinDecision Admit(byte[] clientContent)
        {
            var verdict = content.Admit(clientContent);
            if (verdict != null && verdict.IsAdmitted) return JoinDecision.Accept();
            var problems = verdict?.Problems ?? Array.Empty<string>();
            if (verdict?.Code == AdmissionCode.NotReady)
            {
                return JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, "The host is not ready for players yet.", AdmissionReport.Encode(AdmissionCode.NotReady, problems));
            }

            return verdict?.Code == AdmissionCode.Protocol
                ? JoinDecision.Reject(MultiplayerSessionEndReason.ProtocolMismatch, "Your version of TopiaForge cannot join this session.", AdmissionReport.Encode(AdmissionCode.Protocol, problems))
                : JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, "Your packages do not match this session's.", AdmissionReport.Encode(AdmissionCode.Content, problems));
        }

        private static JoinDecision TooManyAttempts(string message) =>
            JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, message, AdmissionReport.Encode(AdmissionCode.TooManyAttempts));

        /// <summary>Wipes the password key once the host stops; a stopped host admits nobody.</summary>
        internal void Wipe()
        {
            if (passwordKey != null) Array.Clear(passwordKey, 0, passwordKey.Length);
        }
    }
}
