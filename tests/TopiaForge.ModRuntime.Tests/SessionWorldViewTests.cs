using System;
using System.Collections.Generic;
using System.Linq;
using TopiaForge.ModManager;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using FakeEntity = TopiaForge.Mods.Testing.FakeEntity;

namespace TopiaForge.ModRuntime.Tests
{
    internal static class SessionWorldViewTests
    {
        private static int checks;
        internal static void Run()
        {
            UnityMainThreadGuard.CaptureCurrentThread();
            checks = 0;
            Audiences(); TargetBounds(); Codec(); Delivery();
            Console.WriteLine("Per-player world view passed: " + checks + " assertions (three runtimes, privacy, credits, generations, references and cleanup).");
        }
        private static void Check(bool value, string reason)
        { checks++; if (!value) throw new InvalidOperationException("Per-player view: " + reason); }
        private static IViewLease Lease(OperationResult<IViewLease> result)
        { Check(result.Succeeded, result.ErrorMessage); return result.Value!; }
        private static void Audiences()
        {
            using var view = new FakeWorldView(new[] { "day", "night", "cloudy" }, "day");
            using var scope = new FakeModLifetime();
            var p1 = new ParticipantId("p1"); var p2 = new ParticipantId("p2");
            var one = view.ForPlayer(p1, scope); var two = view.ForPlayer(p2, scope);
            using var targeted = Lease(one.Environment("night"));
            using var common = Lease(view.Environment("cloudy"));
            Check(view.GetEnvironment(p1) == "night" && view.GetEnvironment(p2) == "cloudy", "targeted environment beats newer shared environment");
            using var shortFog = Lease(one.Fog(VisionFog.Blackout));
            using var broad = Lease(view.Fog(VisionFog.Radius(24)));
            Check(view.GetFog(p1)?.VisibleDistance == 6 && view.GetFog(p2)?.VisibleDistance == 24, "shortest applicable fog only");
            var entity = new FakeEntity("prop", "Prop", Vec3.Zero);
            using var hide = Lease(one.Hide(entity));
            Check(view.GetHiddenEntities(p1).Count == 1 && view.GetHiddenEntities(p2).Count == 0, "hide union stays within audience");
            shortFog.Dispose(); Check(view.GetFog(p1)?.VisibleDistance == 24, "release restores shared fog");
            scope.Dispose(); Check(view.GetEnvironment(p1) == "cloudy" && !targeted.IsActive && !hide.IsActive, "scope owns targeted leases");
            using var players = new FakePlayerDirectory();
            var remote = players.Add(p1, "Sam");
            var mutable = new List<IPlayer> { remote, remote };
            var captured = mutable.View(); mutable.Clear();
            using var group = Lease(captured.Fog(VisionFog.Blackout));
            Check(players.WorldView.GetFog(p1).HasValue && !players.WorldView.EffectiveFog.HasValue, "captured audience and duplicate suppression");
            var retained = remote.View;
            players.Remove(p1);
            Check(!group.IsActive && retained.Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.NotFound, "departure retires leases and retained controls");
            var replacement = players.Add(p1, "Sam");
            Check(!players.WorldView.GetFog(p1).HasValue, "reused identity does not inherit old view");
            players.IsAuthoritative = false;
            Check(replacement.View.Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.NotAuthoritative, "fake authority matches runtime");
            players.IsAuthoritative = true;
            var failed = new[] { replacement, remote }.View().Fog(VisionFog.Blackout);
            Check(!failed.Succeeded && !players.WorldView.GetFog(p1).HasValue, "failed audience call rolls back earlier leases");
        }
        private static void TargetBounds()
        {
            using var view = new FakeWorldView(); using var scope = new FakeModLifetime();
            var p1 = new ParticipantId("p1"); var p2 = new ParticipantId("p2");
            var first = view.ForPlayer(p1, scope); var second = view.ForPlayer(p2, scope);
            var succeeded = true;
            for (var i = 0; i < 1024; i++)
            {
                succeeded &= first.Hide(new FakeEntity("a" + i, "Object", Vec3.Zero)).Succeeded;
                succeeded &= second.Hide(new FakeEntity("b" + i, "Object", Vec3.Zero)).Succeeded;
            }
            Check(succeeded && view.GetHiddenEntities(p1).Count == 1024 && view.GetHiddenEntities(p2).Count == 1024,
                "each player has its own 1024-target allowance");
            var extra = new FakeEntity("extra", "Object", Vec3.Zero);
            Check(first.Hide(extra).ErrorCode == ModErrorCode.RateLimited, "targeted view refuses 1025th target");
            Check(view.Hide(extra).ErrorCode == ModErrorCode.RateLimited, "shared hide must fit every targeted effective view");
            scope.Dispose(); Check(view.Hide(extra).Succeeded, "scope cleanup releases hidden-target budget");
        }
        private static void Codec()
        {
            var targets = Enumerable.Range(0, 1024).Select(i => new ViewTarget(1, new string('s', 128), "e" + i)).ToArray();
            var snapshot = new ViewSnapshot("world", "night", VisionFog.Radius(12, FogTint.Haze), targets);
            var wire = new WorldViewWire(); ViewSnapshot? result = null;
            var steps = WorldViewWire.Steps(snapshot);
            Check(steps == 66, "1024 targets use 64 bounded chunks plus begin/end");
            for (var step = 0; step < steps; step++)
            {
                var bytes = WorldViewWire.Write(snapshot, step);
                Check(bytes.Length <= 20 * 1024 && wire.Read(bytes, out result), "bounded chunk accepted");
                if (step != steps - 1) Check(result == null, "partial baseline never applies");
            }
            Check(result != null && result.Same(snapshot), "baseline round trips exactly");
            Check(!wire.Read(WorldViewWire.Write(snapshot, steps - 1), out _), "end without begin rejected");
            var begin = WorldViewWire.Write(snapshot, 0);
            for (var i = 0; i < begin.Length; i++)
            { wire.Reset(); Check(!wire.Read(begin.Take(i).ToArray(), out _), "truncated header rejected"); }
            wire.Reset(); Check(!wire.Read(begin.Concat(new byte[] { 0 }).ToArray(), out _), "trailing data rejected");
            var huge = new ViewSnapshot("world", null, null, new ViewTarget[1025]);
            wire.Reset(); Check(!wire.Read(WorldViewWire.Write(huge, 0), out _), "1025 targets rejected before allocation");
            var invalid = new ViewSnapshot("world", new string('x', 65), null, Array.Empty<ViewTarget>());
            wire.Reset(); Check(!wire.Read(WorldViewWire.Write(invalid, 0), out _), "65-character preset rejected");
            wire.Reset(); Check(wire.Read(begin, out _) && wire.Read(begin, out _), "new baseline supersedes incomplete predecessor within the bound");
            var duplicate = new ViewSnapshot("world", null, null, new[] { targets[0], targets[0] });
            wire.Reset(); Check(wire.Read(WorldViewWire.Write(duplicate, 0), out _)
                && !wire.Read(WorldViewWire.Write(duplicate, 1), out _), "duplicate target rejected");
        }
        private static void Delivery()
        {
            var net = new LoopbackNetPair { HostWindowMessages = 1, HostWindowBytes = 20 * 1024 };
            using var host = new Peer(net.Host);
            using var one = new Peer(net.AddPlayer("p1"));
            using var two = new Peer(net.AddPlayer("p2"));
            net.StartHosting(); net.Join(one.Control); net.Join(two.Control);
            void Frames(int count = 6)
            { for (var i = 0; i < count; i++) { host.Frame(); one.Frame(); two.Frame(); } }
            Frames(); host.Activate("world"); one.Activate("world"); two.Activate("world");
            Authority(host, one);
            var traffic = new List<(ParticipantId To, byte[] Payload)>();
            host.Control.Channel(InternalSystemChannelKind.PerPlayer)!.Log = traffic;
            using var scope = new FakeModLifetime();
            var seam = host.Services.Bind("example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), scope);
            var targeted = seam.PlayerWorldView(host.Renderer, "p1");
            using var fog = Lease(targeted.Fog(VisionFog.Blackout));
            using var environment = Lease(targeted.Environment("night"));
            Frames();
            Check(one.Renderer.Fog.HasValue && one.Renderer.Environment == "night" && two.Renderer.Fog == null
                && two.Renderer.Environment == null && host.Renderer.Fog == null, "only addressed player applies private settings");
            var wire = new WorldViewWire();
            foreach (var packet in traffic.Where(p => p.To.Value == "p2"))
            { Check(wire.Read(packet.Payload, out var view), "other player's stream valid"); if (view != null) Check(view.Fog == null && view.Environment == null, "other link contains no private view"); }
            Check(targeted.Hide(new FakeEntity("native", "Native", Vec3.Zero)).ErrorCode == ModErrorCode.InvalidArgument, "unnameable remote target refused");
            var authored = host.Binding.Add("prop", 1);
            var spawn = host.Binding.Add("spawn", 2);
            var avatar = host.Binding.Add("avatar", 2);
            using var hideMap = Lease(targeted.Hide(authored));
            using var hideSpawn = Lease(targeted.Hide(spawn));
            using var hidePlayer = Lease(targeted.Hide(avatar));
            using var localHide = Lease(seam.PlayerWorldView(host.Renderer, "host").Hide(avatar));
            var newHostAvatar = host.Binding.Add("avatar", 2); Frames();
            Check(!host.Renderer.Hidden.Contains(avatar) && host.Renderer.Hidden.Contains(newHostAvatar), "host local view follows replacement entity");
            localHide.Dispose();
            Frames(); Check(one.Renderer.Hidden.Count == 0, "unresolved targets wait without native work");
            one.Binding.Add("prop", 1); one.Binding.Add("spawn", 2); one.Binding.Add("avatar", 2);
            Frames(); Check(one.Renderer.Hidden.Count == 3 && two.Renderer.Hidden.Count == 0, "authored and replicated references arrive later");
            var oldAvatar = one.Binding.Targets["avatar"];
            one.Binding.Add("avatar", 2); Frames();
            Check(!one.Renderer.Hidden.Contains(oldAvatar) && one.Renderer.Hidden.Contains(one.Binding.Targets["avatar"]), "entity replacement rebinds stable reference");
            wire.Reset();
            foreach (var packet in traffic.Where(p => p.To.Value == "p2"))
            {
                Check(wire.Read(packet.Payload, out var view), "other recipient retains a valid independent stream");
                if (view != null) Check(view.Hidden.Length == 0 && view.Fog == null && view.Environment == null,
                    "other link contains no private target identifiers or effective values");
            }
            net.LoseLink(one.Control); Frames();
            Check(one.Renderer.Fog == null && one.Renderer.Hidden.Count == 0 && fog.IsActive, "stream end clears local view but grace retains host lease");
            net.Restore(one.Control); Frames(14);
            Check(one.Renderer.Fog.HasValue && one.Renderer.Hidden.Count == 3, "reconnect generation gets complete private baseline");
            net.Rejoin(one.Control); Frames(14);
            Check(one.Renderer.Fog.HasValue, "rejoin stream baselines unchanged state");
            net.ReplaceSession(); Frames(14);
            Check(one.Renderer.Fog.HasValue && two.Renderer.Fog == null, "session stream replacement baselines each private view");
            // Re-enter as a late player while a shared effect is active.
            net.Leave(two.Control); Frames();
            using var shared = Lease(seam.WorldView(host.Renderer).Fog(VisionFog.Radius(24)));
            net.Join(two.Control); Frames(14);
            Check(two.Renderer.Fog?.VisibleDistance == 24 && one.Renderer.Fog?.VisibleDistance == 6, "late join gets its effective shared view without another player's restriction");
            shared.Dispose(); Frames();
            // End a scope while one receiver cannot return credits; the other receiver still progresses.
            one.Control.Stalled = true;
            using var changedFog = Lease(targeted.Fog(VisionFog.Radius(3)));
            using var otherFog = Lease(seam.PlayerWorldView(host.Renderer, "p2").Fog(VisionFog.Radius(24)));
            Frames();
            Check(two.Renderer.Fog?.VisibleDistance == 24, "unrelated receiver continues while first is stalled");
            Check(host.Control.Channel(InternalSystemChannelKind.PerPlayer)!.Outstanding(one.Control.Id).Messages <= 1,
                "sender stays within the one-message test credit window");
            scope.Dispose(); Frames();
            Check(two.Renderer.Fog == null, "scope end clears responsive receiver immediately");
            one.Control.Stalled = false; Frames(20);
            Check(one.Renderer.Fog == null && one.Renderer.Environment == null && one.Renderer.Hidden.Count == 0, "scope teardown converges after backpressure");
            using var freshScope = new FakeModLifetime();
            var fresh = host.Services.Bind("example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), freshScope);
            using var finalFog = Lease(fresh.PlayerWorldView(host.Renderer, "p1").Fog(VisionFog.Blackout));
            Frames(); net.LoseLink(one.Control); Frames(); net.ExpireGrace(one.Control); Frames();
            Check(!finalFog.IsActive, "grace expiry releases targeted lease");
            // Replace a world after Begin has consumed the only credit, before its End can be sent.
            using var partial = Lease(fresh.WorldView(host.Renderer).Fog(VisionFog.Blackout));
            host.Frame();
            host.Activate("next-world"); two.Activate("next-world"); Frames(14);
            Check(two.Renderer.Fog == null && two.Renderer.Hidden.Count == 0, "world replacement starts clean");
            Check(host.Errors.Count == 0 && one.Errors.Count == 0 && two.Errors.Count == 0, "runtime stages report no failures");
        }
        private static void Authority(Peer host, Peer client)
        {
            using var scope = new FakeModLifetime();
            var clientSeam = client.Services.Bind("example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), scope);
            var controls = clientSeam.WorldView(client.Renderer);
            using var shadow = Lease(controls.Fog(VisionFog.Blackout));
            Check(shadow.IsActive && client.Renderer.Fog == null, "joined scripted call creates a native no-op shadow");
            using (client.Runtime.Intents.EnterUiActivation("example.mode"))
                Check(controls.Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.NotAuthoritative, "joined player intent cannot alter its view");
            foreach (var peer in new[] { host, client })
            {
                var participant = peer.Services.Bind("example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Participant), scope);
                Check(participant.PlayerWorldView(peer.Renderer, "p1").Fog(VisionFog.Blackout).ErrorCode == ModErrorCode.NotAuthoritative,
                    "participant cannot alter a view in either network role");
            }
            var server = host.Services.Bind("example.server", new ModNetProfile(ModNetworkClass.ServerOnly, ModExecutionContext.Process), scope);
            using var permitted = Lease(server.PlayerWorldView(host.Renderer, "p1").Fog(VisionFog.Radius(32)));
            scope.Dispose(); Check(!shadow.IsActive && !permitted.IsActive, "both shadow and host view end with their exact scope");
        }
        private sealed class Peer : IDisposable, IMultiplayerLog
        {
            internal readonly LoopbackControl Control;
            internal readonly MultiplayerRuntime Runtime;
            internal readonly MultiplayerReplication Services;
            internal readonly List<string> Errors = new List<string>();
            internal Renderer Renderer = new Renderer();
            internal Binding Binding = null!;
            internal Peer(LoopbackControl control)
            {
                Control = control; Runtime = new MultiplayerRuntime(() => control, new MultiplayerRuntimeHost("2545", "0.2.0", this), this);
                Services = new MultiplayerReplication(Runtime); Check(Runtime.Connect(), "runtime attached");
            }
            internal void Activate(string world)
            {
                Renderer.Dispose(); Renderer = new Renderer(); Binding = new Binding(world, Renderer);
                Services.References = Binding; Services.Views.Activate(Binding);
            }
            internal void Frame() { Runtime.BeforeModsUpdate(.1f); Runtime.AfterModsUpdate(.1f); }
            public void Info(string message) { }
            public void Warn(string message) => Errors.Add(message);
            public void Error(string message) => Errors.Add(message);
            public void Dispose() { Renderer.Dispose(); Runtime.Disconnect(); }
        }
        private sealed class Binding : IInternalWorldViewBinding, IMultiplayerReferenceSource
        {
            internal readonly Dictionary<string, IEntity> Targets = new Dictionary<string, IEntity>();
            private readonly Dictionary<string, byte> kinds = new Dictionary<string, byte>();
            internal Binding(string world, Renderer renderer) { WorldId = world; Renderer = renderer; }
            public string WorldId { get; }
            public IInternalWorldViewRenderer Renderer { get; }
            internal IEntity Add(string id, byte kind)
            { var entity = new FakeEntity(id, "Object", Vec3.Zero); Targets[id] = entity; kinds[id] = kind; return entity; }
            public bool TryNameAuthored(IEntity entity, out string scene, out string id)
            { scene = "scene"; id = entity.Id; return kinds.TryGetValue(id, out var kind) && kind == 1 && ReferenceEquals(Targets[id], entity); }
            public IEntity? ResolveAuthored(string scene, string id) => scene == "scene" && Targets.TryGetValue(id, out var entity) ? entity : null;
            public bool TryCreateRef(IEntity entity, out InternalNetRef reference)
            {
                reference = default;
                if (!kinds.TryGetValue(entity.Id, out var kind) || kind == 1 || !ReferenceEquals(Targets[entity.Id], entity)) return false;
                reference = new InternalNetRef(kind == 3 ? InternalNetRefKind.Player : InternalNetRefKind.Entity, entity.Id); return true;
            }
            public bool TryResolveRef(InternalNetRef reference, out IEntity? entity) => Targets.TryGetValue(reference.Value!, out entity);
        }
        private sealed class Renderer : IInternalWorldViewRenderer
        {
            internal string? Environment;
            internal VisionFog? Fog;
            internal readonly HashSet<IEntity> Hidden = new HashSet<IEntity>();
            public bool IsAlive { get; private set; } = true;
            public bool EnvironmentAvailable => true;
            public bool FogAvailable => true;
            public IReadOnlyList<string> Presets => new[] { "day", "night" };
            public event Action Stopped = delegate { };
            public bool HasPreset(string key) => key == "day" || key == "night";
            public OperationResult<bool> SetEnvironment(string? value) { Environment = value; return OperationResult<bool>.Success(true); }
            public OperationResult<bool> SetFog(VisionFog? value) { Fog = value; return OperationResult<bool>.Success(true); }
            public OperationResult<IDisposable> Hide(IEntity entity)
            { Hidden.Add(entity); return OperationResult<IDisposable>.Success(new Hold(() => Hidden.Remove(entity))); }
            public void Dispose() { if (!IsAlive) return; Stopped(); IsAlive = false; }
        }
        private sealed class Hold : IDisposable
        {
            private Action? end;
            internal Hold(Action end) { this.end = end; }
            public void Dispose() { var action = end; end = null; action?.Invoke(); }
        }
    }
}
