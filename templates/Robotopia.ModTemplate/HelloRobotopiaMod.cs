using System.Runtime.Serialization;
using Robotopia.Mods;

namespace Robotopia.SampleHelloMod
{
    public sealed class HelloRobotopiaMod : IRobotopiaMod
    {
        private SampleConfig config = new SampleConfig();
        private float elapsed;

        public void OnLoad(IModContext context)
        {
            config = context.LoadConfig(new SampleConfig());
            context.Logger.Info("Hello mod loaded. Message: " + config.Message);

            // Optional framework services:
            // - Add robotopia.assets to robotopia.mod.json before calling context.LoadAssetBundle/LoadAsset/SpawnAsset.
            // - Add robotopia.prompts to robotopia.mod.json before calling context.RegisterPromptOverride.
            // Example:
            // var prompt = context.RegisterPromptOverride("robot.greeting", "Replacement prompt text.", priority: 10);
            //
            // Want in-game UI (windows, HUD bars, toasts)? Reference Robotopia.Mods.UnityUi.dll and see
            // docs/UiKit.md — the Robotopia.UiGallery dev mod (F8 in-game) is the living catalog.

            context.SceneLoaded += scene => context.Logger.Info("Scene loaded: " + scene);
            context.Update += deltaTime =>
            {
                elapsed += deltaTime;
                if (elapsed >= config.LogEverySeconds)
                {
                    elapsed = 0f;
                    context.Logger.Debug(config.Message);
                }
            };
        }

        public void OnUnload()
        {
        }
    }

    [DataContract]
    public sealed class SampleConfig
    {
        [DataMember(Name = "message")]
        public string Message { get; set; } = "Hello from a Robotopia mod.";

        [DataMember(Name = "logEverySeconds")]
        public float LogEverySeconds { get; set; } = 30f;
    }
}
