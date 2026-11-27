using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Multiplayer.Network
{
    internal sealed partial class DescriptorEndpoint
    {
        private void BeginArchive(Client client, string sha256)
        {
            if (packages == null)
            {
                client.Respond("TFD1 404\n", null);
                return;
            }

            // Timed-out clients can leave a disk read finishing. They keep their worker slot until it really ends,
            // so cycling connections never queues unlimited hashing work, even on a stalled disk.
            if (Volatile.Read(ref pendingArchives) >= MaximumConnections)
            {
                client.Respond("TFD1 503\n", null);
                return;
            }

            Interlocked.Increment(ref pendingArchives);
            client.Archive = new PendingArchive(token =>
            {
                try { return packages(sha256, token); }
                finally { Interlocked.Decrement(ref pendingArchives); }
            });
        }

        private static bool FinishArchive(Client client)
        {
            var pending = client.Archive!;
            if (!pending.Work.IsCompleted) return false;
            client.Archive = null;
            Stream? stream = null;
            try
            {
                stream = pending.Take();
                if (stream == null) client.Respond("TFD1 404\n", null);
                else client.Respond("TFD1 200 " + stream.Length.ToString(CultureInfo.InvariantCulture) + "\n", stream);
            }
            catch (Exception exception) when (IsArchiveFailure(exception))
            {
                stream?.Dispose();
                client.Respond("TFD1 404\n", null);
            }

            return true;
        }

        private sealed class PendingArchive : IDisposable
        {
            private readonly CancellationTokenSource cancellation = new CancellationTokenSource();

            internal PendingArchive(Func<CancellationToken, Stream?> open)
            {
                // Do not pass the token to Task.Run: the callback must run its finally even if already cancelled.
                Work = Task.Run(() => open(cancellation.Token));
            }

            internal Task<Stream?> Work { get; }

            internal Stream? Take()
            {
                if (!Work.IsCompleted) throw new InvalidOperationException("The archive is still being verified.");
                cancellation.Dispose();
                if (Work.Status == TaskStatus.RanToCompletion) return Work.Result;
                _ = Work.Exception; // Observe it, without retaining or logging paths from a disk exception.
                return null;
            }

            public void Dispose()
            {
                cancellation.Cancel();
                _ = Work.ContinueWith(completed =>
                {
                    if (completed.Status == TaskStatus.RanToCompletion) completed.Result?.Dispose();
                    else _ = completed.Exception;
                    cancellation.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
    }
}
