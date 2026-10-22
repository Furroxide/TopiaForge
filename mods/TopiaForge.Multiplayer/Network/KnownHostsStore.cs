using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>What the known-hosts record says about the server a typed address serves now.</summary>
    internal enum KnownHostTrust
    {
        /// <summary>
        /// The address served this server before; or it served none, and this one is now recorded; or the player trusted
        /// exactly this server for this join.
        /// </summary>
        Trusted = 0,

        /// <summary>The address served another server before, and the player has not trusted this one.</summary>
        Changed = 1,

        /// <summary>The record could not be read just now, so the server could not be checked; nothing was recorded.</summary>
        Unchecked = 2
    }

    /// <summary>
    /// Trust on first use as live sessions keep it: the <see cref="KnownHosts"/> record and its file,
    /// <c>multiplayer/known-hosts.json</c>, read before the first check and saved as checks change the record.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file that cannot be read just now, as when another program holds it or the disk fails, is never taken for an
    /// empty record and never written over. Until a read succeeds, every check is <see cref="KnownHostTrust.Unchecked"/>,
    /// nothing is recorded and nothing is saved; the next check reads the file again, so trying again once it reads
    /// recovers. A server the player trusts for one join is still let through, as a changed server is, and is not
    /// recorded either. That trust was given without the record, so it holds only while the record cannot be read: once
    /// it reads, an address that served another server is a changed identity again, for the player to decide on in those
    /// words, and a server trusted then is recorded as any changed server the player trusts is.
    /// </para>
    /// <para>
    /// A file that reads but is damaged is salvaged as before: the entries that read are kept, and the rest are dropped.
    /// A record in another layout, with white space, a byte-order mark or its members in another order, reads whole, so it
    /// keeps every pin. Before anything replaces such a file, its bytes are copied to the first of
    /// <see cref="DamagedPaths"/> that holds no file, so a record the game could not use as it stands is never lost
    /// silently; while that copy cannot be written the file stays where it is, to be salvaged again. A copy is never
    /// written over. Anything that does not read back exactly as this game writes it counts as damaged: a file of another
    /// schema, an entry that does not read, more entries than the record keeps, or bytes that are not canonical.
    /// </para>
    /// </remarks>
    internal sealed class KnownHostsStore
    {
        internal const string DamagedPath = "multiplayer/known-hosts.damaged.json";

        /// <summary>
        /// Where a damaged file's bytes are kept, in order, <see cref="DamagedPath"/> first. The provider's file service names
        /// no path and lists nothing, so the copies are a fixed set: each damage takes the first place that holds no file,
        /// and a place that holds one, or cannot be read, is never written. Once all three hold copies, a damaged file is
        /// salvaged without one.
        /// </summary>
        internal static readonly IReadOnlyList<string> DamagedPaths = new[]
        {
            DamagedPath,
            "multiplayer/known-hosts.damaged-2.json",
            "multiplayer/known-hosts.damaged-3.json"
        };

        /// <summary>How many servers reported unchecked are remembered, the oldest forgotten first.</summary>
        private const int MaximumUncheckedServers = 64;

        private readonly INetworkDataFiles files;
        private readonly object gate = new object();

        /// <summary>
        /// The servers checks reported <see cref="KnownHostTrust.Unchecked"/> while the record could not be read, as a typed
        /// target's key and the server id, oldest first. A trust the player gave for one of them was given without the record.
        /// </summary>
        private readonly List<string> uncheckedServers = new List<string>();
        private Task<bool>? reading;
        private bool read;
        private bool dirty;
        private byte[]? damaged;

        internal KnownHostsStore(INetworkDataFiles files)
        {
            this.files = files ?? throw new ArgumentNullException(nameof(files));
        }

        /// <summary>Gets the record this process keeps. Every use locks it.</summary>
        internal KnownHosts Record { get; } = new KnownHosts();

        /// <summary>Gets whether the record holds what the file held: once a read succeeded, or found no file.</summary>
        internal bool IsRead
        {
            get
            {
                lock (gate) return read;
            }
        }

        /// <summary>
        /// Reads the file unless a read already succeeded; true once the record holds what the file held. A read that fails
        /// leaves the record unread, and the next call reads again. One read runs at a time, and a caller that comes while
        /// one runs shares it.
        /// </summary>
        internal Task<bool> EnsureReadAsync()
        {
            lock (gate)
            {
                if (read) return Task.FromResult(true);
                if (reading == null || reading.IsCompleted) reading = ReadAsync();
                return reading;
            }
        }

        /// <summary>
        /// Checks that a typed <paramref name="host"/> and <paramref name="port"/> serve <paramref name="serverId"/>, and
        /// records a first server there. <paramref name="trustedServerId"/> is the one server the player trusted for this
        /// join, if any; a trust given after the server was reported unchecked counts only while the record cannot be read.
        /// </summary>
        internal async Task<KnownHostTrust> TrustAsync(string host, int port, string serverId, string? trustedServerId, long unixSeconds)
        {
            var trusted = string.Equals(serverId, trustedServerId, StringComparison.Ordinal);
            var reported = KnownHosts.TargetKey(host, port) + ":" + serverId;
            if (!await EnsureReadAsync().ConfigureAwait(false))
            {
                // Nothing can be checked, and nothing is recorded: only the player's own trust in this very server goes on.
                if (trusted) return KnownHostTrust.Trusted;
                lock (gate)
                {
                    if (!uncheckedServers.Contains(reported)) uncheckedServers.Add(reported);
                    if (uncheckedServers.Count > MaximumUncheckedServers) uncheckedServers.RemoveAt(0);
                }

                return KnownHostTrust.Unchecked;
            }

            // A trust given for a server that could not be checked was given without the record. Now that it reads, an
            // address that served another server asks again, in the changed server's words, before anything is recorded.
            bool trustedUnchecked;
            lock (gate) trustedUnchecked = uncheckedServers.Remove(reported) && trusted;
            lock (Record)
            {
                var check = Record.Check(host, port, serverId);
                if (check == KnownHostCheck.Match) return KnownHostTrust.Trusted;
                if (check == KnownHostCheck.Mismatch && (!trusted || trustedUnchecked)) return KnownHostTrust.Changed;
                Record.Remember(host, port, serverId, unixSeconds);
            }

            MarkChanged();
            return KnownHostTrust.Trusted;
        }

        /// <summary>Marks the record for saving after a change, from any thread.</summary>
        internal void MarkChanged()
        {
            lock (gate) dirty = true;
        }

        /// <summary>
        /// Queues a save behind <paramref name="previous"/> when the record changed since the last one, and returns it, or
        /// returns <paramref name="previous"/> when there is nothing to save. Nothing is saved until a read succeeded, so a
        /// file this process could not read is never written over. A damaged file's copy is written first, and the record
        /// only once the copy landed.
        /// </summary>
        internal Task SaveAfter(Task previous)
        {
            if (previous == null) throw new ArgumentNullException(nameof(previous));
            lock (gate)
            {
                if (!dirty || !read) return previous;
                dirty = false;
            }

            byte[] bytes;
            lock (Record) bytes = Record.Serialize();
            return Queue(previous, bytes);
        }

        /// <summary>
        /// Chains the writes behind <paramref name="previous"/>. It is a method of its own so that the frame that has
        /// nothing to save allocates nothing: a lambda in <see cref="SaveAfter"/> would capture its locals on every call.
        /// Saves are never cancelled, even while closing: a cancelled write would lose the change, and each is small.
        /// </summary>
        private Task Queue(Task previous, byte[] bytes) =>
            previous.ContinueWith(_ => WriteAsync(bytes), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();

        private async Task<bool> ReadAsync()
        {
            byte[]? bytes;
            try
            {
                bytes = await files.ReadAsync(KnownHosts.FilePath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Held, failing or refused: the record stays unread, so nothing is checked against it or saved over it.
                return false;
            }

            var parsed = KnownHosts.Parse(bytes);
            var intact = bytes == null || SameBytes(parsed.Serialize(), bytes);
            lock (Record) Record.MergeFrom(parsed);
            lock (gate)
            {
                read = true;
                if (!intact) damaged = bytes;
            }

            return true;
        }

        /// <summary>
        /// Writes the record, after the damaged file's copy while one is still due. The damaged bytes are taken when the write
        /// runs, not when it was queued, and let go only once their copy landed: a save queued behind one whose copy failed
        /// tries the copy again first, so the canonical rewrite never replaces the damaged file without it.
        /// </summary>
        private async Task WriteAsync(byte[] bytes)
        {
            byte[]? copy;
            lock (gate) copy = damaged;
            if (copy != null)
            {
                // A copy that cannot be written throws here, before the record: the damaged file stays where it is.
                await KeepCopyAsync(copy).ConfigureAwait(false);
                lock (gate)
                {
                    if (ReferenceEquals(damaged, copy)) damaged = null;
                }
            }

            await files.WriteAsync(KnownHosts.FilePath, bytes, CancellationToken.None).ConfigureAwait(false);
        }

        /// <summary>
        /// Writes <paramref name="copy"/> to the first of <see cref="DamagedPaths"/> that holds no file, and throws when that
        /// write fails. A place that holds a file, or cannot be read just now, holds a copy already, so it is never written.
        /// </summary>
        private async Task KeepCopyAsync(byte[] copy)
        {
            foreach (var path in DamagedPaths)
            {
                byte[]? kept;
                try
                {
                    kept = await files.ReadAsync(path, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    continue;
                }

                if (kept != null) continue;
                await files.WriteAsync(path, copy, CancellationToken.None).ConfigureAwait(false);
                return;
            }
        }

        private static bool SameBytes(byte[] left, byte[] right)
        {
            if (left.Length != right.Length) return false;
            for (var index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index]) return false;
            }

            return true;
        }
    }
}
