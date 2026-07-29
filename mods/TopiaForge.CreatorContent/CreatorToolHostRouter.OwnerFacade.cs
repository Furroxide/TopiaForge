using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.CreatorContent
{
    internal sealed partial class CreatorToolHostRouter
    {
        private sealed class OwnerFacade : ICreatorToolHostRouter
        {
            private readonly CreatorToolHostRouter router;
            private readonly string ownerId;
            private readonly IModLifetime lifetime;

            public OwnerFacade(
                CreatorToolHostRouter router,
                string ownerId,
                IModLifetime lifetime)
            {
                this.router = router;
                this.ownerId = ownerId;
                this.lifetime = lifetime;
            }

            public CreatorToolHostDescriptor? ActiveHost => router.ActiveHost;

            public OperationResult<ICreatorToolHostRegistration> RegisterHost(
                CreatorToolHostRegistrationRequest request)
            {
                if (lifetime.IsStopping)
                {
                    return OperationResult<ICreatorToolHostRegistration>.Failure(
                        ModErrorCode.Cancelled,
                        "The host mod is stopping.");
                }
                var result = router.RegisterHost(ownerId, lifetime, request);
                if (!result.TryGetValue(out var registration)) return result;
                try
                {
                    lifetime.Track(registration);
                    return result;
                }
                catch (ObjectDisposedException)
                {
                    registration.Dispose();
                    return OperationResult<ICreatorToolHostRegistration>.Failure(
                        ModErrorCode.Cancelled,
                        "The host mod stopped during registration.");
                }
            }

            public OperationResult<bool> Toggle() => lifetime.IsStopping
                ? OperationResult<bool>.Failure(
                    ModErrorCode.Cancelled,
                    "The host mod is stopping.")
                : router.Toggle();

            public OperationResult<bool> CloseActive(
                CreatorToolCloseReason reason = CreatorToolCloseReason.Requested) =>
                router.CloseActive(reason);
        }
    }
}
