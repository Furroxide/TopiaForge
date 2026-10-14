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

    /// <summary>
    /// A listen host's admission, ahead of the engine's own protocol and contract checks:
    /// <list type="bullet">
    /// <item>A banned identity is refused first.</item>
    /// <item>A password-protected session needs a proof bound to this link's handshake. Five wrong proofs from one
    /// identity within a minute refuse it for the rest of that minute, so guessing online is slow as well as offline.</item>
    /// <item>The runtime's content admission decides last.</item>
    /// </list>
    /// Every refusal carries an <see cref="AdmissionReport"/> for the joining player.
    /// </summary>
    internal sealed class HostPolicy : IJoinPolicy
    {
        internal const int AttemptsPerMinute = 5;
        private const long MinuteMicros = 60_000_000;
        private const int TrackedIdentities = 1024;

        private readonly BanList bans;
        private readonly byte[]? passwordKey;
        private readonly ILiveSessionContent content;
        private readonly Func<long> clock;
        private readonly Dictionary<string, Attempts> failures = new Dictionary<string, Attempts>(StringComparer.Ordinal);

        internal HostPolicy(BanList bans, byte[]? passwordKey, ILiveSessionContent content, Func<long> clock)
        {
            this.bans = bans ?? throw new ArgumentNullException(nameof(bans));
            this.passwordKey = passwordKey == null ? null : (byte[])passwordKey.Clone();
            this.content = content ?? throw new ArgumentNullException(nameof(content));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

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

            return content.Admit(clientContent) ?? JoinDecision.Accept();
        }

        private JoinDecision? CheckPassword(JoinRequestContext context, byte[]? proof)
        {
            var now = clock();
            Attempts? attempts = failures.TryGetValue(context.PeerIdentity, out var found) ? found : null;
            if (attempts != null && now - attempts.WindowStart >= MinuteMicros)
            {
                failures.Remove(context.PeerIdentity);
                attempts = null;
            }

            if (attempts != null && attempts.Count >= AttemptsPerMinute)
            {
                return JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, "Too many wrong passwords; wait a minute.", AdmissionReport.Encode(AdmissionCode.TooManyAttempts));
            }

            if (proof == null)
            {
                return JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, "This session needs a password.", AdmissionReport.Encode(AdmissionCode.PasswordRequired));
            }

            if (PasswordProof.Verify(passwordKey!, context.Link.ChannelBinding, proof)) return null;
            if (attempts == null)
            {
                if (failures.Count >= TrackedIdentities) failures.Clear();
                attempts = new Attempts(now);
                failures[context.PeerIdentity] = attempts;
            }

            attempts.Count++;
            return JoinDecision.Reject(MultiplayerSessionEndReason.AdmissionRejected, "The password is wrong.", AdmissionReport.Encode(AdmissionCode.PasswordWrong));
        }

        /// <summary>Wipes the password key once the host stops; a stopped host admits nobody.</summary>
        internal void Wipe()
        {
            if (passwordKey != null) Array.Clear(passwordKey, 0, passwordKey.Length);
        }

        private sealed class Attempts
        {
            internal Attempts(long windowStart)
            {
                WindowStart = windowStart;
            }

            internal long WindowStart { get; }
            internal int Count { get; set; }
        }
    }
}
