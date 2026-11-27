using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.ModManager.TestDoubles;
using TopiaForge.Mods;
using static TopiaForge.ModManager.Tests.Seam;

namespace TopiaForge.ModManager.Tests
{
    /// <summary>
    /// The network decorators change no behaviour: each member hands the host's service exactly its arguments and returns
    /// exactly what it returns, its exceptions included; the asset service keeps parent scopes working and records each
    /// spawn that succeeds, without the mod ever seeing the recording.
    /// </summary>
    internal static class NetGameplayServicesTests
    {
        internal static void Run()
        {
            EntitiesPassThrough();
            ItemsPassThrough();
            UiPassesThrough();
            AssetsPassThroughAndRecordSpawns();
            ParentAssetScopesPassThrough();
            RecordingNeverReachesTheMod();
        }

        private static void EntitiesPassThrough()
        {
            var inner = new Probe();
            var service = new NetEntityService(inner, Bind());
            var entity = new SeamEntity();
            var transform = new TransformState(new Vec3(1, 2, 3), Quat.Identity, new Vec3(1, 1, 1));
            var query = new EntityQuery(maximumResults: 3);
            That(service.TryGetTransform(entity, out var read) && read.Equals(Probe.Transform) && inner.Last("TryGetTransform", entity), "TryGetTransform");
            That(ReferenceEquals(service.SetTransform(entity, transform), inner.SetResult) && inner.Last("SetTransform", entity, transform), "SetTransform");
            That(ReferenceEquals(service.Query(query), inner.QueryResult) && inner.Last("Query", query), "Query");
            That(ReferenceEquals(service.Destroy(entity), inner.DestroyResult) && inner.Last("Destroy", entity), "Destroy");
            That(ReferenceEquals(service.AcquireMotion(entity), inner.MotionResult) && inner.Last("AcquireMotion", entity), "AcquireMotion");
            That(ReferenceEquals(service.Inner, inner), "the decorator keeps the host's service");
            inner.Failure = new InvalidOperationException("entity failure");
            Rethrows(inner.Failure, () => service.SetTransform(entity, transform), "SetTransform");
            Rethrows(inner.Failure, () => service.Destroy(null!), "Destroy, even of nothing");
            Rethrows(inner.Failure, () => service.AcquireMotion(entity), "AcquireMotion");
            Rethrows(inner.Failure, () => service.Query(query), "Query");
            Rethrows(inner.Failure, () => service.TryGetTransform(entity, out _), "TryGetTransform");
            Throws<ArgumentNullException>(() => new NetEntityService(null!, Bind()), "the decorator needs the host's service");
            Throws<ArgumentNullException>(() => new NetEntityService(inner, null!), "and its context's seam");
        }

        private static void ItemsPassThrough()
        {
            var inner = new Probe();
            var service = new NetItemService(inner, Bind());
            var request = new ItemGrantRequest("tools.wrench", 2);
            using var cancellation = new CancellationTokenSource();
            That(service.TryGetHeld(out var held) && ReferenceEquals(held, Probe.Held) && inner.Last("TryGetHeld"), "TryGetHeld");
            That(ReferenceEquals(service.GiveAsync(request, cancellation.Token), inner.GiveResult) && inner.Last("GiveAsync", request, cancellation.Token), "GiveAsync");
            That(ReferenceEquals(service.DropHeldAsync(new Vec3(0, 4, 0), cancellation.Token), inner.DropResult) &&
                 inner.Last("DropHeldAsync", new Vec3(0, 4, 0), cancellation.Token), "DropHeldAsync");
            inner.Failure = new InvalidOperationException("item failure");
            Rethrows(inner.Failure, () => service.GiveAsync(request), "GiveAsync");
            Rethrows(inner.Failure, () => service.DropHeldAsync(Vec3.Zero), "DropHeldAsync");
            Rethrows(inner.Failure, () => service.TryGetHeld(out _), "TryGetHeld");
        }

