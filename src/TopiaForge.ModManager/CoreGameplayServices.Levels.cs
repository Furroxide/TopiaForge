using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal sealed partial class CoreGameplayServices : ISessionLevelLoader
    {
        /// <summary>
        /// Loads a level by its scene name as a Build Scenes world does: the game's own loader, given the scene's name, under
        /// the caller's transition grant, waiting for the scene, the player and its spawn. With a spawn, the player then stands
        /// there. Nothing of the load outlives the call: the level stays, as the game's own loads leave it.
        /// </summary>
        public async Task<OperationResult<bool>> LoadLevelAsync(IInternalSceneTransitionService transitions, string sceneName,
            TransformState? spawn, CancellationToken cancellationToken)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (transitions == null) throw new ArgumentNullException(nameof(transitions));
            if (string.IsNullOrWhiteSpace(sceneName)) throw new ArgumentException("A level's scene name is required.", nameof(sceneName));
            if (disposed) return OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The gameplay host has stopped.");
            var lifetime = new OwnerModLifetime();
            try
            {
                var service = new OwnerWorldRuntimeService(lifetime, transitions, worlds, new FrameClock(scheduler), dispatcher);
                var prepared = await service.PrepareAsync(NativeWorldLoadRequest.Scene(sceneName, WorldLoadTransition.SceneReplacement),
                    cancellationToken);
                if (!prepared.TryGetValue(out var preparation))
                    return OperationResult<bool>.Failure(prepared.ErrorCode, prepared.ErrorMessage);
                try
                {
                    if (!spawn.HasValue) return OperationResult<bool>.Success(true);
                    var ready = await preparation.FinishAsync(new WorldSpawnPolicy(WorldSpawnKind.ProviderDefault), null, spawn,
                        cancellationToken);
                    return ready.Succeeded ? OperationResult<bool>.Success(true)
                        : OperationResult<bool>.Failure(ready.ErrorCode, ready.ErrorMessage);
                }
                finally
                {
                    if (preparation is WorldPreparation owned) await owned.CloseAsync();
                    else preparation.Dispose();
                }
            }
            finally
            {
                lifetime.Dispose();
            }
        }

        /// <summary>The frames a level load waits through, from the game's own scheduler.</summary>
        private sealed class FrameClock : IWorldRuntimeClock
        {
            private readonly UnityScheduler scheduler;

            internal FrameClock(UnityScheduler scheduler)
            {
                this.scheduler = scheduler;
            }

            public DateTime UtcNow => DateTime.UtcNow;

            public Task<OperationResult<bool>> NextFrameAsync(CancellationToken token)
            {
                if (token.IsCancellationRequested)
                    return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The level load stopped."));
                var completion = new TaskCompletionSource<OperationResult<bool>>(TaskCreationOptions.RunContinuationsAsynchronously);
                IDisposable scheduled;
                try
                {
                    scheduled = scheduler.ScheduleFrames(1, () => completion.TrySetResult(OperationResult<bool>.Success(true)));
                }
                catch (ObjectDisposedException)
                {
                    return Task.FromResult(OperationResult<bool>.Failure(ModErrorCode.Unavailable, "The game scheduler is no longer available."));
                }

                var registration = token.Register(() =>
                {
                    scheduled.Dispose();
                    completion.TrySetResult(OperationResult<bool>.Failure(ModErrorCode.Cancelled, "The level load stopped."));
                });
                _ = completion.Task.ContinueWith(_ => registration.Dispose(), CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                return completion.Task;
            }
        }
    }
}
