using TopiaForge.Mods;

namespace TopiaForge.ValidTestMod
{
    /// <summary>
    /// A mod that spawns a crate through its asset service each time a scene loads, where its data folder holds
    /// <c>spawn-crates.txt</c>. The network world's runtime tests load it in every game and mark only the host's, so only
    /// the host spawns: what a joined player sees of it comes from the host.
    /// </summary>
    public sealed class RuntimeWorldSpawningMod : TopiaForgeMod
    {
        private IPrefabAsset? crate;
        private int spawned;

        protected override void OnLoad()
        {
            var spawns = Context.Files.DataFileExists("spawn-crates.txt");
            RuntimeTrace.Write(spawns ? "world-spawner" : "world-watcher");
            if (spawns) Context.Events.SubscribeSceneLoaded((SceneLoadEvent scene) => Spawn(scene.SceneName));
        }

        private void Spawn(string sceneName)
        {
            crate ??= LoadCrate();
            if (crate == null)
            {
                RuntimeTrace.Write("world-load-failed");
                return;
            }

            var at = new TransformState(new Vec3(10f + 3f * spawned, 1f, 0f), Quat.Identity, new Vec3(1f, 1f, 1f));
            spawned++;
            var result = Context.Assets.Spawn(new AssetSpawnRequest(crate, at));
            RuntimeTrace.Write(result.Succeeded ? "world-spawned:" + sceneName : "world-spawn-failed:" + result.ErrorCode);
        }

        private IPrefabAsset? LoadCrate()
        {
            // The tests' asset service loads at once, as a bundle already in memory does; a load still running is a failure.
            var bundle = Context.Assets.LoadBundleAsync("assets/world.bundle");
            if (!bundle.IsCompleted || !bundle.Result.TryGetValue(out var loaded) || loaded == null) return null;
            var prefab = Context.Assets.LoadPrefabAsync(loaded, "Crate");
            return prefab.IsCompleted && prefab.Result.TryGetValue(out var asset) ? asset : null;
        }
    }
}
