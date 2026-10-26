using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Multiplayer.Network;

namespace TopiaForge.Multiplayer
{
    /// <summary>A private, run-bound port receipt; it carries no address and is never part of the SDK snapshot.</summary>
    internal sealed class DeveloperPeerRendezvous : IDisposable
    {
        internal const string FileName = "dev-session.json";
        private readonly LiveSessions live;
        private readonly INetworkDataFiles files;
        private readonly string runId;
        private readonly IModLogger logger;
        private readonly CancellationTokenSource stopping = new CancellationTokenSource();
        private Task? writing;
        private LiveHost? published;
        private DateTime nextAttempt;
        private bool disposed;

        internal DeveloperPeerRendezvous(LiveSessions live, INetworkDataFiles files, string runId, IModLogger logger)
        { this.live = live; this.files = files; this.runId = runId; this.logger = logger; }

        internal static string? ReadRunId()
        {
            var value = Environment.GetEnvironmentVariable("TOPIAFORGE_DEV_PEERS");
            if (value == null || value.Length != 32) return null;
            foreach (var character in value)
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'))) return null;
            return value;
        }

        internal void Pump()
        {
            if (disposed || (writing != null && !writing.IsCompleted)) return;
            if (writing != null)
            {
                if (writing.IsFaulted || writing.IsCanceled)
                {
                    _ = writing.Exception;
                    published = null;
                    nextAttempt = DateTime.UtcNow.AddSeconds(1);
                    logger.Warn("The development host receipt could not be written. Check the private data directory permissions.");
                }
                writing = null;
            }
            var host = live.Host;
            if (host == null || host.Descriptor == null || ReferenceEquals(host, published) || DateTime.UtcNow < nextAttempt) return;
            var bytes = CanonicalJson.Serialize(new JsonObjectNode().Add("schemaVersion", 1).Add("runId", runId)
                .Add("port", host.Port).Add("serverId", host.Descriptor.Descriptor.ServerId));
            published = host;
            try { writing = files.WriteAsync(FileName, bytes, stopping.Token); }
            catch (Exception exception) { writing = Task.FromException(exception); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            stopping.Cancel();
            if (writing != null) _ = writing.ContinueWith(task => { _ = task.Exception; }, TaskScheduler.Default);
            stopping.Dispose();
        }
    }
}
