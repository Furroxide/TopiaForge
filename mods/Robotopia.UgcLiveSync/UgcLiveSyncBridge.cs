using System.Collections.Generic;
using Robotopia.Mods;

namespace Robotopia.UgcLiveSync
{
    /// <summary>
    /// Seam between the Unity-free service logic (<see cref="UgcLiveSyncService"/>) and the clean-room
    /// reflection into the game (<see cref="UgcGameBridge"/>). Keeping this interface Unity-free lets the
    /// service state machine be unit-tested on plain .NET with a fake bridge — the real implementation is the
    /// only part that touches <c>UnityEngine</c> / <c>GameCode</c>.
    /// </summary>
    internal interface IUgcLiveSyncBridge
    {
        /// <summary>True when the required game symbols were resolved (false disables the service).</summary>
        bool IsAvailable { get; }

        /// <summary>True when a UGC import host controller exists in the active scene and can receive content.</summary>
        bool IsImportControllerReady();

        /// <summary>Name of the game's UGC play scene (what <see cref="EnsurePlaySceneLoaded"/> loads).</summary>
        string PlaySceneName { get; }

        /// <summary>
        /// True when <paramref name="sceneName"/> is Unity's active scene. This lets the Unity-free service
        /// distinguish a resolving single-mode transition from an unrelated additive scene notification.
        /// </summary>
        bool IsActiveScene(string sceneName);

        /// <summary>Loads the game's UGC play scene (content import suppressed) so a controller becomes available.</summary>
        bool EnsurePlaySceneLoaded();

        /// <summary>The game's default UGC import folder, used when no watch folder is configured (may be empty).</summary>
        string GetDefaultWatchFolder();

        /// <summary>Resets the incremental-apply state (prev snapshot + patcher) for a fresh session.</summary>
        void ResetApplyState();

        /// <summary>Registers modder asset-id overrides on the game's built-in asset map.</summary>
        void ApplyAssetOverrides(IReadOnlyList<UgcAssetOverride> overrides);

        /// <summary>Clears any runtime asset overrides previously applied.</summary>
        void ClearAssetOverrides();

        /// <summary>
        /// Applies one exported-project snapshot to the running scene, reproducing the game's own
        /// import/diff/patch logic. Throws on failure (the caller turns that into a non-fatal sync error and
        /// keeps the previous snapshot). The first successful call full-builds; later calls diff + patch.
        /// </summary>
        UgcApplyOutcome ApplyLocalSnapshot(byte[] bytes, string sceneId, string label);

        /// <summary>
        /// Queues the game's native Automerge live controller request. When <paramref name="loadPlayScene"/> is
        /// true, this also loads the UGC play scene; when false, the request stays armed for a later transition.
        /// <paramref name="onRevision"/> runs on the Unity main thread for each imported revision, best-effort.
        /// </summary>
        bool StartAutomerge(
            string documentUrl,
            string syncServerUrl,
            string sceneId,
            bool loadPlayScene,
            System.Action<UgcApplyOutcome> onRevision);

        /// <summary>Stops the native Automerge controller (destroys its GameObject) and detaches callbacks.</summary>
        void StopAutomerge();

        /// <summary>Forwarded scene-load notification; lets the Automerge channel confirm the live session is up.</summary>
        void NotifySceneLoaded(string sceneName);
    }

    /// <summary>Result of applying one snapshot; carries the metadata the service surfaces in events.</summary>
    internal sealed class UgcApplyOutcome
    {
        public UgcApplyOutcome(string projectName, string sceneId, string sceneName, int entityCount, bool isFullRebuild, bool wasFirstSnapshot)
        {
            ProjectName = projectName;
            SceneId = sceneId;
            SceneName = sceneName;
            EntityCount = entityCount;
            IsFullRebuild = isFullRebuild;
            WasFirstSnapshot = wasFirstSnapshot;
        }

        public string ProjectName { get; }
        public string SceneId { get; }
        public string SceneName { get; }
        public int EntityCount { get; }
        public bool IsFullRebuild { get; }
        public bool WasFirstSnapshot { get; }
    }
}
