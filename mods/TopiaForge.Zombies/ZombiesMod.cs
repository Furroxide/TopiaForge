using System;
using TopiaForge.Mods;

namespace TopiaForge.Zombies
{
    /// <summary>Owns persistent Zombies configuration and commands; the declared factory owns each run.</summary>
    public sealed class ZombiesMod : TopiaForgeMod
    {
        /// <summary>Gets the stable Zombies gamemode id.</summary>
        public const string GamemodeId = "io.github.furroxide.topiaforge.zombies.survival";
        private static readonly ConfigDefinition<ZombiesConfig> ConfigContract = new ConfigDefinition<ZombiesConfig>(
            3, () => new ZombiesConfig(), validate: null, migrate: (storedVersion, value) =>
            {
                value.MigrateFrom(storedVersion);
                return OperationResult<ZombiesConfig>.Success(value);
            });

        /// <inheritdoc />
        protected override void OnLoad()
        {
            ApplyAccessibility(Context, LoadConfiguration(Context));
            Register("zombies-restart", "Restart the current Zombies run.", target => target.Restart());
            Register("zombies-stand-down", "Temporarily halt the infected robot horde.", target => target.StandDown());
            Register("zombies-status", "Describe the current Zombies run.", target => target.Status());
            Context.Logger.Info("Zombies configuration and commands loaded; gameplay starts through its declared launch target.");
        }

        internal static ZombiesConfig LoadConfiguration(IModContext context)
        {
            var loaded = context.Config.Load(ConfigContract);
            if (!loaded.TryGetValue(out var value))
            {
                context.Logger.Warn("Zombies config could not be loaded: " + loaded.ErrorMessage);
                value = new ZombiesConfig();
            }
            value.Normalize();
            var saved = context.Config.Save(ConfigContract, value);
            if (!saved.Succeeded) context.Logger.Warn("Zombies config normalization could not be saved: " + saved.ErrorMessage);
            return value;
        }

        internal static void ApplyAccessibility(IModContext context, ZombiesConfig config)
        {
            var current = context.Ui.Accessibility;
            var result = context.Ui.ApplyAccessibility(new UiAccessibilityPreferences(config.HudHighContrast, config.HudScale,
                current.ReducedMotion || config.HudMotionIntensity <= 0f, config.HudMotionIntensity));
            if (!result.Succeeded) context.Diagnostics.Report(new DiagnosticEntry("ZOMBIES_ACCESSIBILITY_UNAVAILABLE",
                "Zombies could not apply its configured UI accessibility profile.", DiagnosticSeverity.Warning, result.ErrorMessage));
        }

        private void Register(string name, string description, Func<ZombiesSessionCommands, OperationResult<string>> handler)
        {
            var result = Context.Commands.Register(new CommandDefinition(name, description), invocation =>
                Context.TryGetExtension<ZombiesSessionCommands>(out var target)
                    ? handler(target) : ZombiesSessionCommands.Inactive());
            if (!result.Succeeded) Context.Logger.Warn("Could not register /" + name + ": " + result.ErrorMessage);
        }
    }
}
