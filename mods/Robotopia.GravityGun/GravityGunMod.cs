using Robotopia.Mods;

namespace Robotopia.GravityGun
{
    public sealed class GravityGunMod : IRobotopiaMod
    {
        private IModContext? context;
        private GravityGunController? controller;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            var config = context.LoadConfig(new GravityGunConfig());
            config.Normalize();
            context.SaveConfig(config);

            controller = new GravityGunController(config, context.Logger);
            context.Update += OnUpdate;
            context.SceneLoaded += OnSceneLoaded;
            context.Logger.Info("Gravity Gun loaded. Hold right mouse to grab rigidbodies, scroll to adjust distance, left mouse to throw.");
        }

        public void OnUnload()
        {
            if (context != null)
            {
                context.Update -= OnUpdate;
                context.SceneLoaded -= OnSceneLoaded;
            }

            controller?.Dispose();
            controller = null;
            context = null;
        }

        private void OnUpdate(float deltaTime)
        {
            controller?.Update(deltaTime);
        }

        private void OnSceneLoaded(string sceneName)
        {
            controller?.OnSceneLoaded(sceneName);
        }
    }
}
