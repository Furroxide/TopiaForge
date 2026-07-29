using System;
using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>Publishes a dependency-scoped singleton extension for the current mod lifetime.</summary>
    public sealed class {{TYPE_NAME}}Mod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            var registration = Context.Extensions.Register<I{{TYPE_NAME}}Service>(
                new {{TYPE_NAME}}Service(Context.Logger));
            if (!registration.Succeeded)
            {
                throw new InvalidOperationException(
                    "Could not publish I{{TYPE_NAME}}Service: " + registration.ErrorMessage);
            }

            Context.Logger.Info("{{DISPLAY_NAME}} loaded; I{{TYPE_NAME}}Service registered.");
        }
    }
}
