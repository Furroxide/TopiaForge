using System;
using TopiaForge.Mods;

namespace TopiaForge.CreatorContent
{
    internal sealed partial class CreatorContentService
    {
        private sealed class OwnerFacade : ICreatorContentService, ICreatorSceneAdapterRegistry
        {
            private readonly CreatorContentService service;
            private readonly string ownerId;
            private readonly IModLifetime lifetime;

            public OwnerFacade(CreatorContentService service, string ownerId, IModLifetime lifetime)
            {
                this.service = service;
                this.ownerId = ownerId;
                this.lifetime = lifetime;
            }

            public CreatorCatalogSnapshot Catalog => service.Catalog;
            public OperationResult<CreatorCatalogSnapshot> RefreshCatalog() => service.RefreshCatalog();

            public OperationResult<ICreatorContentRegistration> Register(CreatorContentRegistrationRequest request)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<ICreatorContentRegistration>.Failure(ModErrorCode.Cancelled, "The source mod is stopping.");
                }
                var result = service.Register(ownerId, lifetime, request);
                return Track(result, "The source mod stopped before content registration completed.");
            }

            public OperationResult<ICreatorSession> BeginSession(CreatorSessionOptions options)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<ICreatorSession>.Failure(ModErrorCode.Cancelled, "The consumer mod is stopping.");
                }
                var result = service.BeginSession(ownerId, lifetime, options);
                return Track(result, "The consumer mod stopped before its creator session began.");
            }

            public OperationResult<ICreatorSceneAdapterRegistration> RegisterSceneAdapter(
                CreatorSceneAdapterRegistrationRequest request)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<ICreatorSceneAdapterRegistration>.Failure(
                        ModErrorCode.Cancelled,
                        "The scene-adapter source mod is stopping.");
                }

                var result = service.RegisterSceneAdapter(ownerId, lifetime, request);
                return Track(result, "The source mod stopped before scene-adapter registration completed.");
            }

            private OperationResult<T> Track<T>(OperationResult<T> result, string cancelledMessage)
                where T : class, IDisposable
            {
                if (!result.TryGetValue(out var value)) return result;
                try
                {
                    lifetime.Track(value);
                    return result;
                }
                catch (ObjectDisposedException)
                {
                    value.Dispose();
                    return OperationResult<T>.Failure(ModErrorCode.Cancelled, cancelledMessage);
                }
            }
        }
    }
}
