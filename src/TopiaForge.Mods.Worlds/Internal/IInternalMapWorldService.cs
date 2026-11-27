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
        string PublishedMapLibraryPath { get; }
    }
    internal interface IInternalMapWorldInstance : IWorldInstance
    {
        InternalMapIdentity? Identity { get; }
        ReadOnlyMemory<byte> PackedMap { get; }
    }
    /// <summary>First-party plain-text notices; implementations disable rich-text parsing.</summary>
    internal interface IInternalMapNoticeUi
    {
        OperationResult<bool> ShowMapNotice(string text, UiTone tone);
    }

    internal sealed class InternalMapWorldRequest
    {
        internal InternalMapWorldRequest(string? publishedId, string? sceneId,
            Func<CancellationToken, Task<OperationResult<byte[]>>>? readFile, string? packagePath = null, string? displayName = null)
        { PublishedId = publishedId; SceneId = sceneId; ReadFile = readFile; PackagePath = packagePath; DisplayName = displayName; }
        internal string? PublishedId { get; }
        internal string? DisplayName { get; }
        internal string? PackagePath { get; }
        internal string? SceneId { get; }
        internal Func<CancellationToken, Task<OperationResult<byte[]>>>? ReadFile { get; }
    }
}
