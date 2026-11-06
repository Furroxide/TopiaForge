namespace TopiaForge.ModManager.Tests
{
    internal static partial class NativeSpawnWiringTests
    {
        private const string Adapter = """
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;
using UnityEngine.SceneManagement;
namespace TopiaForge.ModManager
{
    internal sealed class ProbeNativeLoad : IWorldNativeLoad
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private readonly UnityEntityRegistry entities = new UnityEntityRegistry();
        private readonly NativeWorldLoadRequest request = NativeWorldLoadRequest.Scene("World", WorldLoadTransition.AdditiveArena);
        private readonly string scenePath = "World.unity";
        private readonly List<Scene> arrivals = new List<Scene>();
        private Scene? captured;
        private bool disposed, sceneLost, started;
        internal ProbeNativeLoad() { captured = SceneManager.GetSceneAt(0); SceneManager.sceneUnloaded += OnSceneUnloaded; }
        public string ExpectedSceneName => "World";
        public NativeSceneDispatchStatus Begin(IInternalNativeSceneCompletion completion) => throw new NotSupportedException("No dispatch in this additive preparation probe.");
        internal bool IsDisposed => disposed;
        /* production */
    }
}
namespace SettlementProbe
{
    internal sealed class FrameClock : TopiaForge.ModManager.IWorldRuntimeClock
    {
        public TimeSpan Elapsed { get; private set; }
        public bool IsPhysicsPaused => Time.timeScale == 0f;
        internal bool AdvanceFixed = true;
        internal Action<int>? OnFrame;
        internal void AdvanceElapsed(TimeSpan duration) => Elapsed += duration;
        public Task<OperationResult<bool>> NextFrameAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            AdvanceElapsed(TimeSpan.FromSeconds(1));
            Time.frameCount = unchecked(Time.frameCount + 1);
            if (AdvanceFixed && !IsPhysicsPaused)
            {
                Time.fixedTime += 0.02f;
                PlayerController.Current?.FPSController.FixedStep();
            }
            OnFrame?.Invoke(Time.frameCount);
            return Task.FromResult(OperationResult<bool>.Success(true));
        }
    }
    internal sealed class Admission : IInternalSceneTransitionLease
    {
        internal int Releases;
        public IInternalSceneTransitionService Transitions => throw new NotSupportedException("No content dispatch in this probe.");
        public void Dispose() { Releases++; }
    }
}
""";
    }
}
