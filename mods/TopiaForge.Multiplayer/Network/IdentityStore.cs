using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>The provider's persistent data files, as the network layer sees them. Writes replace a file atomically.</summary>
    internal interface INetworkDataFiles
    {
        /// <summary>Reads a file, or returns null when it does not exist.</summary>
        Task<byte[]?> ReadAsync(string relativePath, CancellationToken cancellationToken);

        Task WriteAsync(string relativePath, byte[] content, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Loads, creates and rotates this install's <see cref="LocalIdentity"/> in <c>multiplayer/identity.json</c>. An
    /// unreadable file is kept beside a fresh identity as <c>multiplayer/identity.unreadable.json</c>, so a damaged file
    /// is never silently lost; nothing about the keys is ever logged.
    /// </summary>
    internal sealed class IdentityStore
    {
        internal const string IdentityPath = "multiplayer/identity.json";
        internal const string UnreadablePath = "multiplayer/identity.unreadable.json";

        private readonly INetworkDataFiles files;
        private readonly Func<long> unixSeconds;

        internal IdentityStore(INetworkDataFiles files, Func<long>? unixSeconds = null)
        {
            this.files = files ?? throw new ArgumentNullException(nameof(files));
            this.unixSeconds = unixSeconds ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        }

        /// <summary>Raised when an unreadable identity file was set aside and replaced. It carries no key material.</summary>
        internal event Action<string>? IdentityReplaced;

        internal async Task<LocalIdentity> LoadOrCreateAsync(CancellationToken cancellationToken)
        {
            var bytes = await files.ReadAsync(IdentityPath, cancellationToken).ConfigureAwait(false);
            if (bytes != null)
            {
                if (LocalIdentity.TryParse(bytes, out var identity, out var error)) return identity!;
                await files.WriteAsync(UnreadablePath, bytes, cancellationToken).ConfigureAwait(false);
                IdentityReplaced?.Invoke("The multiplayer identity file was unreadable (" + error + "), so a new identity was created.");
            }

            return await CreateAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Replaces the identity with a new one. Hosts that knew the old one see a new player.</summary>
        internal Task<LocalIdentity> RotateAsync(CancellationToken cancellationToken) => CreateAsync(cancellationToken);

        private async Task<LocalIdentity> CreateAsync(CancellationToken cancellationToken)
        {
            var identity = LocalIdentity.Create(unixSeconds());
            await files.WriteAsync(IdentityPath, identity.Serialize(), cancellationToken).ConfigureAwait(false);
            return identity;
        }
    }

    internal enum KnownHostCheck
    {
        /// <summary>This address has not been seen before.</summary>
        Unknown = 0,

        /// <summary>This address served the same server before.</summary>
        Match = 1,

        /// <summary>This address served a different server before: its identity changed, or someone is impersonating it.</summary>
        Mismatch = 2
    }

    /// <summary>
    /// Trust on first use for addresses typed by hand, which carry no server id. Each entry maps the SHA-256 of a
    /// normalized <c>host:port</c> to the server id its descriptor proved, so the file never holds an address.
    /// Links, listings and beacons name a server id and need no entry here.
    /// </summary>
    internal sealed class KnownHosts
    {
        internal const string Schema = "topiaforge.known-hosts/1";
        internal const string FilePath = "multiplayer/known-hosts.json";
        internal const int MaximumEntries = 256;
        private const int MaximumBytes = 64 * 1024;

        private readonly List<Entry> entries = new List<Entry>();

        internal int Count => entries.Count;

        internal static string TargetKey(string host, int port)
        {
            if (!SessionLink.TryNormalizeHost(host, out var normalized)) throw new ArgumentException("A host is a DNS name or an IP address.", nameof(host));
            if (port < 1 || port > 65535) throw new ArgumentOutOfRangeException(nameof(port));
            return WireText.ToHex(WireText.Sha256(Encoding.UTF8.GetBytes(normalized + ":" + port.ToString(CultureInfo.InvariantCulture))));
        }

        internal KnownHostCheck Check(string host, int port, string serverId)
        {
            var target = TargetKey(host, port);
            foreach (var entry in entries)
            {
                if (entry.Target == target) return entry.ServerId == serverId ? KnownHostCheck.Match : KnownHostCheck.Mismatch;
            }

            return KnownHostCheck.Unknown;
        }

        /// <summary>Records that the address served <paramref name="serverId"/>, replacing any earlier record.</summary>
        internal void Remember(string host, int port, string serverId, long unixSeconds)
        {
            if (!WireText.IsHex(serverId, 32)) throw new ArgumentException("A server id is 64 lowercase hexadecimal characters.", nameof(serverId));
            var target = TargetKey(host, port);
            entries.RemoveAll(entry => entry.Target == target);
            entries.Add(new Entry(target, serverId, Math.Max(0, unixSeconds)));
            if (entries.Count > MaximumEntries) entries.RemoveAt(0);
        }

        internal bool Forget(string host, int port)
        {
            var target = TargetKey(host, port);
            return entries.RemoveAll(entry => entry.Target == target) > 0;
        }

        internal byte[] Serialize()
        {
            var items = new List<JsonNode>(entries.Count);
            foreach (var entry in entries)
            {
                items.Add(new JsonObjectNode().Add("target", entry.Target).Add("serverId", entry.ServerId).Add("seenAt", entry.SeenAt));
            }

            return CanonicalJson.Serialize(new JsonObjectNode().Add("schema", Schema).Add("hosts", new JsonArrayNode(items)));
        }

        /// <summary>Reads the file. An unreadable file yields an empty record rather than an error: it only costs a prompt.</summary>
        internal static KnownHosts Parse(byte[]? bytes)
        {
            var result = new KnownHosts();
            if (bytes == null || !CanonicalJson.TryParse(bytes, MaximumBytes, out var node, out _)) return result;
            if (!(node is JsonObjectNode value) || !value.HasExactly("schema", "hosts") ||
                !value.TryGet("schema", out var schema) || !(schema is JsonStringNode text) || text.Value != Schema ||
                !value.TryGet("hosts", out var hosts) || !(hosts is JsonArrayNode list))
            {
                return result;
            }

            foreach (var item in list.Items)
            {
                if (result.entries.Count == MaximumEntries) break;
                if (!(item is JsonObjectNode entry) || !entry.HasExactly("target", "serverId", "seenAt")) continue;
                if (!entry.TryGet("target", out var targetNode) || !(targetNode is JsonStringNode target) || !WireText.IsHex(target.Value, 32)) continue;
                if (!entry.TryGet("serverId", out var serverNode) || !(serverNode is JsonStringNode server) || !WireText.IsHex(server.Value, 32)) continue;
                if (!entry.TryGet("seenAt", out var seenNode) || !(seenNode is JsonIntegerNode seen)) continue;
                result.entries.RemoveAll(existing => existing.Target == target.Value);
                result.entries.Add(new Entry(target.Value, server.Value, Math.Max(0, seen.Value)));
            }

            return result;
        }

        private readonly struct Entry
        {
            internal Entry(string target, string serverId, long seenAt)
            {
                Target = target;
                ServerId = serverId;
                SeenAt = seenAt;
            }

            internal string Target { get; }
            internal string ServerId { get; }
            internal long SeenAt { get; }
        }
    }
}
