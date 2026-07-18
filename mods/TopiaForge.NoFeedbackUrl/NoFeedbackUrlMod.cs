using System;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.NoFeedbackUrl
{
    public sealed class NoFeedbackUrlMod : TopiaForgeMod
    {
        private static readonly ConfigDefinition<NoFeedbackUrlConfig> Config =
            new ConfigDefinition<NoFeedbackUrlConfig>(1, () => new NoFeedbackUrlConfig());

        private static bool allowFeedbackPageLaunchThisSession;
        private static IModLogger? logger;

        private IHarmonyLease? harmonyLease;

        /// <inheritdoc/>
        protected override void OnLoad()
        {
            logger = Context.Logger;
            allowFeedbackPageLaunchThisSession = ConfigureLaunchPolicy(Context);

            harmonyLease = Context.CreateHarmonyLease("feedback-url");

            var target = typeof(global::OpenFeedBackURL).GetMethod(
                "OpenFeedbackTask",
                BindingFlags.Public | BindingFlags.Static);
            if (target == null)
            {
                Context.Logger.Warn("Could not find OpenFeedBackURL.OpenFeedbackTask; feedback URL suppression is inactive.");
                return;
            }

            var prefix = typeof(NoFeedbackUrlMod).GetMethod(
                nameof(SuppressFeedbackTask),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (prefix == null)
            {
                Context.Logger.Warn("Could not find feedback URL suppression prefix; feedback URL suppression is inactive.");
                return;
            }

            harmonyLease.Patch(target, prefix: prefix);
            Context.Logger.Info("No Feedback URL loaded.");
        }

        /// <inheritdoc/>
        protected override void OnUnload()
        {
            try
            {
                harmonyLease?.Dispose();
                Context.Logger.Info("No Feedback URL unloaded.");
            }
            catch (Exception ex)
            {
                Context.Logger.Error(ex, "Failed to unpatch No Feedback URL.");
            }
            finally
            {
                harmonyLease = null;
                logger = null;
                allowFeedbackPageLaunchThisSession = false;
            }
        }

        private static bool SuppressFeedbackTask()
        {
            if (allowFeedbackPageLaunchThisSession)
            {
                logger?.Info("Allowing shutdown feedback page launch for the first game launch.");
                return true;
            }

            logger?.Info("Suppressed shutdown feedback page launch; first game launch has already occurred.");
            return false;
        }

        private static bool ConfigureLaunchPolicy(IModContext context)
        {
            var configResult = context.Config.Load(Config);
            if (!configResult.TryGetValue(out var config))
            {
                context.Logger.Error(
                    $"NoFeedbackUrl configuration could not be loaded ({configResult.ErrorCode}): {configResult.ErrorMessage}");
                return false;
            }

            if (config.HasSeenFirstLaunch)
            {
                context.Logger.Info("First game launch has already occurred. Shutdown feedback page launches will be suppressed.");
                return false;
            }

            config.HasSeenFirstLaunch = true;
            var saveResult = context.Config.Save(Config, config);
            if (!saveResult.Succeeded)
            {
                context.Logger.Warn(
                    $"Could not persist first-launch state ({saveResult.ErrorCode}): {saveResult.ErrorMessage}");
            }
            context.Logger.Info("First game launch detected. Shutdown feedback page launch will be allowed once this session.");
            return true;
        }
    }

}
