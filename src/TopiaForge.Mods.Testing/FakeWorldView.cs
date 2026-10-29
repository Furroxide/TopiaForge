using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Mods.Testing
{
    /// <summary>A world view fake using the runtime's lease derivation and lifetime rules.</summary>
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
            leases = new WorldViewLeases(renderer, () => "local");
            controls = leases.Bind(lifetime, () => ModErrorCode.None, () => { });
        }
        /// <summary>Creates controls for one player with the supplied scope and authority.</summary>
        [RequiresTopiaForge("0.2.0")]
        public IViewControls ForPlayer(ParticipantId player, FakeModLifetime scope, bool authoritative = true)
        {
            if (!player.IsValid) throw new ArgumentException("A player is required.", nameof(player));
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            return leases.Bind(scope, () => authoritative ? ModErrorCode.None : ModErrorCode.NotAuthoritative, () => { }, player.Value);
        }
        /// <summary>Gets one player's derived environment, including shared leases.</summary>
        [RequiresTopiaForge("0.2.0")]
        public string GetEnvironment(ParticipantId player) => leases.EnvironmentFor(player.Value) ?? renderer.Original;
        /// <summary>Gets one player's most restrictive fog.</summary>
        [RequiresTopiaForge("0.2.0")]
        public VisionFog? GetFog(ParticipantId player) => leases.FogFor(player.Value);
        /// <summary>Gets one player's union of hidden entities.</summary>
        [RequiresTopiaForge("0.2.0")]
        public IReadOnlyList<IEntity> GetHiddenEntities(ParticipantId player) => leases.HiddenFor(player.Value);
        internal void RemovePlayer(ParticipantId player) => leases.RemovePlayer(player.Value);
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
        /// <summary>Binds this same view to a caller's scope. Participants are refused in every role; host changes derive the effective views.</summary>
        [RequiresTopiaForge("0.2.0")]
        public IWorldView ForScope(FakeModLifetime scope, bool participant = false, GamemodeSessionRole role = GamemodeSessionRole.SinglePlayer)
        {
            if (scope == null) throw new ArgumentNullException(nameof(scope));
            if (role < GamemodeSessionRole.SinglePlayer || role > GamemodeSessionRole.Client) throw new ArgumentOutOfRangeException(nameof(role));
            return new Scoped(this, scope, leases.Bind(scope, () => participant ? ModErrorCode.NotAuthoritative
                : role != GamemodeSessionRole.Client ? ModErrorCode.None : ModErrorCode.NotAuthoritative, () => { }), participant, role);
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
            private readonly bool shadow;
            internal Scoped(FakeWorldView owner, FakeModLifetime lifetime, IViewControls controls, bool participant, GamemodeSessionRole role)
            { this.owner = owner; this.lifetime = lifetime; this.controls = controls; permitted = !participant; shadow = !participant && role == GamemodeSessionRole.Client; }
            public bool IsAvailable => permitted && !lifetime.IsStopping && owner.IsAvailable;
            public IReadOnlyList<string> EnvironmentPresets => owner.EnvironmentPresets;
            public OperationResult<IViewLease> Environment(string preset) => shadow ? Shadow() : controls.Environment(preset);
            public OperationResult<IViewLease> Fog(VisionFog fog) => shadow ? Shadow() : controls.Fog(fog);
            public OperationResult<IViewLease> Hide(IEntity entity) => shadow ? Shadow() : controls.Hide(entity);
            private OperationResult<IViewLease> Shadow() => lifetime.IsStopping || !owner.IsAvailable
                ? OperationResult<IViewLease>.Failure(ModErrorCode.Cancelled, "The view's scope or world is stopping.")
                : OperationResult<IViewLease>.Success(new ShadowLease(lifetime, owner.renderer));
        }
        private sealed class ShadowLease : IViewLease
        {
            private readonly Renderer renderer;
            private IDisposable? tracking;
            private bool disposed;
            internal ShadowLease(FakeModLifetime lifetime, Renderer renderer)
            { this.renderer = renderer; tracking = lifetime.Track(this); renderer.Stopped += Dispose; }
            public bool IsActive => !disposed && renderer.IsAlive;
            public void Dispose()
            { if (disposed) return; disposed = true; renderer.Stopped -= Dispose; var hold = tracking; tracking = null; hold?.Dispose(); }
        }

        private sealed class Renderer : IInternalWorldViewRenderer
        {
            internal readonly HashSet<IEntity> Hidden = new HashSet<IEntity>();
            private readonly string original;
            internal string Original => original;
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
