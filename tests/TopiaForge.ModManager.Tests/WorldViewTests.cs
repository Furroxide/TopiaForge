using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class WorldViewTests
    {
        private static int assertions;
        internal static void Run()
        {
            assertions = 0;
            Values(); Derivation(); LifetimeAndAuthority(); Bounds(); RendererOwnership(); FailureRollback(); Runner(); Provider(); Selection();
            Console.WriteLine("World view passed: " + assertions + " assertions (production leases, runtime seam, fakes and renderer ownership).");
        }
        private static void Check(bool value, string reason) { assertions++; if (!value) throw new InvalidOperationException("World view: " + reason); }
        private static IViewLease Lease(OperationResult<IViewLease> result) { Check(result.Succeeded, result.ErrorMessage); return result.Value!; }
        private static void Values()
        {
            foreach (var distance in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity, 0, .99f, 500.01f })
            {
                var failed = false; try { VisionFog.Radius(distance); } catch (ArgumentOutOfRangeException) { failed = true; }
                Check(failed, "invalid fog distance refused");
            }
            Check(VisionFog.Radius(1).VisibleDistance == 1 && VisionFog.Radius(500).VisibleDistance == 500, "inclusive distance bounds");
            Check(VisionFog.Blackout.Equals(VisionFog.Radius(6)) && VisionFog.Blackout.GetHashCode() == VisionFog.Radius(6).GetHashCode(), "six-metre default and equality");
            var invalidTint = false; try { VisionFog.Radius(10, (FogTint)99); } catch (ArgumentOutOfRangeException) { invalidTint = true; }
            Check(invalidTint, "undefined tint refused");
            using var view = new FakeWorldView();
            Check(view.Fog(default).ErrorCode == ModErrorCode.InvalidArgument, "default struct cannot create zero fog");
            Check(view.Hide((IEntity)null!).ErrorCode == ModErrorCode.InvalidArgument, "null target refused");
            Check(view.Environment("night").ErrorCode == ModErrorCode.Unavailable, "world without presets");
            view.SupportsFog = false;
            Check(view.Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.Unavailable, "unsupported fog is explicit");
        }
        private static void Derivation()
        {
            using var view = new FakeWorldView(new[] { "day", "night", "overcast" }, "day");
            using var first = new FakeModLifetime(); using var second = new FakeModLifetime();
            var a = view.ForScope(first); var b = view.ForScope(second);
            var restrictive = Lease(a.Fog(VisionFog.Radius(6, FogTint.Smoke)));
            var broad = Lease(b.Fog(VisionFog.Radius(24)));
            Check(view.EffectiveFog!.Value.VisibleDistance == 6, "later broad fog does not reveal a restrictive view");
            var tie = Lease(b.Fog(VisionFog.Radius(6)));
            Check(view.EffectiveFog.Value.Tint == FogTint.Dark, "equal-distance tint is deterministic");
            tie.Dispose(); restrictive.Dispose();
            Check(view.EffectiveFog.Value.VisibleDistance == 24, "releasing restrictive fog restores the next");
            broad.Dispose(); Check(view.EffectiveFog == null, "last fog release restores baseline");
            var night = Lease(a.Environment("night")); var cloudy = Lease(b.Environment("overcast"));
            Check(view.EffectiveEnvironment == "overcast", "newest environment wins");
            cloudy.Dispose(); Check(view.EffectiveEnvironment == "night", "environment stack restores previous");
            var target = new FakeEntity("object", "Object", Vec3.Zero);
            var hideA = Lease(a.Hide(target)); var hideB = Lease(b.Hide(target));
            hideA.Dispose(); Check(view.HiddenEntities.Count == 1, "hide union survives one release");
            hideB.Dispose(); hideB.Dispose(); Check(view.HiddenEntities.Count == 0, "last hide release and idempotence");
            first.Dispose(); Check(!night.IsActive && view.EffectiveEnvironment == "day", "scope end restores map original");
            Check(first.TrackedResourceCount == 0 && second.TrackedResourceCount == 0, "released leases untrack scope resources");
            Check(view.Environment("nite").ErrorCode == ModErrorCode.NotFound && view.Environment("nite").ErrorMessage.Contains("night", StringComparison.Ordinal), "unknown preset lists bounded choices");
            foreach (var key in new[] { "", " ", new string('x', 65), "bad\nkey" })
                Check(view.Environment(key).ErrorCode == ModErrorCode.InvalidArgument, "invalid preset key");
        }
        private static void LifetimeAndAuthority()
        {
            var services = MultiplayerReplication.Offline();
            using var backend = new Renderer();
            foreach (ModNetworkClass network in Enum.GetValues(typeof(ModNetworkClass)))
            {
                using var lifetime = new FakeModLifetime();
                var participant = new RuntimeReplication(services, "example.mode", new ModNetProfile(network, ModExecutionContext.Participant), lifetime).WorldView(backend);
                Check(participant.Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.NotAuthoritative, "runtime refuses offline participant for each network class");
                Check(participant.Environment("night").ErrorCode == ModErrorCode.NotAuthoritative, "environment authority precedes lookup");
                Check(participant.Hide(new FakeEntity("x", "x", Vec3.Zero)).ErrorCode == ModErrorCode.NotAuthoritative, "hide authority");
            }
            using var owner = new FakeModLifetime();
            var controls = new RuntimeReplication(services, "example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), owner).WorldView(backend);
            var fog = Lease(controls.Fog(VisionFog.Blackout));
            Check(backend.Fog.HasValue, "runtime seam applies immediately offline");
            services.Views.RealmChanged(NetRealm.Offline, NetRealm.ListenHost);
            Check(!fog.IsActive && backend.Fog == null && owner.TrackedResourceCount == 0, "realm promotion clears offline-only state");
            using var view = new FakeWorldView();
            foreach (GamemodeSessionRole role in Enum.GetValues(typeof(GamemodeSessionRole)))
                Check(view.ForScope(owner, true, role).Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.NotAuthoritative, "fake participant forbidden in every role");
            Check(view.ForScope(owner, role: GamemodeSessionRole.Host).Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.Unavailable, "multiplayer delivery is not pretended");
            var active = Lease(view.ForScope(owner).Fog(VisionFog.Blackout));
            view.Dispose(); Check(!active.IsActive && owner.TrackedResourceCount == 0, "world end releases caller tracking");
            Check(view.Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.Cancelled, "stopped world refuses new work");
            using var another = new FakeWorldView(); owner.Dispose();
            Check(another.ForScope(owner).Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.Cancelled, "stopping scope refused");
        }
        private static void Bounds()
        {
            using var view = new FakeWorldView();
            var held = new List<IViewLease>();
            for (var i = 0; i < 1024; i++) held.Add(Lease(view.Hide(new FakeEntity("target" + i, "Object", Vec3.Zero))));
            Check(view.HiddenEntities.Count == 1024, "hidden bound inclusive");
            Check(view.Hide(new FakeEntity("overflow", "Object", Vec3.Zero)).ErrorCode == ModErrorCode.RateLimited, "1,025th target refused");
            using var same = Lease(view.Hide(view.HiddenEntities[0]));
            foreach (var item in held) item.Dispose();
            Check(view.HiddenEntities.Count == 1, "duplicate target does not spend a hidden slot");
            same.Dispose();
            for (var i = 0; i < 4096; i++) held.Add(Lease(view.Fog(VisionFog.Radius(12))));
            Check(view.Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.RateLimited, "total lease bound");
            foreach (var item in held) item.Dispose();
            Check(view.EffectiveFog == null, "bounded lease cleanup complete");
        }
        private sealed class Visible { internal bool Enabled; internal bool Alive = true; }
        private static void RendererOwnership()
        {
            using var visibility = new OwnedRendererVisibility<Visible>(r => r.Alive, r => r.Enabled, (r, v) => r.Enabled = v);
            var target = new Visible { Enabled = true };
            var a = visibility.Hide(new[] { target }); var b = visibility.Hide(new[] { target });
            Check(!target.Enabled, "own renderer disabled");
            a.Dispose(); Check(!target.Enabled, "overlapping targets preserve hide");
            b.Dispose(); Check(target.Enabled, "last renderer hold restores original");
            target.Enabled = false;
            a = visibility.Hide(new[] { target }); target.Enabled = true;
            a.Dispose(); Check(target.Enabled, "restore does not overwrite an external renderer change");
            target.Enabled = false; a = visibility.Hide(new[] { target }); a.Dispose();
            Check(!target.Enabled, "initially disabled renderer remains disabled");
            target.Alive = false; using var dead = visibility.Hide(new[] { target });
            Check(!target.Enabled, "destroyed renderer ignored");
        }
        private static void FailureRollback()
        {
            using var renderer = new Renderer(); using var scope = new FakeModLifetime(); using var leases = new WorldViewLeases(renderer);
            var controls = leases.Bind(scope, () => ModErrorCode.None, () => { });
            renderer.FailFogOnce = true;
            Check(!controls.Fog(VisionFog.Blackout).Succeeded && renderer.Fog == null && scope.TrackedResourceCount == 0, "partial native fog failure restores and leaves no lease");
            var good = Lease(controls.Fog(VisionFog.Radius(24)));
            renderer.FailFogOnce = true;
            Check(!controls.Fog(VisionFog.Blackout).Succeeded && renderer.Fog!.Value.VisibleDistance == 24, "failed stricter fog preserves prior live lease");
            renderer.ThrowFogOnce = true;
            Check(!controls.Fog(VisionFog.Blackout).Succeeded && renderer.Fog!.Value.VisibleDistance == 24, "throwing native fog setter compensates its partial write");
            renderer.Dispose(); Check(!good.IsActive && renderer.Fog == null, "renderer end clears lease state");
            using var destroyed = new Renderer(); using var vanished = new WorldViewLeases(destroyed);
            var last = Lease(vanished.Bind(scope, () => ModErrorCode.None, () => { }).Fog(VisionFog.Blackout));
            var cleanupFailed = false;
            try { destroyed.Kill(); } catch { cleanupFailed = true; }
            Check(!cleanupFailed && !last.IsActive && scope.TrackedResourceCount == 0, "native destruction clears leases without restoring a vanished renderer");
        }
        private sealed class Renderer : IInternalWorldViewRenderer
        {
            internal VisionFog? Fog; internal bool FailFogOnce, ThrowFogOnce;
            public bool IsAlive { get; private set; } = true;
            public bool FogAvailable => true;
            public bool EnvironmentAvailable => false;
            public IReadOnlyList<string> Presets => Array.Empty<string>();
            public event Action Stopped = delegate { };
            public bool HasPreset(string preset) => false;
            public OperationResult<bool> SetEnvironment(string? preset) => OperationResult<bool>.Success(true);
            public OperationResult<bool> SetFog(VisionFog? value)
            {
                if (!IsAlive) throw new InvalidOperationException("Synthetic destroyed native object.");
                Fog = value;
                if (ThrowFogOnce) { ThrowFogOnce = false; throw new InvalidOperationException("Synthetic partial write."); }
                if (FailFogOnce) { FailFogOnce = false; return OperationResult<bool>.Failure(ModErrorCode.External, "Synthetic renderer failure."); }
                return OperationResult<bool>.Success(true);
            }
            public OperationResult<IDisposable> Hide(IEntity entity) => OperationResult<IDisposable>.Failure(ModErrorCode.InvalidArgument, "Synthetic unsupported target.");
            private bool stopping;
            public void Dispose() { if (!IsAlive || stopping) return; stopping = true; Stopped(); IsAlive = false; }
            internal void Kill() { IsAlive = false; Stopped(); }
        }
    }
}