        private static void UiPassesThrough()
        {
            var inner = new Probe();
            var service = new NetUiService(inner, Bind());
            var surface = new UiSurfaceRequest("seam.surface", "Seam", "A surface the host makes.");
            var modal = new UiModalRequest("Leave current game?", "Your progress stays here.");
            Action<bool> completed = _ => { };
            var preferences = UiAccessibilityPreferences.Default;
            That(ReferenceEquals(service.Accessibility, Probe.Preferences) && inner.Last("Accessibility"), "Accessibility");
            That(ReferenceEquals(service.ApplyAccessibility(preferences), inner.AccessibilityResult) && inner.Last("ApplyAccessibility", preferences), "ApplyAccessibility");
            That(ReferenceEquals(service.CreateSurface(surface), inner.SurfaceResult) && inner.Last("CreateSurface", surface), "CreateSurface");
            That(ReferenceEquals(service.ShowModal(modal, completed), inner.ModalResult) && inner.Last("ShowModal", modal, completed), "ShowModal");
            That(ReferenceEquals(service.ShowToast("Saved", UiTone.Success), inner.ToastResult) && inner.Last("ShowToast", "Saved", UiTone.Success), "ShowToast");
            IUiService asInterface = service;
            asInterface.ShowToast("Plain");
            That(inner.Last("ShowToast", "Plain", UiTone.Neutral), "the interface's default tone reaches the host's service");
            inner.Failure = new InvalidOperationException("ui failure");
            Rethrows(inner.Failure, () => service.CreateSurface(surface), "CreateSurface");
            Rethrows(inner.Failure, () => service.ShowModal(modal, completed), "ShowModal");
            Rethrows(inner.Failure, () => service.ShowToast("x"), "ShowToast");
            Rethrows(inner.Failure, () => service.ApplyAccessibility(preferences), "ApplyAccessibility");
            Rethrows(inner.Failure, () => _ = service.Accessibility, "Accessibility");
        }

        private static void AssetsPassThroughAndRecordSpawns()
        {
            var inner = new Probe();
            var replication = Bind();
            var service = new NetAssetService(inner, replication);
            var bundle = new SeamBundle();
            var request = new AssetSpawnRequest(new SeamPrefab(), new TransformState(new Vec3(4, 0, 2), Quat.Identity, new Vec3(1, 1, 1)));
            using var cancellation = new CancellationTokenSource();
            That(ReferenceEquals(service.LoadBundleAsync("assets/blocks.bundle", cancellation.Token), inner.BundleResult) &&
                 inner.Last("LoadBundleAsync", "assets/blocks.bundle", cancellation.Token), "LoadBundleAsync");
            That(ReferenceEquals(service.LoadPrefabAsync(bundle, "Crate", cancellation.Token), inner.PrefabResult) &&
                 inner.Last("LoadPrefabAsync", bundle, "Crate", cancellation.Token), "LoadPrefabAsync");
            var spawned = service.Spawn(request);
            That(ReferenceEquals(spawned, inner.SpawnResult) && inner.Last("Spawn", request), "Spawn returns the host's own result");
            var records = replication.Services.Runtime.Spawns.Live();
            That(records.Count == 1, "a spawn that succeeds is recorded at spawn time (" + records.Count + " records)");
            var record = records[0];
            That(ReferenceEquals(record.Entity, inner.SpawnResult.Value) && record.Description.Kind == MultiplayerSpawnKinds.AssetPrefab &&
                 ReferenceEquals(record.Description.Descriptor, MultiplayerAssetDescriptor.Unknown) && record.Description.Transform.Equals(request.Transform) &&
                 record.Description.OwnerModId == "io.example.seam" && record.Description.Profile.Equals(replication.Profile) &&
                 record.Description.Source == MultiplayerSpawnSource.SdkSpawn,
                "a spawn that succeeds is recorded at spawn time, described without the request's prefab handle, which belongs to its scope");

            // A prefab the runtime loaded names its bundle and asset, which is how a joined player builds the same replica.
            var crate = new SeamEntity();
            inner.SpawnResult = OperationResult<ISpawnedEntity>.Success(crate);
            service.Spawn(new AssetSpawnRequest(new ProvenancePrefab("assets/blocks.bundle", "Crate"), TransformState.Identity));
            That(replication.Services.Runtime.Spawns.Find(crate)?.Description.Descriptor is MultiplayerAssetDescriptor described &&
                 described.IsPortable && described.BundlePath == "assets/blocks.bundle" && described.AssetName == "Crate",
                "a prefab with provenance is described by its bundle path and asset name");
            inner.SpawnResult = OperationResult<ISpawnedEntity>.Success(new SeamEntity());
            That(MultiplayerAssetDescriptor.Of(new ProvenancePrefab(new string('b', MultiplayerAssetDescriptor.MaximumBundlePathLength + 1), "Crate")) ==
                 MultiplayerAssetDescriptor.Unknown && MultiplayerAssetDescriptor.Of(new ProvenancePrefab("assets/blocks.bundle", "")) == MultiplayerAssetDescriptor.Unknown,
                "a provenance past its bounds, or empty, describes nothing a joined player could build");
            replication.Services.Runtime.Spawns.Remove(replication.Services.Runtime.Spawns.Find(crate)!);

            inner.SpawnResult = OperationResult<ISpawnedEntity>.Failure(ModErrorCode.InvalidArgument, "foreign prefab");
            That(ReferenceEquals(service.Spawn(request), inner.SpawnResult) && replication.Services.Runtime.Spawns.Live().Count == 1,
                "a refused spawn passes through and records nothing");
            inner.Failure = new InvalidOperationException("asset failure");
            Rethrows(inner.Failure, () => service.Spawn(request), "Spawn");
            Rethrows(inner.Failure, () => service.Spawn(null!), "Spawn, even of nothing");
            Rethrows(inner.Failure, () => service.LoadBundleAsync("x"), "LoadBundleAsync");
            Rethrows(inner.Failure, () => service.LoadPrefabAsync(bundle, "x"), "LoadPrefabAsync");
            That(replication.Services.Runtime.Spawns.Live().Count == 1, "and a spawn that throws records nothing either");
        }

