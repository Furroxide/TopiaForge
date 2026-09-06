using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Worlds
{
    internal sealed partial class UgcImportHostBridge
    {
        public OperationResult<ILocalImportTransaction> Prepare(RoboWorldImportPlan plan,
            IReadOnlyList<WorldAssetOverride> overrides, WorldSceneIdentity scene)
        {
            if (!IsAvailable)
                return OperationResult<ILocalImportTransaction>.Failure(ModErrorCode.Unavailable, "The native export loader is unavailable.");
            if (!TryValidateExport(plan.FilePath, out _, out var failure))
                return OperationResult<ILocalImportTransaction>.Failure(ModErrorCode.InvalidArgument, failure);
            try
            {
                if (importHostControllerType == null || !UnityWorldScene.TryGetLoaded(scene, out var nativeScene))
                    return OperationResult<ILocalImportTransaction>.Failure(ModErrorCode.Unavailable, "The session's native scene is unavailable.");
                var hosts = nativeScene.GetRootGameObjects().SelectMany(root => root.GetComponentsInChildren(importHostControllerType, true)).ToArray();
                if (hosts.Length != 1)
                    return OperationResult<ILocalImportTransaction>.Failure(ModErrorCode.Unavailable, "The session scene must contain exactly one local import host.");
                return OperationResult<ILocalImportTransaction>.Success(new ImportTransaction(hosts[0], plan, overrides, scene));
            }
            catch (Exception error)
            { return OperationResult<ILocalImportTransaction>.Failure(ModErrorCode.Unavailable, "Local import ownership bindings are unavailable: " + Unwrap(error).Message); }
        }

        private sealed class ImportTransaction : ILocalImportTransaction
        {
            private readonly Component host;
            private readonly RoboWorldImportPlan plan;
            private readonly WorldSceneIdentity scene;
            private readonly object sceneRoot;
            private readonly PropertyInfo activeRoot;
            private readonly MethodInfo discardRoot;
            private readonly PropertyInfo importedScene;
            private readonly MethodInfo configureFolder;
            private readonly MethodInfo importFile;
            private readonly NativeImportSelection selection;
            private readonly List<(string Id, GameObject Prefab, Vector3? Offset)> overrides = new List<(string, GameObject, Vector3?)>();
            private readonly object? assetConfig;
            private readonly MethodInfo? setOverride;
            private readonly IDictionary? overrideTable;
            private LocalImportOwnership? temporary;
            private bool used;
            internal ImportTransaction(Component host, RoboWorldImportPlan plan,
                IReadOnlyList<WorldAssetOverride> values, WorldSceneIdentity scene)
            {
                this.host = host; this.plan = plan; this.scene = scene;
                var type = host.GetType();
                importedScene = RequireProperty(type, "LastImportedScene", "UgcExportScene");
                var rootProperty = RequireProperty(type, "SceneRoot", "UgcSceneRoot");
                sceneRoot = rootProperty.GetValue(host) ?? throw new InvalidOperationException("The native import scene root is missing.");
                activeRoot = RequireProperty(sceneRoot.GetType(), "ActiveImportRoot", typeof(Transform).FullName!);
                discardRoot = RequireMethod(sceneRoot.GetType(), "DiscardSceneRebuild", typeof(void), typeof(Transform));
                configureFolder = RequireMethod(type, "ConfigureRuntimeImportFolder", typeof(void), typeof(string));
                importFile = RequireMethod(type, "ImportFile", typeof(void), typeof(string));
                selection = new NativeImportSelection(host, type, plan);
                if (values.Count == 0) return;
                assetConfig = RequireProperty(type, "RuntimeAssetConfig", "UgcRuntimeAssetConfig").GetValue(host)
                    ?? throw new InvalidOperationException("The native runtime asset config is missing.");
                setOverride = RequireMethod(assetConfig.GetType(), "SetRuntimeOverride", typeof(void), typeof(string), typeof(GameObject), typeof(Vector3?));
                overrideTable = assetConfig.GetType().GetField("runtimeOverrides", AnyInstance)?.GetValue(assetConfig) as IDictionary
                    ?? throw new InvalidOperationException("The native override table cannot be preserved.");
                foreach (var value in values)
                {
                    var prefab = value.Prefab.GetType().GetProperty("Prefab", AnyInstance)?.GetValue(value.Prefab) as GameObject;
                    if (prefab == null) throw new InvalidOperationException("Override '" + value.AssetId + "' no longer has an owned native prefab.");
                    Vector3? offset = value.LocalPositionOffset.HasValue
                        ? new Vector3(value.LocalPositionOffset.Value.X, value.LocalPositionOffset.Value.Y, value.LocalPositionOffset.Value.Z) : (Vector3?)null;
                    overrides.Add((value.AssetId, prefab, offset));
                }
            }
            public bool NativeEntered { get; private set; }
            public bool NativeReturned { get; private set; }
            public OperationResult<IDisposable> Import()
            {
                if (used) throw new InvalidOperationException("A local import transaction can run only once.");
                used = true;
                if (host == null || !UnityWorldScene.ContainsRoot(scene, host.gameObject))
                    return OperationResult<IDisposable>.Failure(ModErrorCode.InvalidState, "The captured import host left the session scene.");
                var previousData = importedScene.GetValue(host);
                var previousRoot = activeRoot.GetValue(sceneRoot) as Transform;
                var previousOverrides = overrideTable == null ? null : new NativeImportOverrides(overrideTable);
                temporary = new LocalImportOwnership(() => { }, () =>
                {
                    previousOverrides?.Restore();
                }, selection.Restore);
                IDisposable? content = null;
                OperationResult<IDisposable>? result = null;
                var failures = new List<Exception>();
                try
                {
                    selection.Apply();
                    foreach (var value in overrides) setOverride!.Invoke(assetConfig, new object?[] { value.Id, value.Prefab, value.Offset });
                    configureFolder.Invoke(host, new object[] { plan.FolderPath });
                    NativeEntered = true;
                    importFile.Invoke(host, new object[] { plan.FilePath });
                    NativeReturned = true;
                    var data = importedScene.GetValue(host);
                    var root = activeRoot.GetValue(sceneRoot) as Transform;
                    if (root != null && !ReferenceEquals(previousRoot, root)) content = OwnRoot(root);
                    if (!UgcImportCompletionPolicy.IsFresh(previousData, data) || content == null || !UnityWorldScene.ContainsRoot(scene, root!.gameObject))
                        result = OperationResult<IDisposable>.Failure(ModErrorCode.External, "The native importer did not produce fresh owned content in the session scene.");
                    else result = OperationResult<IDisposable>.Success(content);
                }
                catch (Exception error)
                {
                    failures.Add(Unwrap(error));
                    try
                    {
                        var root = activeRoot.GetValue(sceneRoot) as Transform;
                        if (content == null && root != null && !ReferenceEquals(previousRoot, root)) content = OwnRoot(root);
                    }
                    catch (Exception captureFailure) { failures.Add(Unwrap(captureFailure)); }
                }
                try { temporary.Dispose(); } catch (Exception error) { failures.Add(error); }
                if (failures.Count == 0 && result != null && result.Succeeded) return result;
                try { content?.Dispose(); } catch (Exception error) { failures.Add(error); }
                if (failures.Count > 0) throw new AggregateException("Local import mutation or cleanup failed.", failures);
                return result ?? OperationResult<IDisposable>.Failure(ModErrorCode.External, "The native importer did not produce content.");
            }
            private IDisposable OwnRoot(Transform root) => new LocalImportOwnership(() =>
            {
                if (root != null && sceneRoot is UnityEngine.Object nativeRoot && nativeRoot != null)
                    discardRoot.Invoke(sceneRoot, new object[] { root });
            }, () =>
            {
                // Retain the exact tree even if its native host disappeared or its cleanup threw.
                if (root != null) UnityEngine.Object.Destroy(root.gameObject);
            }, () => { });
            public void Dispose() { temporary?.Dispose(); }
        }
        private static PropertyInfo RequireProperty(Type type, string name, string returnType)
        {
            var property = type.GetProperty(name, PublicInstance);
            return property != null && property.PropertyType.FullName == returnType && property.GetMethod != null
                ? property : throw new MissingMemberException(type.FullName, name);
        }
        private static MethodInfo RequireMethod(Type type, string name, Type returnType, params Type[] arguments)
        {
            var method = type.GetMethod(name, PublicInstance, null, arguments, null);
            return method != null && method.ReturnType == returnType ? method : throw new MissingMethodException(type.FullName, name);
        }
    }
}
