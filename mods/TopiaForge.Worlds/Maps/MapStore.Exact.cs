using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds.Maps
{
    internal sealed partial class MapStore
    {
        internal async Task<OperationResult<MapAcquisition>> GetExactAsync(InternalMapIdentity expected, Uri uri,
            InternalMapCopy? hostCopy, CancellationToken token, IProgress<long>? progress = null)
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var cached = cache.ReadHash(expected.Sha256);
                if (cached != null)
                {
                    var document = ValidateExact(expected, cached);
                    token.ThrowIfCancellationRequested();
                    return Success(document, false, string.Empty);
                }
                var live = await GetAsync(expected.MapId, uri, token, progress).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (live.TryGetValue(out var current) && expected.Matches(current.Document.Packed))
                    return Success(ValidateExact(expected, current.Document.Packed), false, string.Empty);
                if (expected.Size > InternalMapIdentity.MaximumTransferBytes || hostCopy == null)
                    return Failure(ModErrorCode.Unavailable, "The host's map revision is unavailable or too large to send. Ask the host to restart the round.");
                var transferred = await hostCopy(token, progress).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (!transferred.TryGetValue(out var bytes)) return Failure(transferred.ErrorCode, transferred.ErrorMessage);
                // Hash and full document validation precede cache publication and all native parsing.
                var exact = ValidateExact(expected, bytes);
                token.ThrowIfCancellationRequested();
                cache.PutHash(exact);
                return Success(exact, false, string.Empty);
            }
            catch (OperationCanceledException) { return Failure(ModErrorCode.Cancelled, "Map loading was cancelled."); }
            catch (MapProblem error) { return Failure(error.Code, error.Message); }
            catch (FormatException error) { return Failure(ModErrorCode.InvalidArgument, error.Message); }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            { return Failure(ModErrorCode.External, "The map copy could not be read or saved."); }
        }

        private static MapDocument ValidateExact(InternalMapIdentity expected, byte[] bytes)
        {
            if (!expected.Matches(bytes)) throw new MapProblem(ModErrorCode.Conflict,
                "The map copy did not match the host's revision. Join again; if it repeats, ask the host to restart.");
            var document = new MapDocument(bytes);
            document.Select(expected.SceneId);
            if (document.Modified != expected.Modified) throw new MapProblem(ModErrorCode.Conflict, "The map revision metadata did not match.");
            return document;
        }
    }
}
