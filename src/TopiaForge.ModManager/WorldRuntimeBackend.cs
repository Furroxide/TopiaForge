using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    internal interface IWorldRuntimeBackend
    {
        OperationResult<IReadOnlyList<NativeWorldEntry>> Discover(NativeWorldSource source, int maximumResults);
        OperationResult<IWorldNativeLoad> CreateLoad(NativeWorldLoadRequest request);
    }

    // Constructed before dispatch, so arrival observation and native identity cannot race the loader.
    internal interface IWorldNativeLoad : IInternalNativeSceneDispatch, IDisposable
    {
        string ExpectedSceneName { get; }
        bool RequiresDispatch { get; }
        OperationResult<WorldSceneIdentity> CaptureScene();
        OperationResult<bool> ValidateScene();
        OperationResult<bool> ValidateContent(IEntity? contentRoot);
        OperationResult<TransformState>? ReadDefaultSpawn();
        OperationResult<IReadOnlyList<TransformState>> FindSpawnMarkers(IEntity? contentRoot, string markerName);
        OperationResult<TransformState>? ApplySpawn(TransformState spawn);
    }

    internal interface IWorldRuntimeClock
    {
        TimeSpan Elapsed { get; }
        bool IsPhysicsPaused { get; }
        Task<OperationResult<bool>> NextFrameAsync(CancellationToken token);
    }

    internal sealed class WorldRuntimeClock : IWorldRuntimeClock
    {
        private readonly IModScheduler scheduler;
        private readonly Stopwatch elapsed = Stopwatch.StartNew();
        internal WorldRuntimeClock(IModScheduler scheduler) { this.scheduler = scheduler; }
        public TimeSpan Elapsed => elapsed.Elapsed;
        public bool IsPhysicsPaused => UnityEngine.Time.timeScale == 0f;
        public Task<OperationResult<bool>> NextFrameAsync(CancellationToken token) => scheduler.DelayAsync(TimeSpan.Zero, token);
    }
}
