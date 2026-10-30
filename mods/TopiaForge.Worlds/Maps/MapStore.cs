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
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(previous == null ? 30 : 5));
            try
            {
                using var response = await requestTransport.FetchAsync(uri, cached?.Record, deadline.Token).ConfigureAwait(false);
                deadline.Token.ThrowIfCancellationRequested();
                if (response.Status == 304 && previous != null) return Success(previous, false, cached!.Record.SavedUtc);
                if (response.Status == 404 || response.Status == 410)
                {
                    try { cache.Remove(id); }
                    catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is FormatException) { }
                    return Failure(ModErrorCode.NotFound, "The map is no longer published. Ask the mod's author for an update.");
                }
                if (response.Status >= 300 && response.Status < 400)
                    return Failure(ModErrorCode.InvalidArgument, "The map service redirected the download. Update TopiaForge or try again later.");
                if (response.Status >= 500) throw new HttpRequestException();
                if (response.Status != 200)
                    return Failure(ModErrorCode.Unavailable, "The public map could not be downloaded. Try again later.");
                if (response.Length > MapDocument.MaximumPackedBytes || response.Length <= 0)
                    return Failure(ModErrorCode.InvalidArgument, "The map exceeds the 8 MiB download limit or is empty. Choose another map.");
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
                cache.Put(id, document, response.Etag, response.Modified);
                token.ThrowIfCancellationRequested();
                return Success(document, false, string.Empty);
            }
            catch (OperationCanceledException)
            {
                if (token.IsCancellationRequested) return Failure(ModErrorCode.Cancelled, "Map loading was cancelled.");
                return previous != null ? Success(previous, true, cached!.Record.SavedUtc)
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

        private static OperationResult<MapAcquisition> Success(MapDocument document, bool offline, string savedUtc) =>
            OperationResult<MapAcquisition>.Success(new MapAcquisition(document, offline, savedUtc));
        private static OperationResult<MapAcquisition> Failure(ModErrorCode code, string message) => OperationResult<MapAcquisition>.Failure(code, message);
    }
    internal sealed class MapAcquisition
    {
        internal MapAcquisition(MapDocument document, bool offline, string savedUtc) { Document = document; Offline = offline; SavedUtc = savedUtc; }
        internal MapDocument Document { get; }
        internal bool Offline { get; }
        internal string SavedUtc { get; }
    }
}
