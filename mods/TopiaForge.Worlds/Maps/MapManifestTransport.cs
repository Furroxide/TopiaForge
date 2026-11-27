using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Worlds.Maps
{
    internal interface IMapManifestBackend
    {
        IMapTransport PublishedTransport(IMapTransport transport);
    }

    // A bounded manifest read, followed by the existing project request; never a native downloader.
    internal sealed class MapManifestTransport : IMapTransport
    {
        private const int MaximumManifestBytes = 64 * 1024;
        private readonly IMapTransport inner;
        private readonly MapEndpointPolicy endpoints;
        private readonly Func<Uri, string, Uri> resolve;
        private readonly SynchronizationContext context;
        internal MapManifestTransport(IMapTransport inner, MapEndpointPolicy endpoints, Func<Uri, string, Uri> resolve, SynchronizationContext context)
        { this.inner = inner; this.endpoints = endpoints; this.resolve = resolve; this.context = context; }
        public async Task<MapResponse> FetchAsync(Uri uri, MapCacheRecord? cached, CancellationToken token)
        {
            endpoints.Require(uri);
            token.ThrowIfCancellationRequested();
            var manifest = await inner.FetchAsync(uri, null, token).ConfigureAwait(false);
            if (manifest.Status == 304)
            { manifest.Dispose(); throw InvalidManifest(); }
            if (manifest.Status != 200) return manifest; // Existing store handles deletion/errors/redirect refusal.
            string json;
            try
            {
                if (manifest.Length > MaximumManifestBytes || manifest.Length <= 0) throw InvalidManifest();
                using var bytes = new MemoryStream();
                var buffer = new byte[4096];
                int count;
                while ((count = await manifest.Content.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) != 0)
                {
                    token.ThrowIfCancellationRequested();
                    if (bytes.Length + count > MaximumManifestBytes) throw InvalidManifest();
                    bytes.Write(buffer, 0, count);
                }
                if (bytes.Length == 0 || (manifest.Length.HasValue && manifest.Length.Value != bytes.Length)) throw InvalidManifest();
                json = new UTF8Encoding(false, true).GetString(bytes.ToArray());
            }
            catch (DecoderFallbackException) { throw InvalidManifest(); }
            catch (IOException) { throw new HttpRequestException(); }
            finally { manifest.Dispose(); }
            var completion = new TaskCompletionSource<Uri>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = token.Register(() => completion.TrySetCanceled());
            context.Post(_ =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var project = resolve(uri, json);
                    endpoints.Require(project);
                    token.ThrowIfCancellationRequested();
                    completion.TrySetResult(project);
                }
                catch (OperationCanceledException) { completion.TrySetCanceled(); }
                catch (Exception) { completion.TrySetException(InvalidManifest()); }
            }, null);
            var selected = await completion.Task.ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            // The previous project validators apply only to the project, never the manifest.
            return await inner.FetchAsync(selected, cached, token).ConfigureAwait(false);
        }
        private static FormatException InvalidManifest() => new FormatException("The map manifest is unsupported or unsafe. Update TopiaForge or try another map.");
    }
}
