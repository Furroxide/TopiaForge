using System;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    /// <summary>Process-owned pump. Runtime leases never dispose native completion tracking.</summary>
    internal sealed class NativeTransitionHost
    {
        private static NativeTransitionHost? current;
        private readonly GameObject loopObject;
        private string? activeRuntimeId;
        private Action<string>? logSink;
        internal SceneCoordinator Coordinator { get; }
        internal HostDispatcher Dispatcher { get; }
        internal UnitySceneBackend Scenes { get; }

        private NativeTransitionHost(Action<string>? log, ISceneTransitionAuthorityPolicy? authority)
        {
            UnityMainThreadGuard.CaptureCurrentThread();
            logSink = log;
            Dispatcher = new HostDispatcher(error => Report("Native host callback failed: " + error.Message));
            Coordinator = new SceneCoordinator(Report, authority, Dispatcher);
            Coordinator.UpdateLogSink(Report);
            Scenes = new UnitySceneBackend();
            loopObject = new GameObject("TopiaForge.NativeTransitionHost");
            UnityEngine.Object.DontDestroyOnLoad(loopObject);
            loopObject.AddComponent<NativeTransitionPump>().Owner = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        internal static NativeTransitionHost GetOrCreate(Action<string>? log = null,
            ISceneTransitionAuthorityPolicy? authorityPolicy = null)
        {
            UnityMainThreadGuard.AssertCurrent();
            return current ?? (current = new NativeTransitionHost(log, authorityPolicy));
        }

        internal void AttachRuntime(string ownershipId, ISceneTransitionAuthorityPolicy policy, Action<string>? logInfo = null)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (activeRuntimeId != null && activeRuntimeId != ownershipId)
                throw new InvalidOperationException("Detach the previous runtime before attaching another authority owner.");
            logSink = logInfo;
            Coordinator.UpdateAuthorityPolicy(policy);
            activeRuntimeId = ownershipId;
        }

        internal void DetachRuntime(string ownershipId)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (activeRuntimeId != null && activeRuntimeId != ownershipId)
                throw new InvalidOperationException("A stale runtime cannot detach a newer runtime.");
            Coordinator.RevokeOwnership(ownershipId);
            activeRuntimeId = null;
            logSink = null;
            Coordinator.SetSessionAdmissionGate(() => false);
        }

        private void Report(string message) { try { logSink?.Invoke(message); } catch { } }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode) =>
            Coordinator.NotifySceneArrived(new SceneSnapshot(scene.name, scene.isLoaded, scene == SceneManager.GetActiveScene()));

        private void Tick()
        {
            Dispatcher.Drain();
            Scenes.PollNativeOperations();
            Coordinator.CheckTimeout(DateTime.UtcNow, TimeSpan.FromSeconds(30));
        }

        // There is deliberately no Dispose-on-runtime-unload method. The Unity process owns this object;
        // pending native operations and the event pump remain alive until native completion or process exit.
        private sealed class NativeTransitionPump : MonoBehaviour
        {
            internal NativeTransitionHost? Owner;
            private void Update() => Owner?.Tick();
        }
    }
}
