using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.Testing;
using TopiaForge.Worlds;
using TopiaForge.Worlds.Maps;
using static TopiaForge.ModManager.Tests.MapDocumentTests;

namespace TopiaForge.ModManager.Tests
{
    internal static class AuthoredMapImportTests
    {
        internal static async Task RunAsync()
        {
            var folder = Directory.CreateTempSubdirectory("authored-import-");
            try
            {
                File.WriteAllBytes(Path.Combine(folder.FullName, "map.json"), Project());
                using var fixture = new BuiltinWorldFixture { SceneTransitions = new MapWorldServiceTests.Transitions() };
                using var operations = new Operations(fixture); fixture.ContentOperations = operations;
                var host = new Host();
                using var service = new LocalWorldService(fixture.Logger, () => host, true, folder.FullName);
                var facade = (ILocalWorldService)((IOwnerContextBoundExtensionFactory)service).CreateOwnerFacade(typeof(ILocalWorldService), fixture);
                Assert((await facade.ImportMapAsync("stale", "map.json")).ErrorCode == ModErrorCode.InvalidState && host.Preparations == 0, "stale session refuses before reading content");
                Assert((await facade.ImportMapAsync(fixture.SessionId, "../outside.json")).ErrorCode == ModErrorCode.InvalidArgument && host.Preparations == 0, "local path containment");
                var result = await facade.ImportMapAsync(fixture.SessionId, "map.json");
                Assert(result.Succeeded && result.Value!.MapId == "file:map.json" && result.Value.Entities.Count == 1 && host.Preparations == 1, "production facade returns an index from validated bytes");
                var retained = result.Value!; operations.End();
                Assert(!retained.IsAlive && host.ContentDisposed == 1, "session ownership ends map and native content");
                operations.Ended = false; host.OnImport = () => operations.Ended = true;
                Assert((await facade.ImportMapAsync(fixture.SessionId, "map.json")).ErrorCode == ModErrorCode.Cancelled && host.ContentDisposed == 2, "late stale completion releases index and native content");
                operations.Ended = false; host.OnImport = null; host.IndexFails = true;
                Assert((await facade.ImportMapAsync(fixture.SessionId, "map.json")).ErrorCode == ModErrorCode.External && host.ContentDisposed == 3, "index failure releases native content");
                host.IndexFails = false;
                using var cancellation = new CancellationTokenSource(); host.OnImport = cancellation.Cancel;
                Assert((await facade.ImportMapAsync(fixture.SessionId, "map.json", cancellation.Token)).ErrorCode == ModErrorCode.Cancelled && host.ContentDisposed == 4,
                    "cancelled native completion drains and releases content");
                await FakeOwnership();
                Console.WriteLine("AuthoredMapImportTests passed (production facade ownership, late completion, cancellation and fake imports).");
            }
            finally { folder.Delete(true); }
        }
        private static async Task FakeOwnership()
        {
            using var context = new FakeModContext();
            var service = new FakeLocalWorldService(context.Lifetime) { SessionId = "session" };
            service.LocalWorlds.Add(new LocalWorldFile("map.json", "map.json", "Test map", ""));
            Assert(!(await service.ImportMapAsync("session", "map.json")).Succeeded && service.ImportedLocalWorlds.Count == 0, "unconfigured fake never records success");
            var map = new FakeAuthoredMap().AddEntity("entry", "Entry"); service.MapImporter = _ => map;
            Assert((await service.ImportMapAsync("session", "map.json")).Succeeded && service.ImportedLocalWorlds.Count == 1, "configured fake import");
            context.Dispose(); Assert(!map.IsAlive, "fake scope owns imported map");
            using var runner = new GamemodeSessionRunner(new Factory(), GamemodeSessionRole.Client).WithMap(new FakeAuthoredMap().AddEntity("entry", "Entry"));
            using var extra = new FakeAuthoredMap();
            Expect<InvalidOperationException>(() => runner.WithMap(extra), contains: "one map");
            Assert((await runner.StartAsync()).Succeeded && runner.Participant!.World.Map != null, "participant sees supplied map");
            var supplied = runner.World.RequireMap(); runner.Stop(); Assert(!supplied.IsAlive, "runner ends supplied map");
        }
        private sealed class Factory : IGamemodeFactory, IGamemodeParticipantFactory
        {
            public Task<OperationResult<IGamemodeController>> StartAsync(IGamemodeSession session, CancellationToken token) => throw new NotSupportedException();
            public Task<OperationResult<IGamemodeParticipantController>> StartParticipantAsync(IGamemodeParticipantSession session, CancellationToken token)
            {
                Assert(session.World.RequireMap().Entity("entry").Entity.IsAlive, "participant indexes map");
                return Task.FromResult(OperationResult<IGamemodeParticipantController>.Success(new Participant()));
            }
        }
        private sealed class Participant : IGamemodeParticipantController { public void Dispose() { } }
        private sealed class Operations : IInternalWorldSessionOperations, IDisposable
        {
            private readonly BuiltinWorldFixture context;
            private readonly List<IDisposable> owned = new List<IDisposable>();
            internal bool Ended;
            internal Operations(BuiltinWorldFixture context) { this.context = context; }
            public async Task<OperationResult<bool>> RunContentOperationAsync(string sessionId,
                Func<IInternalSessionContentContext, CancellationToken, Task<OperationResult<IDisposable>>> action, CancellationToken token = default)
            {
                if (Ended || sessionId != context.SessionId) return OperationResult<bool>.Failure(ModErrorCode.InvalidState, "Stale session.");
                var result = await action(context, token);
                if (!result.TryGetValue(out var resource)) return OperationResult<bool>.Failure(result.ErrorCode, result.ErrorMessage);
                if (Ended || token.IsCancellationRequested) { resource.Dispose(); return OperationResult<bool>.Failure(ModErrorCode.Cancelled, "Session ended."); }
                owned.Add(resource); return OperationResult<bool>.Success(true);
            }
            internal void End() { Ended = true; foreach (var resource in owned) resource.Dispose(); owned.Clear(); }
            public void Dispose() => End();
        }
        private sealed class Host : ILocalWorldImportHost, ILocalAuthoredMapBackend, IMapImportTransaction
        {
            internal int Preparations, ContentDisposed;
            internal Action? OnImport;
            internal bool IndexFails;
            public bool IsAvailable => true;
            public bool CanScanFolder => false;
            public string GetDefaultImportFolder() => "";
            public IReadOnlyList<RoboWorldFile> ScanFolder(string folder) => Array.Empty<RoboWorldFile>();
            public OperationResult<ILocalImportTransaction> Prepare(RoboWorldImportPlan plan, IReadOnlyList<WorldAssetOverride> overrides, WorldSceneIdentity scene) => throw new NotSupportedException();
            public Task<OperationResult<IMapImportTransaction>> PrepareMapAsync(MapDocument document, string? sceneId, string label, WorldSceneIdentity scene, IReadOnlyList<WorldAssetOverride> overrides, CancellationToken token)
            { Preparations++; return Task.FromResult(OperationResult<IMapImportTransaction>.Success(this)); }
            public bool NativeEntered => true;
            public bool NativeReturned => true;
            public string SceneId => "scene";
            public OperationResult<IDisposable> Import() { OnImport?.Invoke(); return OperationResult<IDisposable>.Success(new Owned(() => ContentDisposed++)); }
            public AuthoredMapIndex CreateMap(MapDocument document, string mapId, IModContext context)
            {
                if (IndexFails) throw new InvalidOperationException("Synthetic native indexing fault.");
                return new AuthoredMapIndex(mapId, document.Authored.Scenes[SceneId], new MapRevision(document.Sha256, document.Packed.Length, ""),
                    Array.Empty<AuthoredMarker>(), p => p, t => TransformState.Identity);
            }
            public OperationResult<TransformState> PlaceRespawn(string id) => throw new NotSupportedException();
            public void Dispose() { }
        }
        private sealed class Owned : IDisposable
        { private Action? action; internal Owned(Action action) { this.action = action; } public void Dispose() { var old = action; action = null; old?.Invoke(); } }
    }
}
