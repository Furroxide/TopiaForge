using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds
{
    internal static class WorldProviderLoader
    {
        public static async Task<OperationResult<IWorldInstance>> LoadAsync(IWorldLoadContext context,
            NativeWorldLoadRequest request, CancellationToken cancellationToken,
            Func<IInternalWorldPreparation, WorldResourceScope, Task<OperationResult<WorldReadiness>>>? finish = null)
        {
            if (context == null) throw new ArgumentNullException(nameof(context));
            if (!(context.Context is IInternalWorldRuntimeContext native))
                return Failure(ModErrorCode.Unavailable, "The owning context does not provide native world readiness.");
            WorldResourceScope? resources = null;
            OperationResult<IWorldInstance> result;
            try
            {
                resources = WorldResourceScope.Create(context.Context.Lifetime, cancellationToken);
                resources.ThrowIfStopping();
                var prepared = await native.WorldRuntime.PrepareAsync(request, resources.StoppingToken);
                if (!prepared.TryGetValue(out var preparation)) result = Failure(prepared.ErrorCode, prepared.ErrorMessage);
                else
                {
                    resources.Add(preparation);
                    resources.ThrowIfStopping();
                    var readiness = finish == null
                        ? await preparation.FinishAsync(context.SpawnPolicy, null, null, resources.StoppingToken)
                        : await finish(preparation, resources);
                    resources.ThrowIfStopping();
                    if (readiness.TryGetValue(out var ready))
                        return OperationResult<IWorldInstance>.Success(new ScopedWorldInstance(ready, resources));
                    result = Failure(readiness.ErrorCode, readiness.ErrorMessage);
                }
            }
            catch (OperationCanceledException) { result = Failure(ModErrorCode.Cancelled, "World preparation was cancelled."); }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || context.Context.Lifetime.IsStopping)
            { result = Failure(ModErrorCode.Cancelled, "The world owner stopped during preparation."); }
            catch (Exception error) { result = Failure(ModErrorCode.External, "World preparation failed: " + error); }
            if (resources != null)
            {
                try { resources.Dispose(); }
                catch (Exception cleanup) { result = Failure(ModErrorCode.External, result.ErrorCode + ": " + result.ErrorMessage + " Cleanup failed: " + cleanup); }
            }
            return result;
        }
        private static OperationResult<IWorldInstance> Failure(ModErrorCode code, string message) =>
            OperationResult<IWorldInstance>.Failure(code, message);
    }
}
