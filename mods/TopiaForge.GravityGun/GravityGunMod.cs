using TopiaForge.Mods;

namespace TopiaForge.GravityGun
{
    /// <summary>TopiaForge Gravity Gun entry point.</summary>
    public sealed class GravityGunMod : TopiaForgeMod
    {
        private static readonly ConfigDefinition<GravityGunConfig> Config =
            new ConfigDefinition<GravityGunConfig>(1, CreateConfig);

        /// <inheritdoc/>
        protected override void OnLoad()
        {
            var result = Context.Config.Load(Config);
            if (!result.TryGetValue(out var config))
            {
                Context.Logger.Error(
                    $"Gravity Gun configuration could not be loaded ({result.ErrorCode}): {result.ErrorMessage}");
                return;
            }

            _ = new GravityGunController(Context, config);
            Context.Logger.Info(
                "Gravity Gun loaded. Hold right mouse to grab, scroll to adjust distance, and press left mouse to throw.");
        }

        private static GravityGunConfig CreateConfig()
        {
            var config = new GravityGunConfig();
            config.Normalize();
            return config;
        }
    }
}
