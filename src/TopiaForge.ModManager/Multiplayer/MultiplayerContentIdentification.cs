using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// One identification of the packages that had no hash when it started, run by a <see cref="IMultiplayerWorkScheduler"/>
    /// away from the frame. It reads each package's install receipt, lists the files of those without one, and hashes them,
    /// counting packages and bytes as it goes. It stops at the first package it cannot read, keeping the hashes it found
    /// before, so the next identification starts from there. The inventory adopts the results on the main thread once
    /// <see cref="IsCompleted"/> says so, and drops a run whose packages changed meanwhile (<see cref="Cancel"/>).
    /// </summary>
    internal sealed class MultiplayerContentIdentification
    {
        private readonly string[] paths;
        private readonly string[] names;
        private readonly string?[] hashes;
        private int packagesDone;
        private long bytesDone;
        private long bytesTotal;
        private int cancelled;
        private volatile bool completed;
        private IOException? failure;

        /// <param name="generation">The inventory's package generation this run describes.</param>
        /// <param name="paths">The package folders to identify, in order.</param>
        /// <param name="names">Each package's name and version, as a failure names it, never its folder.</param>
        internal MultiplayerContentIdentification(int generation, IReadOnlyList<string> paths, IReadOnlyList<string> names)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            if (names == null) throw new ArgumentNullException(nameof(names));
            if (paths.Count != names.Count) throw new ArgumentException("Every package has a name.", nameof(names));
            Generation = generation;
            this.paths = new string[paths.Count];
            this.names = new string[names.Count];
            for (var index = 0; index < this.paths.Length; index++)
            {
                this.paths[index] = paths[index] ?? throw new ArgumentException("A package path is missing.", nameof(paths));
                this.names[index] = names[index] ?? throw new ArgumentException("A package name is missing.", nameof(names));
            }

            hashes = new string?[this.paths.Length];
        }

        /// <summary>Gets the package generation this run describes.</summary>
        internal int Generation { get; }

        /// <summary>Gets how many packages it covers.</summary>
        internal int Count => paths.Length;

        /// <summary>Gets whether the run finished, with every hash or with a failure. Its results are safe to read after.</summary>
        internal bool IsCompleted => completed;

        /// <summary>Gets whether <see cref="Cancel"/> was called.</summary>
        internal bool IsCancelled => Volatile.Read(ref cancelled) != 0;

        /// <summary>Gets the progress so far, as pending readiness. Any thread; it allocates nothing.</summary>
        internal InternalContentReadiness Progress =>
            new InternalContentReadiness(
                InternalContentState.Pending,
                Math.Min(Volatile.Read(ref packagesDone), paths.Length),
                paths.Length,
                Interlocked.Read(ref bytesDone),
                Interlocked.Read(ref bytesTotal));

        /// <summary>Gets why the run stopped: an exception naming the package and never its folder; null after success.</summary>
        internal IOException? Failure => completed ? failure : null;

        /// <summary>Gets the hash found for the package at <paramref name="index"/>, or null when the run did not reach it.</summary>
        internal string? HashOf(int index) => completed ? hashes[index] : null;

        /// <summary>Asks the run to stop as soon as it can; a cancelled run's results are never adopted.</summary>
        internal void Cancel() => Interlocked.Exchange(ref cancelled, 1);

        /// <summary>Does the work. It runs once, on whichever thread the scheduler chose, and never throws.</summary>
        internal void Run()
        {
            try
            {
                var payloads = new List<int>();
                for (var index = 0; index < paths.Length && !IsCancelled; index++)
                {
                    var receipted = ReadReceipt(index);
                    if (receipted != null)
                    {
                        hashes[index] = receipted;
                        Interlocked.Increment(ref packagesDone);
                    }
                    else
                    {
                        payloads.Add(index);
                    }
                }

                // List every file first, so progress counts bytes against a known total.
                var files = new List<IReadOnlyList<MultiplayerContentInventory.PayloadFile>>(payloads.Count);
                foreach (var index in payloads)
                {
                    if (IsCancelled) return;
                    if (!TryList(index, out var listed)) return;
                    files.Add(listed);
                    foreach (var file in listed) Interlocked.Add(ref bytesTotal, file.Length);
                }

                for (var item = 0; item < payloads.Count; item++)
                {
                    var index = payloads[item];
                    if (IsCancelled) return;
                    try
                    {
                        hashes[index] = MultiplayerContentInventory.PayloadSha256(paths[index], files[item], count => Interlocked.Add(ref bytesDone, count), () => IsCancelled);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
                    {
                        failure = Named(index, exception);
                        return;
                    }

                    Interlocked.Increment(ref packagesDone);
                }
            }
            catch (Exception exception)
            {
                // Whatever else went wrong, the content is reported as unreadable rather than left pending for ever.
                failure ??= new IOException("This game's mods could not be identified for multiplayer (" + exception.GetType().Name + ").", exception);
            }
            finally
            {
                completed = true;
            }
        }

        private string? ReadReceipt(int index)
        {
            try
            {
                return MultiplayerContentInventory.ReceiptSha256(paths[index]);
            }
            catch (Exception)
            {
                // An unreadable receipt falls back to the files themselves, as it always did.
                return null;
            }
        }

        private bool TryList(int index, out IReadOnlyList<MultiplayerContentInventory.PayloadFile> listed)
        {
            try
            {
                listed = MultiplayerContentInventory.ListPayloadFiles(paths[index]);
                return true;
            }
            catch (Exception exception) when (exception is IOException || exception is UnauthorizedAccessException)
            {
                failure = Named(index, exception);
                listed = Array.Empty<MultiplayerContentInventory.PayloadFile>();
                return false;
            }
        }

        private IOException Named(int index, Exception exception) =>
            new IOException(names[index] + " could not be read to identify it for multiplayer (" + exception.GetType().Name + ").", exception);
    }
}
