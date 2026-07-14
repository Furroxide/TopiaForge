using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>
    /// Loads an AssetBundle shipped inside this package (built by the scaffolded unity-companion project into
    /// assets/AssetBundles) via the io.github.furroxide.topiaforge.assets framework service, then spawns a prefab from it.
    /// </summary>
    public sealed class {{TYPE_NAME}}Mod : ITopiaForgeMod
    {
        private IModContext? context;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            context.SceneLoaded += OnSceneLoaded;
            context.Logger.Info("{{DISPLAY_NAME}} loaded.");
        }

        public void OnUnload()
        {
            if (context != null)
            {
                context.SceneLoaded -= OnSceneLoaded;
                // Release every bundle/object this mod loaded through the Assets service.
                context.GetService<IAssetBundleService>()?.UnloadOwner(context.ModId);
            }

            context = null;
        }

        private void OnSceneLoaded(string sceneName)
        {
            if (context == null)
            {
                return;
            }

            // Example load: a bundle at assets/AssetBundles/{{MOD_ID}} inside this package. Build it from the
            // unity-companion project, then uncomment:
            //
            // var bundle = context.LoadAssetBundle("assets/AssetBundles/{{MOD_ID}}");
            // if (bundle.Succeeded)
            // {
            //     var prefab = context.LoadAsset<UnityEngine.GameObject>(bundle.Handle!, "MyPrefab");
            //     if (prefab.Succeeded)
            //     {
            //         context.SpawnAsset(prefab.Asset!);
            //     }
            // }
        }
    }
}