        private static void ParentAssetScopesPassThrough()
        {
            // The scope's host service receives the parent's host service, not its decorator, exactly as before.
            var parentInner = new Probe();
            var childInner = new Probe();
            var parent = new NetAssetService(parentInner, Bind());
            IParentAssetScope child = new NetAssetService(childInner, Bind());
            child.AttachParent(parent);
            That(ReferenceEquals(childInner.AttachedParent, parentInner) && childInner.Last("AttachParent", parentInner), "a decorated parent is unwrapped");
            var raw = new Probe();
            IParentAssetScope second = new NetAssetService(new Probe(), Bind());
            second.AttachParent(raw);
            That(ReferenceEquals(((Probe)((NetAssetService)second).Inner).AttachedParent, raw), "an undecorated parent passes as it is");

            // The host's own refusal of a parent reaches the caller unchanged.
            var refusing = new Probe { Failure = new InvalidOperationException("Parent assets must belong to the same loaded package host.") };
            IParentAssetScope refused = new NetAssetService(refusing, Bind());
            Rethrows(refusing.Failure, () => refused.AttachParent(parent), "AttachParent");

            // A host service without parent scopes was never attached before the decorator existed, and still is not.
            var plain = new PlainAssets();
            IParentAssetScope plainScope = new NetAssetService(plain, Bind());
            plainScope.AttachParent(parent);
            That(plain.Calls == 0, "a service without parent scopes is left as it was");
        }

        private static void RecordingNeverReachesTheMod()
        {
            var inner = new Probe();
            var replication = Bind();
            var log = (RecordingRuntimeLog)replication.Services.Runtime.Log;
            var service = new NetAssetService(inner, replication);
            var broken = new SeamEntity { ThrowOnId = true };
            inner.SpawnResult = OperationResult<ISpawnedEntity>.Success(broken);
            var request = new AssetSpawnRequest(new SeamPrefab(), TransformState.Identity);
            var first = service.Spawn(request);
            var second = service.Spawn(request);
            That(ReferenceEquals(first, inner.SpawnResult) && ReferenceEquals(second, inner.SpawnResult) && replication.Services.Runtime.Spawns.Count == 0,
                "a spawn the runtime cannot record still reaches the mod as it succeeded");
            var errors = log.Lines.Where(line => line.StartsWith("error:", StringComparison.Ordinal)).ToArray();
            That(errors.Length == 1 && errors[0].Contains("InvalidOperationException", StringComparison.Ordinal) && !errors[0].Contains("203.0.113.9", StringComparison.Ordinal),
                "the failure is reported once, by type (" + string.Join(" | ", errors) + ")");
            inner.SpawnResult = OperationResult<ISpawnedEntity>.Success(new SeamEntity());
            service.Spawn(request);
            inner.SpawnResult = OperationResult<ISpawnedEntity>.Success(broken);
            service.Spawn(request);
            That(log.Lines.Count(line => line.StartsWith("error:", StringComparison.Ordinal)) == 2, "and again after a spawn recorded in between");

            var cosmetic = new NetAssetService(new Probe(), Bind(new ModNetProfile(ModNetworkClass.ClientLocal, ModExecutionContext.Process)));
            cosmetic.Spawn(request);
            That(cosmetic.Replication.Services.Runtime.Spawns.Count == 0, "a client-local mod's spawn is never recorded");
        }

