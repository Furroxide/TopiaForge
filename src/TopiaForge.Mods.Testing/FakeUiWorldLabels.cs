using System;
using System.Collections.Generic;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic local labels, with the same owner limit and lifetime state as the runtime.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class FakeUiWorldLabels : IUiWorldLabels, IDisposable
    {
        private readonly FakeModLifetime lifetime;
        private readonly Func<bool> gameplay;
        private readonly Func<int> scene;
        private readonly List<FakeUiWorldLabel> labels = new List<FakeUiWorldLabel>();
        private bool disposed;
        internal FakeUiWorldLabels(FakeModLifetime lifetime, Func<bool> gameplay, Func<int> scene)
        { this.lifetime = lifetime; this.gameplay = gameplay; this.scene = scene; }
        /// <summary>Gets the live label count, pruning destroyed entities first.</summary>
        public int Count { get { Prune(); return labels.Count; } }
        /// <summary>Gets captured handles. Mutating one scope never changes another scope's labels.</summary>
        public IReadOnlyList<FakeUiWorldLabel> Active { get { Prune(); return labels.AsReadOnly(); } }
        /// <inheritdoc/>
        public OperationResult<IUiWorldLabel> Add(UiWorldLabelRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (disposed || lifetime.IsStopping) return Fail(ModErrorCode.Cancelled, "The label owner is stopping.");
            if (!gameplay()) return Fail(ModErrorCode.InvalidState, "Enter a world before placing a world label.");
            if (!UiLabelState.EntityAlive(request.Entity)) return Fail(ModErrorCode.NotFound, "The label's entity no longer exists.");
            Prune();
            if (labels.Count >= UiLabelState.MaximumLabels) return Fail(ModErrorCode.Unavailable, "This UI scope already has 64 world labels. Release a label before adding another.");
            var label = new FakeUiWorldLabel(request, scene(), gameplay, value => labels.Remove(value));
            labels.Add(label); return OperationResult<IUiWorldLabel>.Success(label);
        }
        internal void Prune()
        { for (var index = labels.Count - 1; index >= 0; index--) if (!labels[index].IsAlive) labels[index].Dispose(); }
        internal void Unload(int instanceId)
        { for (var index = labels.Count - 1; index >= 0; index--) if (labels[index].Scene == instanceId) labels[index].Dispose(); }
        /// <inheritdoc/>
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            while (labels.Count > 0) labels[labels.Count - 1].Dispose();
        }
        private static OperationResult<IUiWorldLabel> Fail(ModErrorCode code, string text) => OperationResult<IUiWorldLabel>.Failure(code, text);
    }

    /// <summary>Inspectable label state. Occlusion is supplied by the test; projection is native-only.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class FakeUiWorldLabel : IUiWorldLabel
    {
        private readonly UiLabelState state;
        private readonly Func<bool> gameplay;
        private readonly float maximumDistance;
        private readonly bool useOcclusion;
        internal FakeUiWorldLabel(UiWorldLabelRequest request, int scene, Func<bool> gameplay, Action<FakeUiWorldLabel> release)
        {
            Scene = scene; this.gameplay = gameplay; maximumDistance = request.MaximumDistance; useOcclusion = request.Occlusion;
            state = new UiLabelState(request, _ => { }, () => release(this));
        }
        internal int Scene { get; }
        /// <summary>Gets or sets whether the test's geometry blocks this label.</summary>
        public bool Occluded { get; set; }
        /// <summary>Gets the last accepted text.</summary>
        public string Text => state.Text;
        /// <inheritdoc/>
        public bool IsAlive => state.IsAlive;
        /// <summary>Reads its current world point, following the entity until detached.</summary>
        public bool TryGetPosition(out Vec3 position) => state.TryPosition(out position);
        /// <summary>Computes the distance fade; offscreen and camera projection checks remain native.</summary>
        public float OpacityFrom(Vec3 camera)
        {
            if (!camera.IsFinite || !gameplay() || (useOcclusion && Occluded) || !state.TryPosition(out var position)) return 0;
            var x = position.X - camera.X; var y = position.Y - camera.Y; var z = position.Z - camera.Z;
            var distance = (float)Math.Sqrt(x * x + y * y + z * z);
            if (distance <= 0 || distance >= maximumDistance) return 0;
            return Math.Min(1, (maximumDistance - distance) / Math.Min(5, maximumDistance / 3));
        }
        /// <inheritdoc/>
        public void SetText(string text) => state.SetText(text);
        /// <inheritdoc/>
        public void SetPosition(Vec3 position) => state.SetPosition(position);
        /// <inheritdoc/>
        public void Dispose() => state.Dispose();
    }
}
