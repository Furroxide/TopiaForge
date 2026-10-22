using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.Network
{
    /// <summary>
    /// A host's bans as live sessions keep them: the <see cref="BanList"/> and its file, <c>multiplayer/bans.json</c>, read
    /// with the identity and again by hosting while unread, and saved as bans change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file that cannot be read just now, as when another program holds it or the disk fails, is never taken for an
    /// empty list and never written over: a host that took it for one would admit every player it banned, and its next ban
    /// would save a list holding that ban alone. Until a read succeeds the list is unread: hosting does not start, so
    /// nobody is ever admitted unchecked, and nothing is saved; hosting again reads the file again, so it recovers once the
    /// file reads. A ban made while the list is unread is held: it counts at once, and once the file reads it joins the
    /// file's bans, as newer than them, and is saved with them.
    /// </para>
    /// <para>
    /// A file that reads but is damaged is salvaged: the bans that read are kept, and the rest are dropped. Before anything
    /// replaces it, its bytes are copied to the first of <see cref="DamagedPaths"/> that holds no file, so a list the game
    /// could not use is never lost silently, and while that copy cannot be written the damaged file stays where it is, to
    /// be salvaged again. A copy is never written over. Anything that does not read back exactly as this game writes it
    /// counts as damaged: a file of another schema, a ban that does not read, a name that is not display text, a banned key
    /// listed twice, more bans than the list keeps, or bytes that are not canonical.
    /// </para>
    /// </remarks>
    internal sealed class BanStore
    {
        /// <summary>
        /// Where a damaged file's bytes are kept, in order. The provider's file service names no path and lists nothing, so
        /// the copies are a fixed set: each damage takes the first place that holds no file, and a place that holds one, or
        /// cannot be read, is never written. Once all three hold copies, a damaged file is salvaged without one.
        /// </summary>
        internal static readonly IReadOnlyList<string> DamagedPaths = new[]
        {
            "multiplayer/bans.damaged.json",
            "multiplayer/bans.damaged-2.json",
            "multiplayer/bans.damaged-3.json"
        };

        private readonly INetworkDataFiles files;
        private readonly object gate = new object();
        private Task<bool>? reading;
        private bool read;
        private bool dirty;
        private byte[]? damaged;

        internal BanStore(INetworkDataFiles files)
        {
            this.files = files ?? throw new ArgumentNullException(nameof(files));
        }

        /// <summary>Gets the bans this host keeps: once the file was read, its bans and every one since; before, those held.</summary>
        internal BanList Record { get; } = new BanList();

        /// <summary>Gets whether the list holds what the file held: once a read succeeded, or found no file.</summary>
        internal bool IsRead
        {
            get
            {
                lock (gate) return read;
            }
        }

        /// <summary>
        /// Reads the file unless a read already succeeded; true once the list holds what the file held. A read that fails
        /// leaves the list unread, and the next call reads again. One read runs at a time, and a caller that comes while one
        /// runs shares it.
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

        /// <summary>Marks the list for saving after a change, from any thread; a change made while it is unread waits for the read.</summary>
        internal void MarkChanged()
        {
            lock (gate) dirty = true;
        }

        /// <summary>
        /// Queues a save behind <paramref name="previous"/> when the list changed since the last one, and returns it, or
        /// returns <paramref name="previous"/> when there is nothing to save. Nothing is saved until a read succeeded, so a
        /// file this process could not read is never written over, and a change made meanwhile is saved once it reads. A
        /// damaged file's copy is written first, and the list only once the copy landed.
        /// </summary>
        internal Task SaveAfter(Task previous)
        {
            if (previous == null) throw new ArgumentNullException(nameof(previous));
            lock (gate)
            {
                if (!dirty || !read) return previous;
                dirty = false;
            }

            return Queue(previous, Record.Serialize());
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
                bytes = await files.ReadAsync(BanList.FilePath, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Held, failing or refused: the list stays unread, so nobody is admitted against it and nothing is saved over it.
                return false;
            }

            var parsed = BanList.Parse(bytes);
            var intact = bytes == null || SameBytes(parsed.Serialize(), bytes);
            Record.MergeFrom(parsed);
            lock (gate)
            {
                read = true;
                if (!intact) damaged = bytes;
            }

            return true;
        }

        /// <summary>
        /// Writes the list, after the damaged file's copy while one is still due. The damaged bytes are taken when the write
        /// runs, not when it was queued, and let go only once their copy landed: a save queued behind one whose copy failed
        /// tries the copy again first, so the list never replaces the damaged file without it.
        /// </summary>
        private async Task WriteAsync(byte[] bytes)
        {
            byte[]? copy;
            lock (gate) copy = damaged;
            if (copy != null)
            {
                // A copy that cannot be written throws here, before the list: the damaged file stays where it is.
                await KeepCopyAsync(copy).ConfigureAwait(false);
                lock (gate)
                {
                    if (ReferenceEquals(damaged, copy)) damaged = null;
                }
            }

            await files.WriteAsync(BanList.FilePath, bytes, CancellationToken.None).ConfigureAwait(false);
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
