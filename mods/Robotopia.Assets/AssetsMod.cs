using Robotopia.Mods;

namespace Robotopia.Assets
{
    public sealed class AssetsMod : IRobotopiaMod
    {
        private IModContext? context;
        private AssetBundleService? service;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            service = new AssetBundleService(context.Logger);
            context.GetService<IModServiceRegistry>()?.Register<IAssetBundleService>(context.ModId, service);
            context.Logger.Info("Robotopia Assets loaded; IAssetBundleService registered.");
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
