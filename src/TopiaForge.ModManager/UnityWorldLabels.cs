using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.UnityUi;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace TopiaForge.ModManager
{
    internal sealed class UnityWorldLabels : IUiWorldLabels, IDisposable
    {
        private readonly UiHost host;
        private readonly IModLifetime lifetime;
        private readonly UiGameplayGate gameplay;
        private readonly UnityEntityRegistry entities;
        private readonly Binding?[] slots = new Binding?[UiLabelState.MaximumLabels];
        private readonly RaycastHit[] hits = new RaycastHit[32];
        private TopiaForgeHudLayer? hud;
        private TopiaForgeNametagLayer? layer;
        private bool disposed;
        internal UnityWorldLabels(UiHost host, IModLifetime lifetime, UiGameplayGate gameplay, UnityEntityRegistry entities)
        {
            this.host = host; this.lifetime = lifetime; this.gameplay = gameplay; this.entities = entities;
            if (!lifetime.IsStopping) host.LayerLost += Lost;
        }
        public OperationResult<IUiWorldLabel> Add(UiWorldLabelRequest request)
        {
            UnityMainThreadGuard.AssertCurrent();
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (disposed || lifetime.IsStopping) return Failure(ModErrorCode.Cancelled, "The label owner is stopping.");
            if (!gameplay.IsGameplay) return Failure(ModErrorCode.InvalidState, "Enter a world before placing a world label.");
            if (!UiLabelState.EntityAlive(request.Entity)) return Failure(ModErrorCode.NotFound, "The label's entity no longer exists.");
            Tick();
            var index = Array.FindIndex(slots, item => item == null);
            if (index < 0) return Failure(ModErrorCode.Unavailable, "This UI scope already has 64 world labels. Release a label before adding another.");
            try
            {
                EnsureLayer();
                var scene = request.Entity != null && entities.TryGetGameObject(request.Entity, out var entityObject)
                    ? entityObject.scene : SceneManager.GetActiveScene();
                var binding = new Binding(this, index, request, scene);
                slots[index] = binding;
                if (!binding.Attach(layer!)) { binding.State.Dispose(); return Failure(ModErrorCode.Unavailable, "The world-label view is unavailable. Try again."); }
                return OperationResult<IUiWorldLabel>.Success(binding.State);
            }
            catch (Exception)
            {
                slots[index]?.State.Dispose();
                return Failure(ModErrorCode.External, "The world label could not be shown. Try again after the scene finishes loading.");
            }
        }
        private void EnsureLayer()
        {
            if (layer != null && layer.Go != null) return;
            hud = host.HudLayer("world-labels-" + Guid.NewGuid().ToString("N"), persistent: true);
            layer = hud.Nametags(UiLabelState.MaximumLabels);
            hud.Go.AddComponent<WorldLabelDriver>().Owner = this;
        }
        internal void Tick()
        {
            if (disposed) return;
            for (var index = 0; index < slots.Length; index++)
            {
                var item = slots[index];
                if (item == null) continue;
                if (!item.State.IsAlive || !item.Scene.isLoaded) item.State.Dispose();
            }
        }
        private void Release(int index, Binding binding)
        {
            if (!ReferenceEquals(slots[index], binding)) return;
            slots[index] = null; binding.Tag?.Remove(); binding.Tag = null;
        }
        private void Lost(GameObject root)
        {
            if (disposed || lifetime.IsStopping || hud == null || !ReferenceEquals(hud.Go, root)) return;
            layer = null; hud = null;
            try
            {
                Tick(); EnsureLayer();
                foreach (var item in slots) if (item != null && !item.Attach(layer!)) item.State.Dispose();
            }
            catch (Exception) { foreach (var item in slots) item?.State.Dispose(); }
        }
        private bool ClearLine(Binding item, Vec3 camera, Vec3 position)
        {
            if (!gameplay.IsGameplay || !item.State.IsAlive) return false;
            if (!item.Occlusion) return true;
            var start = new Vector3(camera.X, camera.Y, camera.Z);
            var end = new Vector3(position.X, position.Y, position.Z);
            var delta = end - start;
            var distance = delta.magnitude;
            if (distance <= 0.1f) return true;
            GameObject? own = null;
            if (item.State.Entity != null && entities.TryGetGameObject(item.State.Entity, out var gameObject)) own = gameObject;
            var count = UnityEngine.Physics.RaycastNonAlloc(start, delta / distance, hits, distance - 0.05f,
                UnityEngine.Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            if (count == hits.Length) return false;
            for (var index = 0; index < count; index++)
            {
                var hit = hits[index].collider;
                if (hit != null && (own == null || !hit.transform.IsChildOf(own.transform))) return false;
            }
            return true;
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true; host.LayerLost -= Lost;
            foreach (var item in slots) item?.State.Dispose();
            hud?.Destroy(); hud = null; layer = null;
        }
        private static OperationResult<IUiWorldLabel> Failure(ModErrorCode code, string message) =>
            OperationResult<IUiWorldLabel>.Failure(code, message);

        private sealed class Binding : ITopiaForgeNametagAnchor, ITopiaForgeNametagPresentation
        {
            private readonly UnityWorldLabels owner;
            private readonly UiTone tone;
            internal Binding(UnityWorldLabels owner, int index, UiWorldLabelRequest request, Scene scene)
            {
                this.owner = owner; Scene = scene; Occlusion = request.Occlusion; tone = request.Tone;
                Falloff = new TopiaForgeNametagFalloff(0, 0, request.MaximumDistance, Math.Min(5, request.MaximumDistance / 3));
                State = new UiLabelState(request, text => Tag?.SetName(text), () => owner.Release(index, this), () => UnityMainThreadGuard.AssertCurrent());
            }
            internal readonly UiLabelState State;
            internal readonly Scene Scene;
            internal readonly bool Occlusion;
            internal TopiaForgeNametag? Tag;
            public TopiaForgeNametagFalloff Falloff { get; }
            internal bool Attach(TopiaForgeNametagLayer target)
            {
                Tag = target.Track(this, State.Text, State.Entity == null ? 0 : TopiaForgeNametagLayer.DefaultVerticalOffset);
                Tag?.SetTone(tone == UiTone.Success ? TopiaForgeTone.Success : tone == UiTone.Warning ? TopiaForgeTone.Warning
                    : tone == UiTone.Danger ? TopiaForgeTone.Danger : TopiaForgeTone.Neutral);
                return Tag != null;
            }
            public bool TryGetPosition(out Vec3 position)
            {
                Tag?.SetVerticalOffset(State.Entity == null ? 0 : TopiaForgeNametagLayer.DefaultVerticalOffset);
                return State.TryPosition(out position);
            }
            public bool CanShow(Vec3 cameraPosition, Vec3 anchorPosition) => owner.ClearLine(this, cameraPosition, anchorPosition);
        }
    }
    internal sealed class WorldLabelDriver : MonoBehaviour
    {
        internal UnityWorldLabels? Owner;
        private void LateUpdate() => Owner?.Tick();
        private void OnDestroy() { Owner = null; }
    }
}
