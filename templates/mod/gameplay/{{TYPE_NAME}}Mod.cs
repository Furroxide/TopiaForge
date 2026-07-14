using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    public sealed class {{TYPE_NAME}}Mod : ITopiaForgeMod
    {
        private IModContext? context;
        private {{TYPE_NAME}}Controller? controller;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            var config = context.LoadConfig(new {{TYPE_NAME}}Config());
            config.Normalize();
            context.SaveConfig(config);

            controller = new {{TYPE_NAME}}Controller(config, context.Logger);
            context.Update += OnUpdate;
            context.SceneLoaded += OnSceneLoaded;
            context.Logger.Info("{{DISPLAY_NAME}} loaded.");
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
