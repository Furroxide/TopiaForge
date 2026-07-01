using System;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using Robotopia.Mods;

namespace Robotopia.NoFeedbackUrl
{
    public sealed class NoFeedbackUrlMod : IRobotopiaMod
    {
        private const string HarmonyId = "robotopia.no-feedback-url.harmony";

        private static bool allowFeedbackPageLaunchThisSession;
        private static IModLogger? logger;

        private IModContext? context;
        private Harmony? harmony;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            logger = context.Logger;
            allowFeedbackPageLaunchThisSession = ConfigureLaunchPolicy(context);

            harmony = new Harmony(HarmonyId);

            var target = typeof(global::OpenFeedBackURL).GetMethod(
                "OpenFeedbackTask",
                BindingFlags.Public | BindingFlags.Static);
            if (target == null)
            {
                context.Logger.Warn("Could not find OpenFeedBackURL.OpenFeedbackTask; feedback URL suppression is inactive.");
                return;
            }

            var prefix = typeof(NoFeedbackUrlMod).GetMethod(
                nameof(SuppressFeedbackTask),
                BindingFlags.NonPublic | BindingFlags.Static);
            if (prefix == null)
            {
                context.Logger.Warn("Could not find feedback URL suppression prefix; feedback URL suppression is inactive.");
                return;
            }

            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            context.Logger.Info("No Feedback URL loaded.");
        }

        public void OnUnload()
        {
            try
            {
                harmony?.UnpatchSelf();
                context?.Logger.Info("No Feedback URL unloaded.");
            }
            catch (Exception ex)
            {
                context?.Logger.Error(ex, "Failed to unpatch No Feedback URL.");
            }
            finally
            {
                harmony = null;
                context = null;
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
            var config = context.LoadConfig(new NoFeedbackUrlConfig());
            if (config.HasSeenFirstLaunch)
            {
                context.Logger.Info("First game launch has already occurred. Shutdown feedback page launches will be suppressed.");
                return false;
            }

            config.HasSeenFirstLaunch = true;
            context.SaveConfig(config);
            context.Logger.Info("First game launch detected. Shutdown feedback page launch will be allowed once this session.");
            return true;
        }
    }

    [DataContract]
    public sealed class NoFeedbackUrlConfig
    {
        [DataMember(Name = "hasSeenFirstLaunch")]
        public bool HasSeenFirstLaunch { get; set; }
    }
}
