using System;
using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>Loads and spawns a package prefab using only the safe, owner-bound SDK.</summary>
    public sealed class {{TYPE_NAME}}Mod : TopiaForgeMod
    {
        private const string BundlePath = "assets/AssetBundles/{{BUNDLE_NAME}}.bundle";
        private const string PrefabName = "assets/myprefab.prefab";

        private bool loadInProgress;
        private ISpawnedEntity? spawned;

        protected override void OnLoad()
        {
            Context.Events.SubscribeSceneLoaded(_ => SpawnPrefab());
            Context.Logger.Info("{{DISPLAY_NAME}} is ready; its prefab will spawn in the next loaded scene.");
        }

        private async void SpawnPrefab()
        {
            var context = Context;
            if (loadInProgress || spawned?.IsAlive == true || context.Lifetime.IsStopping)
            {
                return;
            }

            loadInProgress = true;
            try
            {
                var bundleResult = await context.Assets.LoadBundleAsync(
                    BundlePath,
                    context.Lifetime.StoppingToken);
                if (!bundleResult.TryGetValue(out var bundle))
                {
                    if (!context.Lifetime.IsStopping)
                    {
                        context.Logger.Error("Could not load '" + BundlePath + "': " + bundleResult.ErrorMessage);
                    }
                    return;
                }

                var prefabResult = await context.Assets.LoadPrefabAsync(
                    bundle,
                    PrefabName,
                    context.Lifetime.StoppingToken);
                if (!prefabResult.TryGetValue(out var prefab))
                {
                    if (!context.Lifetime.IsStopping)
                    {
                        context.Logger.Error("Could not load prefab '" + PrefabName + "': " + prefabResult.ErrorMessage);
                    }
                    return;
                }

                var spawnResult = context.Assets.Spawn(new AssetSpawnRequest(prefab, TransformState.Identity));
                if (!spawnResult.TryGetValue(out spawned))
                {
                    if (!context.Lifetime.IsStopping)
                    {
                        context.Logger.Error("Could not spawn prefab '" + PrefabName + "': " + spawnResult.ErrorMessage);
                    }
                    return;
                }

                context.Logger.Info("Spawned '" + PrefabName + "'.");
            }
            catch (OperationCanceledException) when (context.Lifetime.IsStopping)
            {
                // Expected during unload; the owner lifetime releases every acquired handle.
            }
            catch (Exception exception)
            {
                context.Logger.Error(exception, "Unexpected asset spawn failure.");
            }
            finally
            {
                loadInProgress = false;
            }
        }
    }
}
