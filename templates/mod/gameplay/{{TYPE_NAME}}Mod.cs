using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>Input-driven aim scanner built entirely on safe SDK services.</summary>
    public sealed class {{TYPE_NAME}}Mod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            var loaded = Context.Config.Load({{TYPE_NAME}}Config.Definition);
            if (!loaded.TryGetValue(out var config))
            {
                Context.Logger.Error(
                    "Config could not be loaded (" + loaded.ErrorCode + "): " + loaded.ErrorMessage);
                return;
            }

            var controller = new {{TYPE_NAME}}Controller(Context, config);
            if (!controller.IsActive)
            {
                return;
            }

            Context.Logger.Info(
                "{{DISPLAY_NAME}} loaded. Press " + config.ActionKey + " to scan the entity under your aim.");
        }
    }
}
