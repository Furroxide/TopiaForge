using Robotopia.Mods;

namespace Robotopia.Prompts
{
    public sealed class PromptsMod : IRobotopiaMod
    {
        private IModContext? context;
        private PromptOverrideRegistry? registry;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            registry = new PromptOverrideRegistry();
            context.GetService<IModServiceRegistry>()?.Register<IPromptOverrideRegistry>(context.ModId, registry);
            context.Logger.Info("Robotopia Prompts loaded; IPromptOverrideRegistry registered.");
        }

        public void OnUnload()
        {
            registry?.UnregisterOwner(context?.ModId ?? string.Empty);
            registry = null;

            if (context != null)
            {
                context.GetService<IModServiceRegistry>()?.UnregisterOwner(context.ModId);
            }

            context = null;
        }
    }
}