        private static RuntimeReplication Bind(ModNetProfile? profile = null)
        {
            var log = new RecordingRuntimeLog();
            var runtime = new MultiplayerRuntime(() => null, new MultiplayerRuntimeHost("2545", "0.2.0", log), log);
            return new MultiplayerReplication(runtime).Bind("io.example.seam",
                profile ?? new ModNetProfile(ModNetworkClass.Shared, ModExecutionContext.Process), new OwnerModLifetime());
        }

        private static void Rethrows(Exception expected, Action call, string member)
        {
            try
            {
                call();
            }
            catch (Exception actual)
            {
                That(ReferenceEquals(actual, expected), member + " lets the host's own exception through unchanged");
                return;
            }

            throw new InvalidOperationException("Multiplayer seam test failed: " + member + " swallowed the host's exception");
        }

        /// <summary>A host service of every kind that records each call and answers with results the test can compare by reference.</summary>
        private sealed class Probe : IEntityService, IItemService, IUiService, IAssetService, IParentAssetScope
        {
            internal static readonly TransformState Transform = new TransformState(new Vec3(7, 8, 9), Quat.Identity, new Vec3(1, 1, 1));
            internal static readonly HeldItemSnapshot Held = new HeldItemSnapshot("tools.wrench", new SeamEntity());
            internal static readonly UiAccessibilityPreferences Preferences = new UiAccessibilityPreferences(true, 1.25f, true, 0.5f);

            private readonly List<(string Member, object?[] Arguments)> calls = new List<(string, object?[])>();

            internal Exception? Failure { get; set; }

            internal IAssetService? AttachedParent { get; private set; }

            internal OperationResult<TransformState> SetResult { get; } = OperationResult<TransformState>.Success(Transform);
            internal IReadOnlyList<IEntity> QueryResult { get; } = new IEntity[] { new SeamEntity() };
            internal OperationResult<bool> DestroyResult { get; } = OperationResult<bool>.Failure(ModErrorCode.InvalidArgument, "not yours");
            internal OperationResult<IEntityMotion> MotionResult { get; } = OperationResult<IEntityMotion>.Failure(ModErrorCode.Conflict, "held");
            internal Task<OperationResult<HeldItemSnapshot>> GiveResult { get; } = Task.FromResult(OperationResult<HeldItemSnapshot>.Success(Held));
            internal Task<OperationResult<IEntity>> DropResult { get; } = Task.FromResult(OperationResult<IEntity>.Success((IEntity)new SeamEntity()));
            internal OperationResult<UiAccessibilityPreferences> AccessibilityResult { get; } = OperationResult<UiAccessibilityPreferences>.Success(Preferences);
            internal OperationResult<IUiSurface> SurfaceResult { get; } = OperationResult<IUiSurface>.Failure(ModErrorCode.Unavailable, "no surfaces");
            internal OperationResult<IUiModal> ModalResult { get; } = OperationResult<IUiModal>.Failure(ModErrorCode.Unavailable, "no modals");
            internal OperationResult<bool> ToastResult { get; } = OperationResult<bool>.Success(true);
            internal Task<OperationResult<IAssetBundle>> BundleResult { get; } = Task.FromResult(OperationResult<IAssetBundle>.Success((IAssetBundle)new SeamBundle()));
            internal Task<OperationResult<IPrefabAsset>> PrefabResult { get; } = Task.FromResult(OperationResult<IPrefabAsset>.Success((IPrefabAsset)new SeamPrefab()));
            internal OperationResult<ISpawnedEntity> SpawnResult { get; set; } = OperationResult<ISpawnedEntity>.Success(new SeamEntity());

            public UiAccessibilityPreferences Accessibility => Record(Preferences, "Accessibility");

            /// <summary>Whether the last call was <paramref name="member"/> with exactly these arguments, by reference for objects.</summary>
            internal bool Last(string member, params object?[] arguments)
            {
                if (calls.Count == 0) return false;
                var last = calls[^1];
                return last.Member == member && last.Arguments.Length == arguments.Length &&
                       last.Arguments.Zip(arguments, (actual, expected) => actual is ValueType || actual is string ? Equals(actual, expected) : ReferenceEquals(actual, expected))
                           .All(same => same);
            }

