using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
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
            }

            context = null;
        }

        private void OnSceneLoaded(string sceneName)
        {
            context?.Logger.Info("Scene loaded: " + sceneName);
        }
    }
}
