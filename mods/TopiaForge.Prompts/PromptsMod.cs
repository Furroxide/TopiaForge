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

            var nativeBridge = NativeRobotPromptBridge.TryInstall(Context, registry);
            if (nativeBridge != null)
            {
                Context.Lifetime.Track(nativeBridge);
            }

            Context.Logger.Info(
                "TopiaForge Prompts loaded; prompt override extension registered" +
                (nativeBridge == null ? " (native robot bridge degraded)." : "."));
        }
    }
}
