using Robotopia.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>
    /// Publishes I{{TYPE_NAME}}Service to the shared service registry on load and withdraws it on unload, so
    /// other mods can resolve it with context.GetService&lt;I{{TYPE_NAME}}Service&gt;().
    /// </summary>
    public sealed class {{TYPE_NAME}}Mod : IRobotopiaMod
    {
        private IModContext? context;
        private {{TYPE_NAME}}Service? service;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            service = new {{TYPE_NAME}}Service(context.Logger);
            context.GetService<IModServiceRegistry>()?.Register<I{{TYPE_NAME}}Service>(context.ModId, service);
            context.Logger.Info("{{DISPLAY_NAME}} loaded; I{{TYPE_NAME}}Service registered.");
        }

        public void OnUnload()
        {
            if (context != null)
            {
                context.GetService<IModServiceRegistry>()?.UnregisterOwner(context.ModId);
            }

            service = null;
            context = null;
        }
    }
}
