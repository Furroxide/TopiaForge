using TopiaForge.ModManager;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using FakeEntity = TopiaForge.Mods.Testing.FakeEntity;
using TopiaForge.Multiplayer.Engine;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class WorldViewLifetimeRefreshTests
{
    internal static void Run()
    {
        UnityMainThreadGuard.CaptureCurrentThread();
        RunCache();
        Reconnect(false);
        Reconnect(true);
        Console.WriteLine("Engine lifetime world-view refresh and cached references passed.");
    }

    internal static void RunCache() { UnityMainThreadGuard.CaptureCurrentThread(); Reconnect(false, true); }

    private static void Reconnect(bool partialBaseline, bool cacheOnly = false)
    {
        using var engine = new EngineHarness(MultiplayerProcessKind.Interactive);
        var guest = engine.AddClient("subject");
        var watcher = engine.AddClient("watcher");
        engine.Flush();
        using var host = new Node(engine.Host, engine);
        using var subject = new Node(guest.Engine, engine);
        using var observer = new Node(watcher.Engine, engine);
        var nodes = new[] { host, subject, observer };
        void Frames(int count)
        {
            for (var frame = 0; frame < count; frame++)
            {
                engine.Advance();
                foreach (var node in nodes) node.Runtime.BeforeModsUpdate(1f / 60);
                foreach (var node in nodes) if (node.SendEnabled) node.Presence.Send.Run(1f / 60);
                foreach (var node in nodes) node.Presence.Avatars.Run(1f / 60);
                foreach (var node in nodes) { node.Control.Views!.Frame(); node.Services.Views.Run(.25f); }
            }
        }
        Frames(40);
        var original = Avatar(host, subject.Id);
        var watched = Avatar(observer, subject.Id);
        var originalLifetime = Row(engine.Host, subject.Id).LifetimeId;
        var made = host.Players.TryCreateReference(original, out var oldReference);
        Check(made && observer.Players.TryResolveReference(oldReference, out var remote) && ReferenceEquals(remote, watched),
            "canonical engine reference resolves the observer's exact current root");
        using var scope = new FakeModLifetime();
        var seam = host.Services.Bind("example.mode", new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.GamemodeServer), scope);
        using var local = Lease(seam.PlayerWorldView(host.Renderer, host.Id.Value).Hide(original));
        using var privateView = Lease(seam.PlayerWorldView(host.Renderer, observer.Id.Value).Hide(original));
        Frames(4);
        Check(host.Renderer.Hidden.Contains(original) && observer.Renderer.Hidden.Contains(watched), "one outer lease hides the current root on host and observer");
        var foreign = new FakeEntity(original.Id, "Foreign", Vec3.Zero);
        Check(!seam.PlayerWorldView(host.Renderer, observer.Id.Value).Hide(foreign).Succeeded,
            "same-ID foreign entity cannot borrow a player's canonical reference");

        if (cacheOnly)
        {
            Idle(host, observer, oldReference);
            // Actual replacement can be published before the consumer samples the intermediate lost-link roster.
            engine.Network.Sever(guest.Link); engine.Advance(3);
            engine.Join(subject.Engine, "subject"); engine.Flush();
            foreach (var node in nodes) node.Runtime.BeforeModsUpdate(1f / 60);
            Check(Row(engine.Host, subject.Id).LifetimeId != originalLifetime && !host.Players.TryResolveReference(oldReference, out _),
                "coalesced engine reconnect retires cached reference without an intermediate disconnect sample");
            var calls = host.Renderer.HideCalls;
            host.Services.Views.Run(.25f);
            Check(host.Renderer.Hidden.Contains(original) && host.Renderer.HideCalls == calls,
                "name refresh occurs before local reconciliation can release an admitted retained-root hold");
            return;
        }

        var extras = new List<IViewLease>();
        if (partialBaseline)
        {
            for (var index = 0; index < 40; index++)
            {
                var authored = new FakeEntity("authored-" + index, "Authored", Vec3.Zero);
                host.Binding.Add(authored); observer.Binding.Add(new FakeEntity(authored.Id, "Authored", Vec3.Zero));
                extras.Add(Lease(seam.PlayerWorldView(host.Renderer, observer.Id.Value).Hide(authored)));
            }
            var begins = host.Control.Views!.Begins;
            host.Control.Views.SendLimit = 1; host.Control.Views.Frame(); host.Services.Views.Run(0);
            // Controlled equivalent publications isolate consumer churn; they do not synthesize admission lifetimes.
            for (var publication = 0; publication < 6; publication++)
            {
                host.Control.SnapshotOverride = Equivalent(engine.Host.Snapshot);
                host.Control.Views.Frame(); host.Services.Views.Run(0);
                engine.Advance(); observer.Runtime.BeforeModsUpdate(1f / 60); observer.Services.Views.Run(0);
            }
            Check(host.Control.Views.Begins == begins + 1 && observer.Renderer.Hidden.Count == 41,
                "equivalent roster publications preserve partial-baseline progress without redundant begin frames");
            host.Control.SnapshotOverride = null;
            // Start another genuinely dirty partial baseline for the independent lifetime/fragments regression.
            var additional = new FakeEntity("authored-final", "Authored", Vec3.Zero);
            host.Binding.Add(additional); observer.Binding.Add(new FakeEntity(additional.Id, "Authored", Vec3.Zero));
            extras.Add(Lease(seam.PlayerWorldView(host.Renderer, observer.Id.Value).Hide(additional)));
            host.Control.Views.Frame(); host.Services.Views.Run(0);
            Check(host.Control.Views.Sent == 1, "a multi-chunk baseline is partially enqueued before backpressure");
            host.Control.Views.SendLimit = 0;
        }
        subject.SendEnabled = false;
        engine.Network.Sever(guest.Link); Frames(3);
        Check(!Row(engine.Host, subject.Id).IsConnected && !host.Players.TryResolveReference(oldReference, out _)
            && !host.Services.Views.IsCurrentAvatar(original, host.Renderer), "disconnected canonical reference and native admission refuse retained root");
        Check(!host.Renderer.Hidden.Contains(original), "disconnect suspends the host's native hold");
        engine.Join(subject.Engine, "subject"); engine.Flush();
        foreach (var node in nodes) node.Runtime.BeforeModsUpdate(1f / 60);
        // Observe the replacement at the real receive boundary before host send may assign a fresh index.
        foreach (var node in new[] { host, observer })
            Check(!node.Presence.TryGetPose(subject.Id, out _) && node.Presence.TryGetPeer(subject.Id, out var replaced)
                && replaced!.Index == 0 && (replaced.Flags & (PresenceFlags.Talking | PresenceFlags.Typing)) == 0,
                "replacement admission resumes views without fabricating gameplay pose or index");
        Frames(3);
        var current = Row(engine.Host, subject.Id);
        Check(current.IsConnected && current.LifetimeId != originalLifetime, "production engine rejoin issues a genuinely fresh lifetime");
        Check(ReferenceEquals(Avatar(host, subject.Id), original), "grace rejoin retains the exact validated avatar root");
        Check(!host.Players.TryResolveReference(oldReference, out _), "retired opaque reference remains refused after rejoin");
        made = host.Players.TryCreateReference(original, out var freshReference);
        Check(made && !freshReference.Equals(oldReference) && host.Players.TryResolveReference(freshReference, out var refreshed)
            && ReferenceEquals(refreshed, original), "canonical cache refreshes by actual engine-issued lifetime");
        Check(!host.Presence.TryGetPose(subject.Id, out _), "view delivery accepts no replacement gameplay pose");
        host.Control.Views!.SendLimit = int.MaxValue; Frames(6);
        Check(host.Renderer.Hidden.Contains(original), "lifetime name refresh precedes host native-hold reconciliation");
        Check(observer.Renderer.Hidden.Contains(Avatar(observer, subject.Id)),
            partialBaseline ? "fresh lifetime view restarts partial baseline without stale wire fragments" : "fresh lifetime reference reaches observer without outer lease reacquisition");
        Check(local.IsActive && privateView.IsActive, "actual lifetime recovery preserves original outer leases");
        var savedWorld = host.World; host.World = "another-world";
        Check(!host.Services.Views.IsCurrentAvatar(original, host.Renderer), "world mismatch cannot admit retained root"); host.World = savedWorld;
        host.SceneVersion++;
        Check(!host.Services.Views.IsCurrentAvatar(original, host.Renderer), "scene mismatch cannot admit retained root"); host.SceneVersion--;

        Idle(host, observer, freshReference);
        foreach (var extra in extras) extra.Dispose();
    }

    private static void Idle(Node host, Node observer, InternalNetRef reference)
    {
        for (var index = 0; index < 200; index++)
        {
            host.Players.TryResolveReference(reference, out _);
            host.Services.Views.Run(.25f); observer.Services.Views.Run(.25f);
        }
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 10_000; index++)
        {
            host.Players.TryResolveReference(reference, out _);
            host.Services.Views.Run(.25f); observer.Services.Views.Run(.25f);
        }
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        if (allocated != 0)
            throw new InvalidOperationException("Lifetime view: unchanged hidden views and canonical references retain exact-zero idle allocation (allocated "
                + allocated + " bytes).");
    }

    private static MultiplayerSessionSnapshot Equivalent(MultiplayerSessionSnapshot source) => new(source.Id, source.State,
        source.ProcessKind, source.ExecutionSides, source.LocalParticipantId, source.Participants, source.Tick, source.Seed,
        source.HostParticipantId, source.EndReason);

    private static MultiplayerParticipant Row(SessionEngine engine, ParticipantId id) => engine.Snapshot.Participants.Single(player => player.Id.Equals(id));
    private static IEntity Avatar(Node node, ParticipantId id) => node.Presence.TryGetAvatar(id, out var value) && value != null
        ? value : throw new InvalidOperationException("Expected validated engine avatar.");
    private static IViewLease Lease(OperationResult<IViewLease> result) => result.TryGetValue(out var lease) ? lease
        : throw new InvalidOperationException("Expected view lease: " + result.ErrorCode);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Lifetime view: " + message); }

    private sealed class Node : IDisposable
    {
        internal readonly SessionEngine Engine;
        internal readonly ParticipantId Id;
        internal readonly ReferenceEngineControl Control;
        internal readonly MultiplayerRuntime Runtime;
        internal readonly MultiplayerReplication Services;
        internal readonly MultiplayerPresence Presence;
        internal readonly MultiplayerPlayers Players;
        internal readonly Renderer Renderer;
        internal readonly Binding Binding;
        internal bool SendEnabled = true;
        internal string? World = "world-one";
        internal int SceneVersion;
        internal Node(SessionEngine engine, EngineHarness harness)
        {
            Engine = engine; Id = engine.Snapshot.LocalParticipantId!.Value; Control = new ReferenceEngineControl(engine);
            var log = new QuietLog(); Runtime = new MultiplayerRuntime(() => Control, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
            Runtime.SessionPhaseSource = () => SessionPhase.Running;
            Services = new MultiplayerReplication(Runtime);
            Presence = MultiplayerPresence.Install(Runtime, Services, new FakePresenceBackend { RecordTrails = false }, () => harness.NowMicros);
            Presence.RunningWorld = () => World; Presence.RunningSpawn = () => TransformState.Identity; Presence.SceneVersion = () => SceneVersion;
            Players = MultiplayerPlayers.Install(Runtime, Presence); Services.Players = Players;
            Check(Runtime.Attach(Control), "real engine control attaches"); Runtime.BeforeModsUpdate(0);
            Renderer = new Renderer(this); Binding = new Binding(Renderer); Services.Views.Activate(Binding);
        }
        public void Dispose() { Runtime.Disconnect(); Renderer.Dispose(); }
    }

    private sealed class Binding(Renderer renderer) : IInternalWorldViewBinding
    {
        private readonly Dictionary<string, IEntity> authored = new(StringComparer.Ordinal);
        internal void Add(IEntity entity) => authored.Add(entity.Id, entity);
        public string WorldId => "world-one";
        public IInternalWorldViewRenderer Renderer => renderer;
        public bool TryNameAuthored(IEntity entity, out string scene, out string id)
        { scene = "scene"; id = entity.Id; return authored.TryGetValue(id, out var current) && ReferenceEquals(current, entity); }
        public IEntity? ResolveAuthored(string scene, string id) => scene == "scene" && authored.TryGetValue(id, out var entity) ? entity : null;
        internal bool Owns(IEntity entity) => authored.TryGetValue(entity.Id, out var current) && ReferenceEquals(current, entity);
    }
    private sealed class Renderer(Node owner) : IInternalWorldViewRenderer
    {
        internal readonly HashSet<IEntity> Hidden = new();
        internal int HideCalls;
        public bool IsAlive { get; private set; } = true;
        public bool FogAvailable => true;
        public bool EnvironmentAvailable => true;
        public IReadOnlyList<string> Presets => Array.Empty<string>();
        public event Action Stopped = delegate { };
        public bool HasPreset(string preset) => false;
        public OperationResult<bool> SetEnvironment(string? preset) => OperationResult<bool>.Success(true);
        public OperationResult<bool> SetFog(VisionFog? fog) => OperationResult<bool>.Success(true);
        public OperationResult<IDisposable> Hide(IEntity entity)
        {
            if (!entity.IsAlive || !(owner.Binding.Owns(entity) || owner.Services.Views.IsCurrentAvatar(entity, this)))
                return OperationResult<IDisposable>.Failure(ModErrorCode.Unavailable, "Target is not current.");
            HideCalls++; Hidden.Add(entity); return OperationResult<IDisposable>.Success(new Hold(this, entity));
        }
        public void Dispose() { if (!IsAlive) return; IsAlive = false; Stopped(); Hidden.Clear(); }
        private sealed class Hold(Renderer renderer, IEntity entity) : IDisposable
        { private bool active = true; public void Dispose() { if (!active) return; active = false; renderer.Hidden.Remove(entity); } }
    }
    private sealed class QuietLog : IMultiplayerLog
    {
        public void Info(string message) { }
        public void Warn(string message) { }
        public void Error(string message) => throw new InvalidOperationException(message);
    }
}