            public bool TryGetTransform(IEntity entity, out TransformState transform)
            {
                transform = Record(Transform, "TryGetTransform", entity);
                return true;
            }

            public OperationResult<TransformState> SetTransform(IEntity entity, TransformState transform) => Record(SetResult, "SetTransform", entity, transform);
            public IReadOnlyList<IEntity> Query(EntityQuery query) => Record(QueryResult, "Query", query);
            public OperationResult<bool> Destroy(IEntity entity) => Record(DestroyResult, "Destroy", entity);
            public OperationResult<IEntityMotion> AcquireMotion(IEntity entity) => Record(MotionResult, "AcquireMotion", entity);

            public bool TryGetHeld(out HeldItemSnapshot? item)
            {
                item = Record(Held, "TryGetHeld");
                return true;
            }

            public Task<OperationResult<HeldItemSnapshot>> GiveAsync(ItemGrantRequest request, CancellationToken cancellationToken = default) =>
                Record(GiveResult, "GiveAsync", request, cancellationToken);
            public Task<OperationResult<IEntity>> DropHeldAsync(Vec3 velocity, CancellationToken cancellationToken = default) =>
                Record(DropResult, "DropHeldAsync", velocity, cancellationToken);
            public OperationResult<UiAccessibilityPreferences> ApplyAccessibility(UiAccessibilityPreferences preferences) =>
                Record(AccessibilityResult, "ApplyAccessibility", preferences);
            public OperationResult<IUiSurface> CreateSurface(UiSurfaceRequest request) => Record(SurfaceResult, "CreateSurface", request);
            public OperationResult<IUiModal> ShowModal(UiModalRequest request, Action<bool> completed) => Record(ModalResult, "ShowModal", request, completed);
            public OperationResult<bool> ShowToast(string message, UiTone tone = UiTone.Neutral) => Record(ToastResult, "ShowToast", message, tone);
            public Task<OperationResult<IAssetBundle>> LoadBundleAsync(string relativePath, CancellationToken cancellationToken = default) =>
                Record(BundleResult, "LoadBundleAsync", relativePath, cancellationToken);
            public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(IAssetBundle bundle, string assetName, CancellationToken cancellationToken = default) =>
                Record(PrefabResult, "LoadPrefabAsync", bundle, assetName, cancellationToken);
            public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request) => Record(SpawnResult, "Spawn", request);

            public void AttachParent(IAssetService parent)
            {
                Record(parent, "AttachParent", parent);
                AttachedParent = parent;
            }

            private T Record<T>(T result, string member, params object?[] arguments)
            {
                calls.Add((member, arguments));
                if (Failure != null) throw Failure;
                return result;
            }
        }

        /// <summary>A host asset service without parent scopes.</summary>
        private sealed class PlainAssets : IAssetService
        {
            internal int Calls { get; private set; }

            public Task<OperationResult<IAssetBundle>> LoadBundleAsync(string relativePath, CancellationToken cancellationToken = default) => Fail<IAssetBundle>();
            public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(IAssetBundle bundle, string assetName, CancellationToken cancellationToken = default) => Fail<IPrefabAsset>();
            public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request)
            {
                Calls++;
                return OperationResult<ISpawnedEntity>.Failure(ModErrorCode.Unavailable, "no assets");
            }

            private Task<OperationResult<T>> Fail<T>() where T : notnull
            {
                Calls++;
                return Task.FromResult(OperationResult<T>.Failure(ModErrorCode.Unavailable, "no assets"));
            }
        }

        private sealed class SeamBundle : IAssetBundle
        {
            public string RelativePath => "assets/blocks.bundle";
            public bool IsAlive => true;
            public void Dispose()
            {
            }
        }

        /// <summary>A prefab handle as the runtime's asset service makes one: it knows its bundle and its asset name.</summary>
        private sealed class ProvenancePrefab : IPrefabAsset, IPrefabAssetProvenance
        {
            internal ProvenancePrefab(string bundlePath, string assetName)
            {
                BundlePath = bundlePath;
                AssetName = assetName;
            }

            public string BundlePath { get; }

            public string AssetName { get; }

            public string Name => AssetName;

            public bool IsAlive => true;

            public void Dispose()
            {
            }
        }
    }
}
