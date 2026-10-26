using System;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Internal
{
    // First-party seam only: authored map worlds require no public loading API.
    internal interface IInternalMapWorldService
    {
        Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context, InternalMapWorldRequest request, CancellationToken token);
    }
    internal interface IInternalMapStorageContext
    {
        string MapCacheDirectory { get; }
    }
    internal sealed class InternalMapWorldRequest
    {
        internal InternalMapWorldRequest(string? publishedId, string? sceneId,
            Func<CancellationToken, Task<OperationResult<byte[]>>>? readFile)
        { PublishedId = publishedId; SceneId = sceneId; ReadFile = readFile; }
        internal string? PublishedId { get; }
        internal string? SceneId { get; }
        internal Func<CancellationToken, Task<OperationResult<byte[]>>>? ReadFile { get; }
    }
}
