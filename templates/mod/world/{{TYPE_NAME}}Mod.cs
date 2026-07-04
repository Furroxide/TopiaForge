using Robotopia.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>
    /// Registers a custom world whose content is a Unity prefab shipped in this package's AssetBundle
    /// (built from the paired Unity project by `robotopia world build`). Launching the world loads the
    /// game's clean play stage and places the prefab at the player spawn; a menu entry pairing it with
    /// the Sandbox gamemode is registered too, so it shows up under GAMEMODES.
    /// </summary>
    public sealed class {{TYPE_NAME}}Mod : IRobotopiaMod
    {
        private const string WorldId = "{{MOD_ID}}.world";

        private IModContext? context;
        private IWorldGamemodeService? worlds;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            worlds = context.RequireService<IWorldGamemodeService>();
            context.RegisterWorldFromBundle(worlds, new BundleWorldOptions
            {
                Id = WorldId,
                Name = "{{DISPLAY_NAME}}",
                Description = "A custom world.",
                // The bundle `robotopia world build` drops into this package.
                BundleRelativePath = "AssetBundles/{{BUNDLE_NAME}}.bundle",
                // PrefabAssetName omitted: the bundle's single prefab is used.
                // Content = new CustomWorldOptions { SpawnPointName = "SpawnPoint", KillPlaneDepth = 100f, ... }
            });
            context.Logger.Info("{{DISPLAY_NAME}} world registered.");
        }

        public void OnUnload()
        {
            worlds?.UnregisterWorld(WorldId);
            if (context != null)
            {
                // Release the bundle and everything loaded from it.
                context.GetService<IAssetBundleService>()?.UnloadOwner(context.ModId, unloadAllLoadedObjects: true);
            }

            worlds = null;
            context = null;
        }
    }
}
