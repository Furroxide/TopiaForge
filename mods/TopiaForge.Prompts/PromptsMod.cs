using TopiaForge.Mods;

namespace TopiaForge.Prompts
{
    /// <summary>Publishes the owner-bound prompt override extension.</summary>
    public sealed class PromptsMod : TopiaForgeMod
    {
        /// <inheritdoc />
        protected override void OnLoad()
        {
            var registry = new PromptOverrideRegistry(Context.Identity.Id);
            Context.Lifetime.Track(registry);
            var registration = Context.Extensions.Register<IPromptOverrideRegistry>(registry);
            if (!registration.Succeeded)
            {
                throw new System.InvalidOperationException(registration.ErrorMessage);
            }

            Context.Logger.Info("TopiaForge Prompts loaded; prompt override extension registered.");
        }
    }
}
