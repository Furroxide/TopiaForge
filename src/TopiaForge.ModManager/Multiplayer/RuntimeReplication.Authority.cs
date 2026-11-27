using System;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// Authority routing for one context (WS3.7): where its spawns, registrations, transform changes and destroys go on a
    /// joined player. A call on the player's intent, or any call of a participant context, which acts for its player, is a
    /// prediction, made here, frozen and asked of the host; a scripted one elsewhere is a shadow of the host's own run of
    /// the same code; neither is ever recorded as canonical. Offline and on a listen host every call is canonical, as
    /// before, and a scripted spawn that a joined player shadows is stamped with its mod's next shadow key. See
    /// docs/internal/MultiplayerRuntimeIntegration.md, section 16.
    /// </summary>
    internal sealed partial class RuntimeReplication
    {
        internal const string OnlyTheHostMakes = "Only the host can make this kind of object here; its provider does not say how the host makes it for players.";
        internal const string NotShareable = "This object cannot be shared with the other players: its prefab does not say which bundle it comes from.";
        internal const string NotAPlainPath = "This object cannot be shared with the other players: load its bundle by its plain path inside the mod's package, such as assets/world.bundle, without \"./\", doubled slashes, a name that ends in a dot or a space, a short name such as WORLD~1.BUN, or a name Windows keeps for a device, such as CON or NUL.";
        internal const string TooManyWaiting = "Too many objects are waiting for the host; wait a moment, then try again.";
        internal const string TooManyObjects = "This player already has as many objects as the host allows. Remove some, then try again.";
        internal const string HostNotReady = "The host is not ready for new objects yet; try again in a moment.";

        /// <summary>
        /// Gets or sets the UI service the context's gameplay host made, beneath its network decorator: a joined player's
        /// refused prediction is shown through it, as a toast the kit renders.
        /// </summary>
        internal IUiService? Ui { get; set; }

        internal bool IsStopping => lifetime.IsStopping;

        /// <summary>Gets authority routing while this process is a joined player, or null: offline and on a host nothing routes.</summary>
        internal IMultiplayerAuthority? JoinedAuthority
        {
            get
            {
                if (services.Runtime.Realm != NetRealm.Client) return null;
                var authority = services.Authority;
                return authority != null && authority.IsJoined ? authority : null;
            }
        }

        /// <summary>
        /// The shadow key a canonical spawn of this context takes now: its mod's next one when a joined player's own run of the
        /// same code shadows it, scripted code in a context every peer runs, and zero otherwise, as for anything the host makes
        /// for a player.
        /// </summary>
        internal long CanonicalShadowKey()
        {
            if (services.MakingForPlayer) return 0;
            return SpawnAuthorityRules.IsShadowable(Profile, SourceOf(VisibleIntent))
                ? services.Runtime.Spawns.ShadowKeys.Next(OwnerModId)
                : 0;
        }

        /// <summary>
        /// A joined player's spawn through the asset service. For its player it is a prediction: made here through
        /// <paramref name="inner"/>, frozen, and asked of the host by its bundle and asset name. Scripted, it is a shadow of
        /// the host's spawn. Either way the mod holds the handle <paramref name="inner"/> gave, and nothing is recorded.
        /// </summary>
        internal OperationResult<ISpawnedEntity> RouteAssetSpawn(AssetSpawnRequest request, IAssetService inner, IMultiplayerAuthority authority)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            var intent = VisibleIntent;
            var route = ExecutionRouting.Route(NetRealm.Client, Profile, SourceOf(intent));
            if (route == ExecutionRoute.Forwarded)
            {
                var asset = MultiplayerAssetDescriptor.Of(request.Prefab);
                if (!asset.IsPortable) return OperationResult<ISpawnedEntity>.Failure(ModErrorCode.Unavailable, NotShareable);

                // The host makes a player's object only from a plain path inside the package; anything else it refuses for certain.
                if (!SpawnAuthorityRules.IsSafeBundlePath(asset.BundlePath)) return OperationResult<ISpawnedEntity>.Failure(ModErrorCode.Unavailable, NotAPlainPath);
                var refusal = authority.CheckPrediction();
                if (refusal != SpawnRefusal.None) return OperationResult<ISpawnedEntity>.Failure(ModErrorCode.RateLimited, Refused(refusal));
                var predicted = inner.Spawn(request);
                if (!predicted.TryGetValue(out var spawned) || spawned == null) return predicted;
                authority.Predict(OwnerModId, MultiplayerSpawnKinds.AssetPrefab, WorldEntryCodec.EncodeAsset(asset), spawned, spawned, null, SourceOf(intent),
                    intent != null && intent.IsInputFrame, request.Transform, Ui);
                return predicted;
            }

            if (route == ExecutionRoute.Shadow)
            {
                if (!authority.CanShadow) return OperationResult<ISpawnedEntity>.Failure(ModErrorCode.RateLimited, TooManyWaiting);
                var shadowed = inner.Spawn(request);
                if (!shadowed.TryGetValue(out var spawned) || spawned == null) return shadowed;
                authority.Shadow(OwnerModId, MultiplayerSpawnKinds.AssetPrefab, WorldEntryCodec.EncodeAsset(MultiplayerAssetDescriptor.Of(request.Prefab)), spawned, spawned, null);
                return shadowed;
            }

            // A client-local mod's spawn is its own; nothing else spawns on a joined player.
            return inner.Spawn(request);
        }

        /// <summary>A joined player's <see cref="IRuntimeReplication.RequestSpawn"/>: a prediction or a shadow, made here through <paramref name="spawn"/>.</summary>
        private OperationResult<IEntity> RouteSpawnRequest(InternalSpawnDescriptor descriptor, Func<OperationResult<IEntity>> spawn)
        {
            var authority = JoinedAuthority;
            var intent = VisibleIntent;
            var route = ExecutionRouting.Route(NetRealm.Client, Profile, SourceOf(intent));
            if (authority == null || descriptor.Source != InternalSpawnSource.SdkSpawn ||
                (route != ExecutionRoute.Forwarded && route != ExecutionRoute.Shadow))
            {
                // Without the network world, as a test host composes the seam, a joined player's spawn runs here as single
                // player's would and is recorded as nothing.
                return spawn() ?? throw new InvalidOperationException("The spawn returned no result.");
            }

            var handler = HandlerFor(descriptor.Kind);
            if (route == ExecutionRoute.Forwarded)
            {
                if (handler == null) return OperationResult<IEntity>.Failure(ModErrorCode.NotAuthoritative, OnlyTheHostMakes);
                var refusal = authority.CheckPrediction();
                if (refusal != SpawnRefusal.None) return OperationResult<IEntity>.Failure(ModErrorCode.RateLimited, Refused(refusal));
            }
            else if (!authority.CanShadow)
            {
                return OperationResult<IEntity>.Failure(ModErrorCode.RateLimited, TooManyWaiting);
            }

            var made = spawn() ?? throw new InvalidOperationException("The spawn returned no result.");
            if (!made.TryGetValue(out var entity) || entity == null || !entity.IsAlive) return made;
            var description = descriptor.Payload.ToArray();
            if (route == ExecutionRoute.Forwarded)
            {
                authority.Predict(OwnerModId, descriptor.Kind, description, entity, entity as IDisposable, handler, SourceOf(intent),
                    intent != null && intent.IsInputFrame, descriptor.Transform, Ui);
            }
            else authority.Shadow(OwnerModId, descriptor.Kind, description, entity, entity as IDisposable, handler);
            return made;
        }

        /// <summary>A joined player's <see cref="IRuntimeReplication.RegisterCanonical"/> of an SDK spawn: a prediction or a shadow of what it already made.</summary>
        private OperationResult<bool> RouteRegistration(IEntity entity, InternalSpawnDescriptor descriptor)
        {
            var authority = JoinedAuthority;
            if (authority == null || authority.IsLocal(entity)) return OperationResult<bool>.Success(true);
            var intent = VisibleIntent;
            var route = ExecutionRouting.Route(NetRealm.Client, Profile, SourceOf(intent));
            var handler = HandlerFor(descriptor.Kind);
            var description = descriptor.Payload.ToArray();
            if (route == ExecutionRoute.Forwarded)
            {
                if (handler == null) return OperationResult<bool>.Failure(ModErrorCode.NotAuthoritative, OnlyTheHostMakes);
                var refusal = authority.CheckPrediction();
                if (refusal != SpawnRefusal.None) return OperationResult<bool>.Failure(ModErrorCode.RateLimited, Refused(refusal));
                authority.Predict(OwnerModId, descriptor.Kind, description, entity, entity as IDisposable, handler, SourceOf(intent),
                    intent != null && intent.IsInputFrame, descriptor.Transform, Ui);
                return OperationResult<bool>.Success(true);
            }

            if (route == ExecutionRoute.Shadow)
            {
                if (!authority.CanShadow) return OperationResult<bool>.Failure(ModErrorCode.RateLimited, TooManyWaiting);
                authority.Shadow(OwnerModId, descriptor.Kind, description, entity, entity as IDisposable, handler);
            }

            return OperationResult<bool>.Success(true);
        }

        /// <summary>
        /// This context's mod starts loading <paramref name="bundlePath"/>: a load of it the network world made gives way first,
        /// so the game does not refuse the mod's own load.
        /// </summary>
        internal void YieldBundle(string bundlePath)
        {
            // Off the game thread the asset service refuses the load anyway, and the network world is the game thread's alone.
            if (bundlePath == null || !UnityMainThreadGuard.IsCurrent) return;
            try
            {
                services.Authority?.YieldBundle(OwnerModId, bundlePath);
            }
            catch (Exception)
            {
                // The mod's load goes on whatever the network world does.
            }
        }

        /// <summary>Lists a bundle load this context's mod started, so a listen host makes players' spawns from its handle first.</summary>
        internal void ListBundleLoad(IAssetService inner, string bundlePath, System.Threading.Tasks.Task<OperationResult<IAssetBundle>> load)
        {
            try
            {
                if (TrackRelease()) services.AssetLoads.BundleLoading(OwnerModId, this, inner, bundlePath, load);
            }
            catch (Exception)
            {
                // The mod's load goes on whatever the list does.
            }
        }

        /// <summary>Lists a prefab load this context's mod started from one of its bundles.</summary>
        internal void ListPrefabLoad(IAssetService inner, IAssetBundle bundle, string assetName, System.Threading.Tasks.Task<OperationResult<IPrefabAsset>> load)
        {
            try
            {
                var bundlePath = bundle.RelativePath;
                if (bundlePath != null && TrackRelease()) services.AssetLoads.PrefabLoading(OwnerModId, this, inner, bundlePath, assetName, load);
            }
            catch (Exception)
            {
                // The mod's load goes on whatever the list does.
            }
        }

        private static IntentSource SourceOf(IntentRecord? intent) => intent == null ? IntentSource.None : intent.Source;

        /// <summary>What the mod hears when a prediction is refused before its spawn runs: past the quota, or not joined yet.</summary>
        private static string Refused(SpawnRefusal refusal) => refusal == SpawnRefusal.QuotaExceeded ? TooManyObjects : HostNotReady;

        /// <summary>The spawn handler this context's mod registered for <paramref name="kind"/>, or null; another mod's never serves.</summary>
        private IInternalSpawnHandler? HandlerFor(string kind) =>
            services.TryGetSpawnHandler(kind, out var handler, out var owner) &&
            (string.Equals(owner, OwnerModId, StringComparison.OrdinalIgnoreCase) || handler is IInternalSharedSpawnHandler)
                ? handler
                : null;
    }
}
