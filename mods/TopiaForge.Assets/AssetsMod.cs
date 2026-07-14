using TopiaForge.Mods;

namespace TopiaForge.Assets
{
    public sealed class AssetsMod : ITopiaForgeMod
    {
        private IModContext? context;
        private AssetBundleService? service;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            service = new AssetBundleService(context.Logger);
            context.GetService<IModServiceRegistry>()?.Register<IAssetBundleService>(context.ModId, service);
            context.Logger.Info("TopiaForge Assets loaded; IAssetBundleService registered.");
        }

        public void OnUnload()
        {
            service?.Dispose();
            service = null;

            if (context != null)
            {
                context.GetService<IModServiceRegistry>()?.UnregisterOwner(context.ModId);
            }

            context = null;
        }
    }
}
