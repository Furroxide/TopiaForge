using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.Worlds.Maps
{
    internal sealed partial class MapStore
    {
        private readonly MapCache cache;
        private readonly MapEndpointPolicy endpoints;
        private readonly IMapTransport transport;
        private readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);
        internal MapStore(MapCache cache, MapEndpointPolicy endpoints, IMapTransport transport)
        { this.cache = cache; this.endpoints = endpoints; this.transport = transport; }

        internal async Task<OperationResult<MapAcquisition>> GetAsync(string id, Uri uri, CancellationToken token, IProgress<long>? progress = null, IMapTransport? requestTransport = null)
        {
            if (!MapDocument.IsId(id, 64)) return Failure(ModErrorCode.InvalidArgument, "Choose a valid published map id.");
            try { endpoints.Require(uri); }
            catch (FormatException error) { return Failure(ModErrorCode.InvalidArgument, error.Message); }
            try { await gate.WaitAsync(token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return Failure(ModErrorCode.Cancelled, "Map loading was cancelled."); }
            try { return await LoadAsync(id, uri, token, progress, requestTransport ?? transport).ConfigureAwait(false); }
            finally { gate.Release(); }
        }

        private async Task<OperationResult<MapAcquisition>> LoadAsync(string id, Uri uri, CancellationToken token, IProgress<long>? progress, IMapTransport requestTransport)
        {
            var cached = cache.Read(id);
            MapDocument? previous = null;
            if (cached != null)
            {
                try { previous = new MapDocument(cached.Bytes); }
                catch (Exception error) when (error is FormatException || error is IOException || error is ArgumentException)
                { cached = null; }
            }
            // First play retains its existing 30-second total budget. A cached revalidation has a 5-second
            // header budget; a changed response then receives the existing 30-second download budget.
            using var firstDownload = CancellationTokenSource.CreateLinkedTokenSource(token);
            if (previous == null) firstDownload.CancelAfter(TimeSpan.FromSeconds(30));
            using var headers = CancellationTokenSource.CreateLinkedTokenSource(firstDownload.Token);
            headers.CancelAfter(TimeSpan.FromSeconds(previous == null ? 30 : 5));
            try
            {
                using var response = await requestTransport.FetchAsync(uri, cached?.Record, headers.Token).ConfigureAwait(false);
                headers.Token.ThrowIfCancellationRequested();
                headers.CancelAfter(Timeout.InfiniteTimeSpan);
                if (response.Status == 304 && previous != null) return Success(previous, false, cached!.Record.SavedUtc);
                if (response.Status == 404 || response.Status == 410)
                {
                    try { cache.Remove(id); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException) { }
                    return Failure(ModErrorCode.NotFound, "The map is no longer published. Ask the mod's author for an update.");
                }
                if (response.Status >= 300 && response.Status < 400)
                    return Failure(ModErrorCode.InvalidArgument, "The map service redirected the download. Update TopiaForge or try again later.");
                // Only transient service/network statuses may replay a verified cache. Permission refusals stay fail-closed.
                if (response.Status == 408 || response.Status == 429 || response.Status >= 500 && response.Status <= 599)
                    return previous != null ? Success(previous, true, cached!.Record.SavedUtc, reason: MapOfflineReason.ServiceBusy)
                        : Failure(ModErrorCode.Unavailable, "The public map service is temporarily unavailable. Try again later.");
                if (response.Status != 200)
                    return Failure(ModErrorCode.Unavailable, "The public map service refused this download. Check that the map is public or ask its author for an update.");
                if (response.Length > MapDocument.MaximumPackedBytes || response.Length <= 0)
                    return Failure(ModErrorCode.InvalidArgument, "The map exceeds the 8 MiB download limit or is empty. Choose another map.");
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(firstDownload.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(30));
                using var output = new MemoryStream();
                var buffer = new byte[16384];
                int count;
                while ((count = await ReadNetworkAsync(response.Content, buffer, deadline.Token).ConfigureAwait(false)) != 0)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (output.Length + count > MapDocument.MaximumPackedBytes)
                        return Failure(ModErrorCode.InvalidArgument, "The map exceeds the 8 MiB download limit. Choose another map.");
                    output.Write(buffer, 0, count);
                    progress?.Report(output.Length);
                }
                deadline.Token.ThrowIfCancellationRequested();
                if (response.Length.HasValue && response.Length.Value != output.Length)
                    return Failure(ModErrorCode.External, "The map download was incomplete. Check the connection and try again.");
                var document = new MapDocument(output.ToArray());
                token.ThrowIfCancellationRequested();
                var saved = TrySave(() => cache.Put(id, document, response.Etag, response.Modified));
                token.ThrowIfCancellationRequested();
                return Success(document, false, string.Empty, cacheWriteFailed: !saved);
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) return Failure(ModErrorCode.Cancelled, "Map loading was cancelled.");
                return previous != null ? Success(previous, true, cached!.Record.SavedUtc, reason: MapOfflineReason.Slow)
                    : Failure(ModErrorCode.TimedOut, "Downloading the map stopped. Check the connection and try again.");
            }
            catch (HttpRequestException)
            {
                return previous != null ? Success(previous, true, cached!.Record.SavedUtc)
                    : Failure(ModErrorCode.Unavailable, "The public map service could not be reached. Connect to the internet once; after that this map plays offline.");
            }
            catch (FormatException error) { return Failure(ModErrorCode.InvalidArgument, error.Message); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException)
            { return Failure(ModErrorCode.External, "The map could not be read or saved. Check available disk space and try again."); }
        }

        private static async Task<int> ReadNetworkAsync(Stream input, byte[] buffer, CancellationToken token)
        {
            try { return await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false); }
            catch (IOException) { throw new HttpRequestException(); }
        }

        private static bool TrySave(Action save)
        {
            try { save(); return true; }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException)
            { return false; }
        }

        private static OperationResult<MapAcquisition> Success(MapDocument document, bool offline, string savedUtc,
            bool cacheWriteFailed = false, MapOfflineReason reason = MapOfflineReason.Unreachable) =>
            OperationResult<MapAcquisition>.Success(new MapAcquisition(document, offline, savedUtc, cacheWriteFailed, reason));
        private static OperationResult<MapAcquisition> Failure(ModErrorCode code, string message) => OperationResult<MapAcquisition>.Failure(code, message);
    }
    internal enum MapOfflineReason { Unreachable, Slow, ServiceBusy }

    internal sealed class MapAcquisition
    {
        internal MapAcquisition(MapDocument document, bool offline, string savedUtc, bool cacheWriteFailed, MapOfflineReason reason)
        { Document = document; Offline = offline; SavedUtc = savedUtc; CacheWriteFailed = cacheWriteFailed; OfflineReason = reason; }
        internal MapDocument Document { get; }
        internal bool Offline { get; }
        internal string SavedUtc { get; }
        internal bool CacheWriteFailed { get; }
        internal MapOfflineReason OfflineReason { get; }
    }
}
