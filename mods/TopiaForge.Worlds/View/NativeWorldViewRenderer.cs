using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.Worlds
{
    internal sealed class NativeWorldViewRenderer : IInternalWorldViewRenderer
    {
        private readonly WorldSceneIdentity scene;
        private readonly IModContext context;
        private readonly NativeMapViewSource? map;
        private readonly NativeMapViewSource.NativeEnvironment? environment;
        private readonly NativeViewHiding hiding = new NativeViewHiding();
        private readonly NativeVisionFog fog;
        private readonly GameObject? sentinel;
        private bool disposed;
        private bool disposing;
        internal static IInternalWorldViewRenderer Create(WorldReadiness world, IModContext context) =>
            world.Map is AuthoredMapIndex map && map.ViewSource != null
                ? map.ViewSource.CreateRenderer(context) : new NativeWorldViewRenderer(world.Scene, context);
        internal NativeWorldViewRenderer(WorldSceneIdentity scene, IModContext context, NativeMapViewSource? map = null)
        {
            this.scene = scene; this.context = context; this.map = map;
            environment = map?.BindEnvironment(); fog = new NativeVisionFog(scene, context.Events);
            if (UnityWorldScene.TryGetLoaded(scene, out var loaded))
            {
                try
                {
                    sentinel = new GameObject("Worlds View Lifetime") { hideFlags = HideFlags.DontSave };
                    SceneManager.MoveGameObjectToScene(sentinel, loaded);
                    if (map != null) sentinel.transform.SetParent(map.Root, false);
                    sentinel.AddComponent<WorldViewLifetime>().End = Dispose;
                }
                catch
                {
                    if (sentinel != null) UnityEngine.Object.Destroy(sentinel);
                    fog.Dispose(); throw;
                }
            }
        }
        public bool IsAlive => !disposed && (map == null || map.IsAlive) && UnityWorldScene.TryGetLoaded(scene, out _);
        public bool FogAvailable => IsAlive && NativeVisionFog.IsSupported;
        public bool EnvironmentAvailable => IsAlive && environment != null;
        public IReadOnlyList<string> Presets => environment?.Presets ?? Array.Empty<string>();
        public event Action Stopped = delegate { };
        public bool HasPreset(string preset) => environment?.HasPreset(preset) == true;
        public OperationResult<bool> SetEnvironment(string? preset) => environment?.Set(preset)
            ?? OperationResult<bool>.Failure(ModErrorCode.Unavailable, "This world has no environment presets.");
        public OperationResult<bool> SetFog(VisionFog? value) => fog.Set(value);
        public OperationResult<IDisposable> Hide(IEntity entity)
        {
            if (!(context is IInternalViewEntityContext bridge) || !(bridge.UnwrapViewEntity(entity) is GameObject target) || target == null)
                return OperationResult<IDisposable>.Failure(ModErrorCode.InvalidArgument, "The entity is not a live object owned by this runtime.");
            if (!UnityWorldScene.ContainsRoot(scene, target))
                return OperationResult<IDisposable>.Failure(ModErrorCode.InvalidArgument, "The entity is outside this world's scene.");
            return hiding.Hide(target);
        }
        public void Dispose()
        {
            if (disposed || disposing) return;
            disposing = true;
            // Leases restore before native resources disappear, even if another subscriber fails.
            var failures = new List<Exception>();
            foreach (Action handler in Stopped.GetInvocationList())
                try { handler(); } catch { failures.Add(new InvalidOperationException("A world view lease could not end.")); }
            disposed = true; Stopped = delegate { };
            if (sentinel != null)
            {
                sentinel.GetComponent<WorldViewLifetime>().End = null;
                UnityEngine.Object.Destroy(sentinel);
            }
            try { hiding.Dispose(); } finally { fog.Dispose(); }
            if (failures.Count > 0) throw new AggregateException(failures);
        }
    }
    internal sealed class WorldViewLifetime : MonoBehaviour
    {
        internal Action? End;
        private void OnDestroy() { var end = End; End = null; end?.Invoke(); }
    }
}
