using TopiaForge.Mods.Internal;
using UnityEngine.SceneManagement;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Worlds
{
    /// <summary>
    /// Clean-room reflection bridge into the game's own scene/level system so modded gamemodes launch
    /// through the same path the game uses (real HDRP scenes, checkpoints, player spawn) instead of a
    /// bare arena. All access is reflective and defensive so a missing/renamed game symbol degrades
    /// gracefully rather than crashing the game.
    /// </summary>
    internal sealed class GameLevelBridge : IDisposable
    {
        private const BindingFlags PublicStatic = BindingFlags.Public | BindingFlags.Static;
        private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>
        /// The game's dedicated, story-free play scene (see <c>LoadUgcScene.sceneName</c> / the
        /// <c>UgcPlayLauncherController</c> default). Loading it brings up a clean scene whose
        /// <c>UgcImportPlayBootstrap</c> spawns a real, walking player — the base for the Open Sandbox arena.
        /// </summary>
        public const string SandboxSceneName = "UgcPlay";

        private readonly IModLogger logger;
        private readonly IInternalSceneTransitionService sceneTransitions;
        private readonly Type? globalAssetsMapType;
        private readonly Type? loadSceneType;
        private readonly Type? sceneUtilType;
        private readonly Type? ugcBootstrapType;
        private readonly Type? ugcLaunchRequestType;
        private readonly Type? ugcLastRunType;
        private readonly Type? playerControllerType;
        private readonly MainThreadDispatchQueue<AsyncLoadOutcome> loadOutcomes =
            new MainThreadDispatchQueue<AsyncLoadOutcome>();

        public GameLevelBridge(IModLogger logger, IInternalSceneTransitionService sceneTransitions)
        {
            this.logger = logger;
            this.sceneTransitions = sceneTransitions;
            globalAssetsMapType = Type.GetType("GlobalAssetsMap, GameCode", throwOnError: false);
            loadSceneType = Type.GetType("LoadSceneOnTriggerEnter, GameCode", throwOnError: false);
            sceneUtilType = Type.GetType("SceneUtil, GameCode", throwOnError: false);
            ugcBootstrapType = Type.GetType("UgcImportPlayBootstrap, GameCode", throwOnError: false);
            ugcLaunchRequestType = Type.GetType("UgcPlayLaunchRequest, GameCode", throwOnError: false);
            ugcLastRunType = Type.GetType("UgcPlayLauncherLastRun, GameCode", throwOnError: false);
            playerControllerType = Type.GetType("PlayerController, GameCode", throwOnError: false);
        }

        /// <summary>Reads the curated level entry points the game shows in its own level-select menu.</summary>
        public List<GameLevel> GetLevels()
        {
            var levels = new List<GameLevel>();
            try
            {
                var entryPoints = globalAssetsMapType?.GetProperty("LevelEntryPoints", PublicStatic)?.GetValue(null);
                if (entryPoints == null)
                {
                    return levels;
                }

                var entriesField = entryPoints.GetType().GetField("entries", AnyInstance);
                if (!(entriesField?.GetValue(entryPoints) is Array entries))
                {
                    return levels;
                }

                foreach (var entry in entries)
                {
                    if (entry == null)
                    {
                        continue;
                    }

                    try
                    {
                        var entryType = entry.GetType();
                        var checkpointAsset = entryType.GetField("checkpointAsset", AnyInstance)?.GetValue(entry);
                        if (checkpointAsset == null)
                        {
                            continue;
                        }

                        var sceneName = checkpointAsset.GetType().GetProperty("SceneName", AnyInstance)?.GetValue(checkpointAsset) as string ?? string.Empty;
                        if (string.IsNullOrWhiteSpace(sceneName))
                        {
                            continue;
                        }

                        var displayName = entryType.GetField("displayName", AnyInstance)?.GetValue(entry) as string ?? sceneName;
                        var description = entryType.GetField("description", AnyInstance)?.GetValue(entry) as string ?? string.Empty;
                        levels.Add(new GameLevel(displayName, description, sceneName, checkpointAsset));
                    }
                    catch (Exception ex)
                    {
                        logger.Debug("Worlds skipped a level entry: " + ex.Message);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not read game level entry points: " + ex.Message);
            }

            return levels;
        }

        public bool LaunchLevel(object checkpointAsset, Action<string>? onFailure = null,
            IInternalSceneTransitionService? transitionOverride = null)
        {
            string? sceneName;
            try { sceneName = checkpointAsset?.GetType().GetProperty("SceneName", AnyInstance)?.GetValue(checkpointAsset) as string; }
            catch { return false; }
            if (string.IsNullOrWhiteSpace(sceneName)) return false;
            return Dispatch(sceneName!, onFailure, transitionOverride,
                (sink, mark) => LaunchLevelCore(checkpointAsset!, onFailure, sink, mark));
        }

        public bool LoadSceneByName(string sceneName, Action<string>? onFailure = null,
            IInternalSceneTransitionService? transitionOverride = null) =>
            Dispatch(sceneName, onFailure, transitionOverride,
                (sink, mark) => LoadSceneByNameCore(sceneName, onFailure, sink, mark));

        public bool LaunchOpenSandbox(Action<string>? onFailure = null,
            IInternalSceneTransitionService? transitionOverride = null) =>
            Dispatch(SandboxSceneName, onFailure, transitionOverride,
                (sink, mark) => LaunchOpenSandboxCore(onFailure, sink, mark));

        public bool LoadSceneDirect(string sceneName, Action<string>? onFailure,
            IInternalSceneTransitionService? transitionOverride) =>
            Dispatch(sceneName, onFailure, transitionOverride, (sink, mark) =>
            {
                if (!Application.CanStreamedLevelBeLoaded(sceneName)) return false;
                mark();
                SceneManager.LoadScene(sceneName, LoadSceneMode.Single);
                var scene = SceneManager.GetActiveScene();
                sink.NativeCompleted(OperationResult<SceneSnapshot>.Success(new SceneSnapshot(scene.name, scene.isLoaded, true)));
                return true;
            });

        private bool Dispatch(string sceneName, Action<string>? onFailure,
            IInternalSceneTransitionService? transitionOverride,
            Func<IInternalNativeSceneCompletion, Action, bool> begin)
        {
            if (string.IsNullOrWhiteSpace(sceneName)) return false;
            var result = (transitionOverride ?? sceneTransitions).TryDispatch(
                new NativeSceneRequest(sceneName, false, "world native load"),
                new DelegateNativeSceneDispatch(sink =>
                {
                    var entered = false;
                    try
                    {
                        if (begin(sink, () => entered = true)) return NativeSceneDispatchStatus.Dispatched;
                    }
                    catch (Exception error)
                    {
                        sink.FailCaller(ModErrorCode.External, error.Message);
                        NotifyFailure(onFailure, error.Message);
                    }
                    if (!entered) return NativeSceneDispatchStatus.NotDispatched;
                    sink.FailCaller(ModErrorCode.External, "The game loader failed after possible native effects; awaiting native drain.");
                    NotifyFailure(onFailure, "The game loader failed after possible native effects.");
                    return NativeSceneDispatchStatus.Indeterminate;
                }));
            return result.Succeeded;
        }

        /// <summary>Launches a real level the same way the game's menu does (correct play state).</summary>
        private bool LaunchLevelCore(object checkpointAsset, Action<string>? onFailure, IInternalNativeSceneCompletion nativeCompletion, Action markDispatched)
        {
            try
            {
                // Validate the checkpoint up front. NOTE: LoadSceneImpl is `async UniTask`; a fault inside the
                // awaited scene load surfaces only on the returned UniTask, so a "true" here means "dispatched",
                // not "scene loaded". We now observe that UniTask (see ObserveAsyncLoad) so a post-await fault is
                // logged as a failure instead of being silently reported as a successful load.
                if (checkpointAsset == null)
                {
                    return false;
                }

                var sceneName = checkpointAsset.GetType().GetProperty("SceneName", AnyInstance)?.GetValue(checkpointAsset) as string;
                if (string.IsNullOrWhiteSpace(sceneName))
                {
                    logger.Warn("Worlds cannot launch a level whose checkpoint has no scene name.");
                    return false;
                }

                var method = loadSceneType?.GetMethod("LoadSceneImpl", PublicStatic);
                if (method == null)
                {
                    return false;
                }

                // Capture the returned UniTask. If we discard it, a fault raised AFTER its first await is lost:
                // UniTask publishes no UnobservedTaskException for a dropped task, so the scene could fail to load
                // while WorldsService still reports "Loaded ...". Observing it makes that failure visible.
                markDispatched();
                var loadTask = method.Invoke(null, new[] { (object)false, checkpointAsset });
                ObserveAsyncLoad(loadTask, sceneName!, onFailure, nativeCompletion);

                logger.Info("Worlds dispatched the game loader for scene '" + sceneName + "'.");
                return true;
            }
            catch (Exception ex)
            {
                logger.Warn("Worlds failed to launch level via the game loader: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Observes the async <c>LoadSceneImpl</c> UniTask so a fault that surfaces AFTER its first await is logged
        /// instead of being swallowed (UniTask raises no UnobservedTaskException for a discarded task). Best-effort
        /// and reflective: if the UniTask/Task plumbing cannot be reached we degrade to the dispatch log alone.
        /// </summary>
        private void ObserveAsyncLoad(object? loadTask, string sceneName, Action<string>? onFailure, IInternalNativeSceneCompletion nativeCompletion)
        {
            if (loadTask == null)
            {
                return;
            }

            try
            {
                // Preferred: convert the UniTask to a System.Threading.Tasks.Task (UniTaskExtensions.AsTask) and
                // attach a plain continuation, keeping the result inspection in ordinary, non-reflective code.
                if (TryObserveViaTask(loadTask, sceneName, onFailure, nativeCompletion))
                {
                    return;
                }

                // Fallback: drive the awaitable contract directly (GetAwaiter/IsCompleted/GetResult/OnCompleted),
                // which UniTask must implement to be awaitable even if AsTask is renamed/absent in this build.
                if (TryObserveViaAwaiter(loadTask, sceneName, onFailure, nativeCompletion))
                {
                    return;
                }

                logger.Debug("Worlds could not attach a load-result observer for scene '" + sceneName
                    + "'; a post-dispatch failure will not be logged.");
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not observe the async level load for scene '" + sceneName + "': " + ex.Message);
            }
        }

        private bool TryObserveViaTask(object loadTask, string sceneName, Action<string>? onFailure, IInternalNativeSceneCompletion nativeCompletion)
        {
            var uniTaskType = loadTask.GetType();
            var extensionsType = uniTaskType.Assembly.GetType("Cysharp.Threading.Tasks.UniTaskExtensions");
            var asTask = extensionsType?.GetMethod("AsTask", PublicStatic, null, new[] { uniTaskType }, null);
            if (!(asTask?.Invoke(null, new[] { loadTask }) is Task task))
            {
                return false;
            }

            // Completion may run on any scheduler. Queue only immutable outcome data here; logging and the
            // generation-bound failure callback are delivered by DrainAsyncLoadOutcomes on Unity's main thread.
            _ = task.ContinueWith(
                completed => QueueLoadOutcome(
                    completed.IsFaulted,
                    completed.IsCanceled,
                    completed.Exception?.GetBaseException()?.Message,
                    sceneName,
                    onFailure, nativeCompletion),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return true;
        }

        private bool TryObserveViaAwaiter(object loadTask, string sceneName, Action<string>? onFailure, IInternalNativeSceneCompletion nativeCompletion)
        {
            var getAwaiter = loadTask.GetType().GetMethod("GetAwaiter", AnyInstance, null, Type.EmptyTypes, null);
            var awaiter = getAwaiter?.Invoke(loadTask, null);
            if (awaiter == null)
            {
                return false;
            }

            var awaiterType = awaiter.GetType();
            var getResult = awaiterType.GetMethod("GetResult", AnyInstance, null, Type.EmptyTypes, null);
            if (getResult == null)
            {
                return false;
            }

            // GetResult returns void on success and rethrows the captured fault (reflection wraps it) on failure.
            Action report = () =>
            {
                try
                {
                    getResult.Invoke(awaiter, null);
                    QueueLoadOutcome(false, false, null, sceneName, onFailure, nativeCompletion);
                }
                catch (TargetInvocationException ex)
                {
                    QueueLoadOutcome(true, false, (ex.InnerException ?? ex).Message, sceneName, onFailure, nativeCompletion);
                }
                catch (Exception ex)
                {
                    QueueLoadOutcome(true, false, ex.Message, sceneName, onFailure, nativeCompletion);
                }
            };

            var isCompleted = awaiterType.GetProperty("IsCompleted", AnyInstance)?.GetValue(awaiter) as bool? ?? false;
            if (isCompleted)
            {
                report();
                return true;
            }

            // UniTask normally resumes in the Unity PlayerLoop, but the awaitable contract does not guarantee
            // that to this reflective bridge. The continuation therefore only queues an outcome as well.
            var onCompleted = awaiterType.GetMethod("OnCompleted", AnyInstance, null, new[] { typeof(Action) }, null)
                ?? awaiterType.GetMethod("UnsafeOnCompleted", AnyInstance, null, new[] { typeof(Action) }, null);
            if (onCompleted == null)
            {
                return false;
            }

            onCompleted.Invoke(awaiter, new object[] { report });
            return true;
        }

        private void QueueLoadOutcome(
            bool faulted,
            bool canceled,
            string? error,
            string sceneName,
            Action<string>? onFailure, IInternalNativeSceneCompletion nativeCompletion)
        {
            if (faulted || canceled)
                nativeCompletion.FailCaller(canceled ? ModErrorCode.Cancelled : ModErrorCode.External,
                    "The game loader failed after dispatch: " + (error ?? sceneName));
            loadOutcomes.TryEnqueue(new AsyncLoadOutcome(faulted, canceled, error, sceneName, onFailure));
        }

        /// <summary>
        /// Delivers async loader completions on the caller's thread. WorldsService invokes this from its Unity
        /// update tick before consuming the generation-aware transition tracker.
        /// </summary>
        public void DrainAsyncLoadOutcomes()
        {
            loadOutcomes.Drain(ReportLoadOutcome);
        }

        private void ReportLoadOutcome(AsyncLoadOutcome outcome)
        {
            var faulted = outcome.Faulted;
            var canceled = outcome.Canceled;
            var error = outcome.Error;
            var sceneName = outcome.SceneName;
            var onFailure = outcome.OnFailure;
            if (faulted)
            {
                var message = "Scene '" + sceneName + "' failed after dispatch: "
                    + (string.IsNullOrEmpty(error) ? "unknown error" : error);
                NotifyFailure(onFailure, message);
                logger.Warn("Worlds level load FAILED after dispatch: " + message);
            }
            else if (canceled)
            {
                var message = "Scene '" + sceneName + "' load was canceled after dispatch.";
                NotifyFailure(onFailure, message);
                logger.Warn("Worlds level load was canceled after dispatch for scene '" + sceneName + "'.");
            }
            else
            {
                logger.Info("Worlds confirmed the game finished loading scene '" + sceneName + "'.");
            }
        }

        public void Dispose()
        {
            loadOutcomes.Dispose();
        }

        private void NotifyFailure(Action<string>? onFailure, string message)
        {
            if (onFailure == null)
            {
                return;
            }

            try
            {
                onFailure(message);
            }
            catch (Exception ex)
            {
                logger.Warn("Worlds load-failure callback failed: " + ex.Message);
            }
        }

        /// <summary>Loads a scene by name through the game's async loader when no checkpoint asset is known.</summary>
        private bool LoadSceneByNameCore(string sceneName, Action<string>? onFailure, IInternalNativeSceneCompletion nativeCompletion, Action markDispatched)
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                return false;
            }

            try
            {
                var method = sceneUtilType?.GetMethod("LoadScene", PublicStatic, null, new[] { typeof(string), typeof(CancellationToken) }, null);
                if (method == null)
                {
                    return false;
                }

                // Capture and observe the returned UniTask so an async load fault is logged, not swallowed.
                markDispatched();
                var loadTask = method.Invoke(null, new object[] { sceneName, CancellationToken.None });
                ObserveAsyncLoad(loadTask, sceneName, onFailure, nativeCompletion);
                return true;
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not use the game scene loader for '" + sceneName + "': " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Launches the clean, story-free Open Sandbox arena: loads the game's <see cref="SandboxSceneName"/>
        /// scene (which spawns a real player) with any UGC content import suppressed. Returns true on dispatch;
        /// the caller layers the arena geometry on once the scene finishes loading.
        /// </summary>
        private bool LaunchOpenSandboxCore(Action<string>? onFailure, IInternalNativeSceneCompletion nativeCompletion, Action markDispatched)
        {
            try
            {
                // Suppress the UGC importer first so the play scene comes up empty (no past creation loaded),
                // then load it. The scene's own bootstrap spawns the player; we only need a clean stage.
                if (sceneUtilType?.GetMethod("LoadScene", PublicStatic, null,
                        new[] { typeof(string), typeof(CancellationToken) }, null) == null) return false;
                markDispatched();
                SuppressUgcImport();
                return LoadSceneByNameCore(SandboxSceneName, onFailure, nativeCompletion, markDispatched);
            }
            catch (Exception ex)
            {
                logger.Warn("Worlds failed to launch the open sandbox scene: " + ex.Message);
                return false;
            }
        }

        /// <summary>True if the game currently has a spawned player (static <c>PlayerController.FindPlayer()</c>).</summary>
        public bool IsPlayerPresent()
        {
            try
            {
                var findPlayer = playerControllerType?.GetMethod("FindPlayer", PublicStatic, null, Type.EmptyTypes, null);
                return findPlayer?.Invoke(null, null) != null;
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not query the player controller: " + ex.Message);
                return false;
            }
        }

        /// <summary>The live player's transform via <c>PlayerController.FindPlayer()</c>, or null.</summary>
        public Transform? GetPlayerTransform()
        {
            try
            {
                var findPlayer = playerControllerType?.GetMethod("FindPlayer", PublicStatic, null, Type.EmptyTypes, null);
                return findPlayer?.Invoke(null, null) is Component player ? player.transform : null;
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not resolve the player transform: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Moves the live player to a position (custom-world kill-plane respawn). A CharacterController is
        /// disabled around the move so it does not fight the teleport, then re-enabled.
        /// </summary>
        public bool RepositionPlayer(Vector3 position)
        {
            try
            {
                var player = GetPlayerTransform();
                if (player == null)
                {
                    return false;
                }

                var controller = player.GetComponent<CharacterController>();
                if (controller != null)
                {
                    controller.enabled = false;
                }

                player.position = position;
                if (controller != null)
                {
                    controller.enabled = true;
                }

                return true;
            }
            catch (Exception ex)
            {
                logger.Warn("Worlds could not reposition the player: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Reads the player prefab the UGC play bootstrap would spawn, so we can spawn a fallback player if the
        /// game's bootstrap did not (e.g. its import step faulted). Returns null if the symbol is unavailable.
        /// </summary>
        public GameObject? ResolveSandboxPlayerPrefab()
        {
            try
            {
                var bootstrap = FindSandboxBootstrap();
                return bootstrap == null
                    ? null
                    : ugcBootstrapType?.GetProperty("RuntimePlayerPrefab", AnyInstance)?.GetValue(bootstrap) as GameObject;
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not resolve the sandbox player prefab: " + ex.Message);
                return null;
            }
        }

        /// <summary>The position the sandbox bootstrap will spawn the player at (its own transform), or origin.</summary>
        public Vector3 GetSandboxSpawnPosition()
        {
            try
            {
                return FindSandboxBootstrap() is Component component ? component.transform.position : Vector3.zero;
            }
            catch (Exception ex)
            {
                logger.Debug("Worlds could not resolve the sandbox spawn position: " + ex.Message);
                return Vector3.zero;
            }
        }

        /// <summary>Spawns the given player prefab at <paramref name="position"/> (used only as a fallback).</summary>
        public void SpawnPlayer(GameObject prefab, Vector3 position)
        {
            try
            {
                UnityEngine.Object.Instantiate(prefab, position, Quaternion.identity);
            }
            catch (Exception ex)
            {
                logger.Warn("Worlds could not spawn a fallback sandbox player: " + ex.Message);
            }
        }

        private UnityEngine.Object? FindSandboxBootstrap()
        {
            return ugcBootstrapType == null ? null : UnityEngine.Object.FindAnyObjectByType(ugcBootstrapType);
        }

        /// <summary>
        /// Best-effort: hands the game's UGC play bootstrap a launch request pointed at an empty folder so it
        /// imports no user content into our sandbox. The bootstrap still runs and spawns the player; only the
        /// content import is neutralized. Degrades silently if the UGC symbols are missing.
        /// </summary>
        private void SuppressUgcImport()
        {
            if (TopiaForge.Mods.GameBridge.UgcNoOpLaunchRequest.TryQueue(
                ugcLastRunType,
                ugcLaunchRequestType,
                "TopiaForgeWorldsSandbox",
                message => logger.Debug("Worlds sandbox: " + message)))
            {
                logger.Debug("Worlds sandbox: queued a no-op UGC launch request to suppress content import.");
            }
        }

        private sealed class AsyncLoadOutcome
        {
            public AsyncLoadOutcome(
                bool faulted,
                bool canceled,
                string? error,
                string sceneName,
                Action<string>? onFailure)
            {
                Faulted = faulted;
                Canceled = canceled;
                Error = error;
                SceneName = sceneName;
                OnFailure = onFailure;
            }

            public bool Faulted { get; }
            public bool Canceled { get; }
            public string? Error { get; }
            public string SceneName { get; }
            public Action<string>? OnFailure { get; }
        }
    }

    internal sealed class GameLevel
    {
        public GameLevel(string displayName, string description, string sceneName, object checkpointAsset)
        {
            DisplayName = displayName;
            Description = description;
            SceneName = sceneName;
            CheckpointAsset = checkpointAsset;
        }

        public string DisplayName { get; }
        public string Description { get; }
        public string SceneName { get; }
        public object CheckpointAsset { get; }
    }
}
