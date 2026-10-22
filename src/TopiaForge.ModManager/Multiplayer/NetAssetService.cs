using System;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The network seam's asset service. Offline and on a listen host it changes no behaviour: every member forwards its
    /// arguments to the service the gameplay host made and returns exactly what that returns, failures and exceptions
    /// included, and each spawn that succeeds is recorded at spawn time, so that opening the session to other players later
    /// can baseline it. On a joined player a spawn is routed (WS3.7): on the player's intent it is a prediction the host is
    /// asked for, and scripted it is a shadow of the host's own; the mod holds the handle the gameplay host made either way.
    /// Every load is listed by the mod, so a listen host makes a player's spawn from the mod's own handles. It keeps
    /// <see cref="IParentAssetScope"/>: a session scope's service accepts its package's prefabs as it did before.
    /// </summary>
    internal sealed class NetAssetService : IAssetService, IParentAssetScope
    {
        private readonly IAssetService inner;

        internal NetAssetService(IAssetService inner, RuntimeReplication replication)
        {
            this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            Replication = replication ?? throw new ArgumentNullException(nameof(replication));
        }

        /// <summary>Gets the service the gameplay host made.</summary>
        internal IAssetService Inner => inner;

        /// <summary>Gets the replication seam of the context this service belongs to.</summary>
        internal RuntimeReplication Replication { get; }

        /// <summary>
        /// Attaches the package's own asset service as this scope's parent. The parent is decorated too, so the service the
        /// host made for it is the one passed on: the host's own check of the relationship sees exactly what it saw before.
        /// A host service without parent scopes is left as it was.
        /// </summary>
        void IParentAssetScope.AttachParent(IAssetService parent)
        {
            var source = parent is NetAssetService decorated ? decorated.inner : parent;
            if (inner is IParentAssetScope scope) scope.AttachParent(source);
        }

        public Task<OperationResult<IAssetBundle>> LoadBundleAsync(string relativePath, CancellationToken cancellationToken = default)
        {
            var load = inner.LoadBundleAsync(relativePath, cancellationToken);
            if (load != null && relativePath != null) Replication.ListBundleLoad(inner, relativePath, load);
            return load!;
        }

        public Task<OperationResult<IPrefabAsset>> LoadPrefabAsync(IAssetBundle bundle, string assetName, CancellationToken cancellationToken = default)
        {
            var load = inner.LoadPrefabAsync(bundle, assetName, cancellationToken);
            if (load != null && bundle != null && assetName != null) Replication.ListPrefabLoad(inner, bundle, assetName, load);
            return load!;
        }

        public OperationResult<ISpawnedEntity> Spawn(AssetSpawnRequest request)
        {
            var authority = Replication.JoinedAuthority;
            if (authority != null) return Replication.RouteAssetSpawn(request, inner, authority);
            var result = inner.Spawn(request);
            if (result.TryGetValue(out var spawned)) Replication.RecordAssetSpawn(spawned, request);
            return result;
        }
    }
}
