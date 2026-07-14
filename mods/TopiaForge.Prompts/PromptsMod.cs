using TopiaForge.Mods;

namespace TopiaForge.Prompts
{
    public sealed class PromptsMod : ITopiaForgeMod
    {
        private IModContext? context;
        private PromptOverrideRegistry? registry;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            registry = new PromptOverrideRegistry();
            context.GetService<IModServiceRegistry>()?.Register<IPromptOverrideRegistry>(context.ModId, registry);
            context.Logger.Info("TopiaForge Prompts loaded; IPromptOverrideRegistry registered.");
        }

        public void OnUnload()
        {
            registry?.Dispose();
            registry = null;

            if (context != null)
            {
                context.GetService<IModServiceRegistry>()?.UnregisterOwner(context.ModId);
            }

            context = null;
        }
    }
}
