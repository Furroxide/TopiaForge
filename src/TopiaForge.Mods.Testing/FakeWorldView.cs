using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods.Testing
{
    /// <summary>A single-player view fake using the runtime's lease derivation and lifetime rules.</summary>
    [RequiresTopiaForge("0.2.0")]
    public sealed class FakeWorldView : IWorldView, IDisposable
    {
        private readonly Renderer renderer;
        private readonly WorldViewLeases leases;
        private readonly FakeModLifetime lifetime = new FakeModLifetime();
        private readonly IViewControls controls;
        /// <summary>Creates a view with explicitly supplied synthetic presets and its world's original preset.</summary>
        [RequiresTopiaForge("0.2.0")]
        public FakeWorldView(IEnumerable<string>? presets = null, string originalEnvironment = "")
        {
            var keys = (presets ?? Array.Empty<string>()).Distinct(StringComparer.Ordinal).ToArray();
            if (keys.Length > 256 || keys.Any(k => !TopiaForge.ModManager.Core.WorldViewLedger<IEntity>.ValidPreset(k)))
                throw new ArgumentException("Supply at most 256 valid preset keys.", nameof(presets));
            renderer = new Renderer(keys, originalEnvironment ?? throw new ArgumentNullException(nameof(originalEnvironment)));
            leases = new WorldViewLeases(renderer);
            controls = leases.Bind(lifetime, () => ModErrorCode.None, () => { });
        }
        /// <summary>Gets the effective environment, or the world's original when no environment lease remains.</summary>
        [RequiresTopiaForge("0.2.0")]
        public string EffectiveEnvironment => renderer.Environment;
        /// <summary>Gets the most restrictive fog, or null without fog leases.</summary>
        [RequiresTopiaForge("0.2.0")]
        public VisionFog? EffectiveFog => renderer.Fog;
        /// <summary>Gets the entities currently hidden by at least one lease.</summary>
        [RequiresTopiaForge("0.2.0")]
        public IReadOnlyList<IEntity> HiddenEntities => renderer.Hidden.ToArray();
        /// <summary>Gets or sets whether this fake renderer can draw fog.</summary>
        [RequiresTopiaForge("0.2.0")]
        public bool SupportsFog { get => renderer.FogAvailable; set => renderer.FogAvailable = value; }
        /// <inheritdoc/>
        [RequiresTopiaForge("0.2.0")]
        public bool IsAvailable => renderer.IsAlive;
        /// <inheritdoc/>
        [RequiresTopiaForge("0.2.0")]
        public IReadOnlyList<string> EnvironmentPresets => renderer.Presets;
        /// <summary>Binds this same view to a caller's scope. Participants are refused in every role; delivery currently requires single player.</summary>
        [RequiresTopiaForge("0.2.0")]
        public IWorldView ForScope(FakeModLifetime scope, bool participant = false, GamemodeSessionRole role = GamemodeSessionRole.SinglePlayer)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (role < GamemodeSessionRole.SinglePlayer || role > GamemodeSessionRole.Client) throw new ArgumentOutOfRangeException(nameof(role));
            return new Scoped(this, scope, leases.Bind(scope, () => participant ? ModErrorCode.NotAuthoritative
                : role == GamemodeSessionRole.SinglePlayer ? ModErrorCode.None : ModErrorCode.Unavailable, () => { }), participant, role);
        }
        /// <inheritdoc/>
        [RequiresTopiaForge("0.2.0")]
        public OperationResult<IViewLease> Environment(string preset) => controls.Environment(preset);
        /// <inheritdoc/>
        [RequiresTopiaForge("0.2.0")]
        public OperationResult<IViewLease> Fog(VisionFog fog) => controls.Fog(fog);
        /// <inheritdoc/>
        [RequiresTopiaForge("0.2.0")]
        public OperationResult<IViewLease> Hide(IEntity entity) => controls.Hide(entity);
        /// <summary>Ends the world, restoring every view and invalidating every lease.</summary>
        [RequiresTopiaForge("0.2.0")]
        public void Dispose() { try { renderer.Dispose(); } finally { lifetime.Dispose(); } }
        private sealed class Scoped : IWorldView
        {
            private readonly FakeWorldView owner;
            private readonly FakeModLifetime lifetime;
            private readonly IViewControls controls;
            private readonly bool permitted;
            internal Scoped(FakeWorldView owner, FakeModLifetime lifetime, IViewControls controls, bool participant, GamemodeSessionRole role)
            { this.owner = owner; this.lifetime = lifetime; this.controls = controls; permitted = !participant && role == GamemodeSessionRole.SinglePlayer; }
            public bool IsAvailable => permitted && !lifetime.IsStopping && owner.IsAvailable;
            public IReadOnlyList<string> EnvironmentPresets => owner.EnvironmentPresets;
            public OperationResult<IViewLease> Environment(string preset) => controls.Environment(preset);
            public OperationResult<IViewLease> Fog(VisionFog fog) => controls.Fog(fog);
            public OperationResult<IViewLease> Hide(IEntity entity) => controls.Hide(entity);
        }
        private sealed class Renderer : IInternalWorldViewRenderer
        {
            internal readonly HashSet<IEntity> Hidden = new HashSet<IEntity>();
            private readonly string original;
            internal string Environment;
            internal VisionFog? Fog;
            internal Renderer(string[] presets, string original)
            { Presets = Array.AsReadOnly(presets); this.original = original; Environment = original; }
            public bool IsAlive { get; private set; } = true;
            public bool FogAvailable { get; set; } = true;
            public bool EnvironmentAvailable => Presets.Count > 0;
            public IReadOnlyList<string> Presets { get; }
            public event Action Stopped = delegate { };
            public bool HasPreset(string preset) => Presets.Contains(preset, StringComparer.Ordinal);
            public OperationResult<bool> SetEnvironment(string? preset) { Environment = preset ?? original; return OperationResult<bool>.Success(true); }
            public OperationResult<bool> SetFog(VisionFog? fog) { Fog = fog; return OperationResult<bool>.Success(true); }
            public OperationResult<IDisposable> Hide(IEntity entity)
            { Hidden.Add(entity); return OperationResult<IDisposable>.Success(new End(() => Hidden.Remove(entity))); }
            public void Dispose()
            {
                if (!IsAlive) return;
                try { Stopped(); } finally { IsAlive = false; Stopped = delegate { }; Hidden.Clear(); Environment = original; Fog = null; }
            }
        }
        private sealed class End : IDisposable
        {
            private Action? action;
            internal End(Action action) { this.action = action; }
            public void Dispose() { var end = action; action = null; end?.Invoke(); }
        }
    }
}
