using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core.Multiplayer;

namespace TopiaForge.ModManager
{
    internal sealed partial class NetworkWorldClient
    {
        private readonly List<ClientWorldEntity> providerReplicas = new List<ClientWorldEntity>();
        private int providerCursor;

        internal TopiaForge.Mods.IEntity? ResolveViewProviderEntity(NetworkEntityId id) =>
            entities.TryGetValue(id.Value, out var mirrored) && mirrored.Binding == ClientEntityBinding.Factory
                && ReferenceEquals(factories(mirrored.Kind), mirrored.Factory) && mirrored.FactoryReplica?.IsAlive == true
                ? mirrored.FactoryReplica : null;

        private void TrackProvider(ClientWorldEntity mirrored)
        {
            if (mirrored.ProviderIndex >= 0) return;
            mirrored.ProviderIndex = providerReplicas.Count;
            providerReplicas.Add(mirrored);
        }

        private void UntrackProvider(ClientWorldEntity mirrored)
        {
            var index = mirrored.ProviderIndex;
            if (index < 0) return;
            var last = providerReplicas[providerReplicas.Count - 1];
            providerReplicas[index] = last;
            last.ProviderIndex = index;
            providerReplicas.RemoveAt(providerReplicas.Count - 1);
            mirrored.ProviderIndex = -1;
        }

        // Four inspected entries per frame, no idle allocation. Failed composition retries at most once per five
        // seconds per entry. Provider replacement and dead visuals release the old instance before any rebuild.
        private void PumpProviders(long now)
        {
            var count = Math.Min(4, providerReplicas.Count);
            for (var i = 0; i < count && providerReplicas.Count > 0; i++)
            {
                if (providerCursor >= providerReplicas.Count) providerCursor = 0;
                var mirrored = providerReplicas[providerCursor++];
                if (mirrored.Source != WorldEntrySource.SdkSpawn || mirrored.Kind == MultiplayerSpawnKinds.AssetPrefab
                    || mirrored.Binding != ClientEntityBinding.None && mirrored.Binding != ClientEntityBinding.Factory)
                { UntrackProvider(mirrored); continue; }
                try
                {
                    var factory = factories(mirrored.Kind);
                    if (mirrored.Binding == ClientEntityBinding.Factory &&
                        (!ReferenceEquals(factory, mirrored.Factory) || mirrored.FactoryReplica?.IsAlive != true || mirrored.Object?.IsAlive != true))
                        Release(mirrored);
                    if (mirrored.Binding != ClientEntityBinding.None || now < mirrored.ProviderRetryAt) continue;
                    mirrored.ProviderRetryAt = now + 5_000_000;
                    if (factory == null) continue;
                    mirrored.Unbuildable = false;
                    BuildWithFactory(mirrored);
                }
                catch (Exception)
                {
                    mirrored.ProviderRetryAt = now + 5_000_000;
                    Release(mirrored);
                    Unbuildable(mirrored);
                }
            }
        }
    }
}
