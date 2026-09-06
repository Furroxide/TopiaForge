using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    internal sealed partial class UnityWorldRuntimeBackend
    {
        private sealed partial class NativeLoad : IWorldNativeLoad
        {
            private readonly UnityEntityRegistry entities;
            private readonly NativeWorldLoadRequest request;
            private readonly string scenePath;
            private readonly object? checkpoint;
            private readonly List<Scene> arrivals = new List<Scene>();
            private Scene? captured;
            private bool started;
            private bool disposed;
            private bool sceneLost;
            internal NativeLoad(UnityEntityRegistry entities, NativeWorldLoadRequest request, string scenePath, object? checkpoint)
            {
                this.entities = entities; this.request = request; this.scenePath = scenePath; this.checkpoint = checkpoint;
                ExpectedSceneName = scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) ? Path.GetFileNameWithoutExtension(scenePath) : scenePath;
                if (RequiresDispatch) SceneManager.sceneLoaded += OnSceneLoaded;
                else
                {
                    captured = SceneManager.GetActiveScene();
                    ExpectedSceneName = captured.Value.name;
                }
                SceneManager.sceneUnloaded += OnSceneUnloaded;
            }
            public string ExpectedSceneName { get; }
            public bool RequiresDispatch => request.Transition == WorldLoadTransition.SceneReplacement;
            private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
            {
                if (!started || disposed || !scene.IsValid() || !Matches(scene, scenePath)) return;
                if (!arrivals.Exists(existing => existing.handle == scene.handle)) arrivals.Add(scene);
            }
            private void OnSceneUnloaded(Scene scene)
            {
                if ((captured.HasValue && captured.Value.handle == scene.handle) || arrivals.Exists(value => value.handle == scene.handle)) sceneLost = true;
            }
            public NativeSceneDispatchStatus Begin(IInternalNativeSceneCompletion completion)
            {
                UnityMainThreadGuard.AssertCurrent();
                if (disposed || started)
                { completion.FailCaller(ModErrorCode.InvalidState, "The native world preparation is closed or already dispatched."); return NativeSceneDispatchStatus.NotDispatched; }
                MethodInfo? loader;
                NativeAwaiterMethods? observer;
                try
                {
                    if (checkpoint != null)
                    {
                        var loaderType = Type.GetType("LoadSceneOnTriggerEnter, GameCode", false);
                        var checkpointType = Type.GetType("CheckpointAsset, GameCode", false);
                        if (loaderType == null || checkpointType == null || !checkpointType.IsInstanceOfType(checkpoint))
                            throw new InvalidOperationException("The native checkpoint loader signature is unsupported.");
                        loader = NativeWorldReflection.CheckpointLoader(loaderType, checkpointType);
                    }
                    else loader = Type.GetType("SceneUtil, GameCode", false)?.GetMethod("LoadScene", PublicStatic,
                        null, new[] { typeof(string), typeof(CancellationToken) }, null);
                    observer = loader == null ? null : NativeWorldReflection.Awaiter(loader.ReturnType);
                    if (loader == null || (observer == null && !typeof(Task).IsAssignableFrom(loader.ReturnType)))
                        throw new InvalidOperationException("The native world loader has no supported observable completion contract.");
                    if (request.Kind == NativeWorldLoadKind.OpenSandbox)
                    {
                        var lastRun = Type.GetType("UgcPlayLauncherLastRun, GameCode", false);
                        var launchRequest = Type.GetType("UgcPlayLaunchRequest, GameCode", false);
                        if (lastRun?.GetField("Mode")?.FieldType != typeof(string)
                            || lastRun.GetField("ImportFolderPath")?.FieldType != typeof(string)
                            || lastRun.GetField("SelectedExportFilePath")?.FieldType != typeof(string)
                            || !TopiaForge.Mods.GameBridge.UgcNoOpLaunchRequest.TryQueue(lastRun, launchRequest, "TopiaForgeSessionSandbox", null))
                            throw new InvalidOperationException("Open Sandbox could not suppress the native content importer.");
                    }
                }
                catch (Exception error)
                {
                    completion.FailCaller(ModErrorCode.Unavailable, Unwrap(error).Message);
                    return NativeSceneDispatchStatus.NotDispatched;
                }
                started = true;
                completion.RequireManagedCompletion();
                var returned = false;
                try
                {
                    var task = loader.Invoke(null, checkpoint != null ? new[] { (object)false, checkpoint } : new object[] { scenePath, CancellationToken.None });
                    returned = true;
                    Observe(task, observer, completion);
                    return NativeSceneDispatchStatus.Dispatched;
                }
                catch (Exception error)
                {
                    var failure = Failure<bool>(ModErrorCode.External, "The native loader failed after possible scene effects: " + Unwrap(error).Message);
                    // If observer attachment failed after invocation returned, its native task may still run.
                    // Retain ownership until an attached completion arrives; never invent managed completion.
                    if (!returned) completion.ManagedCompleted(failure);
                    completion.FailCaller(failure.ErrorCode, failure.ErrorMessage);
                    return NativeSceneDispatchStatus.Indeterminate;
                }
            }
            private static void Observe(object? task, NativeAwaiterMethods? observer, IInternalNativeSceneCompletion completion)
            {
                if (task is Task managed)
                {
                    _ = managed.ContinueWith(done => completion.ManagedCompleted(done.IsFaulted || done.IsCanceled
                        ? Failure<bool>(done.IsCanceled ? ModErrorCode.Cancelled : ModErrorCode.External, done.Exception?.GetBaseException().Message ?? "Native loader cancelled.")
                        : OperationResult<bool>.Success(true)), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                    return;
                }
                if (task == null || observer == null)
                    throw new InvalidOperationException("The native loader returned no supported awaitable.");
                // Invoke the same declared contract validated before dispatch, even for runtime subtypes.
                var awaiter = observer.GetAwaiter.Invoke(task, null)
                    ?? throw new InvalidOperationException("The native loader returned no awaiter.");
                Action complete = () =>
                {
                    try { observer.GetResult.Invoke(awaiter, null); completion.ManagedCompleted(OperationResult<bool>.Success(true)); }
                    catch (Exception error) { completion.ManagedCompleted(Failure<bool>(ModErrorCode.External, Unwrap(error).Message)); }
                };
                if ((bool)observer.IsCompleted.GetValue(awaiter)!) complete();
                else observer.OnCompleted.Invoke(awaiter, new object[] { complete });
            }
            public OperationResult<WorldSceneIdentity> CaptureScene()
            {
                UnityMainThreadGuard.AssertCurrent();
                if (disposed) return Failure<WorldSceneIdentity>(ModErrorCode.Cancelled, "The world preparation stopped.");
                if (RequiresDispatch)
                {
                    if (arrivals.Count != 1) return Failure<WorldSceneIdentity>(arrivals.Count == 0 ? ModErrorCode.NotFound : ModErrorCode.Conflict,
                        "The native world load did not identify exactly one arriving scene instance.");
                    captured = arrivals[0];
                }
                var valid = ValidateScene();
                return valid.Succeeded ? OperationResult<WorldSceneIdentity>.Success(new WorldSceneIdentity(captured!.Value.handle, captured.Value.name))
                    : Failure<WorldSceneIdentity>(valid.ErrorCode, valid.ErrorMessage);
            }
            public OperationResult<bool> ValidateScene()
            {
                UnityMainThreadGuard.AssertCurrent();
                if (disposed) return Failure<bool>(ModErrorCode.Cancelled, "The world preparation stopped.");
                if (!sceneLost && captured.HasValue && (!RequiresDispatch || arrivals.Count == 1))
                {
                    var expected = captured.Value;
                    for (var index = 0; index < SceneManager.sceneCount; index++)
                    {
                        var current = SceneManager.GetSceneAt(index);
                        if (current.IsValid() && current.isLoaded && current.handle == expected.handle
                            && string.Equals(current.name, expected.name, StringComparison.Ordinal)
                            && string.Equals(current.path, expected.path, StringComparison.Ordinal)) return OperationResult<bool>.Success(true);
                    }
                }
                return Failure<bool>(ModErrorCode.InvalidState, "The prepared scene is no longer the same loaded scene instance.");
            }
            public void Dispose()
            {
                UnityMainThreadGuard.AssertCurrent();
                if (disposed) return;
                disposed = true;
                try { if (RequiresDispatch) SceneManager.sceneLoaded -= OnSceneLoaded; }
                finally { SceneManager.sceneUnloaded -= OnSceneUnloaded; arrivals.Clear(); captured = null; }
            }
        }
    }
}
