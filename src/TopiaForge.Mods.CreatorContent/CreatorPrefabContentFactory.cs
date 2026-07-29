using System;

namespace TopiaForge.Mods
{
    /// <summary>Safe convenience factory for a prefab owned by the registering mod.</summary>
    public sealed class CreatorPrefabContentFactory : ICreatorContentFactory
    {
        private readonly IAssetService assets;
        private readonly IEntityService entities;
        private readonly IPrefabAsset prefab;

        /// <summary>Creates a prefab-backed creator factory.</summary>
        public CreatorPrefabContentFactory(IAssetService assets, IEntityService entities, IPrefabAsset prefab)
        {
            this.assets = assets ?? throw new ArgumentNullException(nameof(assets));
            this.entities = entities ?? throw new ArgumentNullException(nameof(entities));
            this.prefab = prefab ?? throw new ArgumentNullException(nameof(prefab));
        }

        /// <inheritdoc />
        public OperationResult<ICreatorSourceInstance> Spawn(TransformState transform)
        {
            if (!prefab.IsAlive)
            {
                return OperationResult<ICreatorSourceInstance>.Failure(
                    ModErrorCode.InvalidState,
                    "The registered prefab is no longer alive.");
            }

            var result = assets.Spawn(new AssetSpawnRequest(prefab, transform));
            if (!result.TryGetValue(out var spawned))
            {
                return OperationResult<ICreatorSourceInstance>.Failure(result.ErrorCode, result.ErrorMessage);
            }

            return OperationResult<ICreatorSourceInstance>.Success(
                new PrefabSourceInstance(spawned, entities));
        }

        private sealed class PrefabSourceInstance : ICreatorSourceInstance
        {
            private ISpawnedEntity? spawned;
            private readonly IEntityService entities;

            public PrefabSourceInstance(ISpawnedEntity spawned, IEntityService entities)
            {
                this.spawned = spawned;
                this.entities = entities;
            }

            public IEntity Entity => spawned ?? throw new ObjectDisposedException(nameof(PrefabSourceInstance));
            public bool IsAlive => spawned?.IsAlive == true;

            public bool TryGetTransform(out TransformState transform)
            {
                var current = spawned;
                if (current == null)
                {
                    transform = TransformState.Identity;
                    return false;
                }
                return entities.TryGetTransform(current, out transform);
            }

            public OperationResult<TransformState> SetTransform(TransformState transform)
            {
                var current = spawned;
                return current == null
                    ? OperationResult<TransformState>.Failure(ModErrorCode.InvalidState, "The creator instance was disposed.")
                    : entities.SetTransform(current, transform);
            }

            public void Dispose()
            {
                var current = spawned;
                spawned = null;
                current?.Dispose();
            }
        }
    }
}
