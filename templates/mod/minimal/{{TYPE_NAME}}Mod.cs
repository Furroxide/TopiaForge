using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>A small utility mod demonstrating validated config, logging, and commands.</summary>
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

            var command = Context.Commands.Register(
                new CommandDefinition(
                    "greet",
                    "Prints this mod's configured greeting."),
                _ =>
                {
                    Context.Logger.Info(config.Greeting);
                    return OperationResult<string>.Success(config.Greeting);
                });
            if (!command.Succeeded)
            {
                Context.Logger.Error(
                    "Command registration failed (" + command.ErrorCode + "): " + command.ErrorMessage);
                return;
            }

            Context.Logger.Info("{{DISPLAY_NAME}} loaded. Run '{{MOD_ID}}:greet' to try its command.");
        }
    }
}
