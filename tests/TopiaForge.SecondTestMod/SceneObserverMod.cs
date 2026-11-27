using System;
using System.IO;
using TopiaForge.Mods;

namespace TopiaForge.SecondTestMod
{
    public sealed class SceneObserverMod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            Context.Events.SubscribeSceneLifecycle(scene =>
            {
                var path = Environment.GetEnvironmentVariable("TOPIAFORGE_RUNTIME_TEST_TRACE");
                if (!string.IsNullOrWhiteSpace(path))
                    File.AppendAllText(path, "scene-lifecycle:second:" + scene.Phase + Environment.NewLine);
            });
        }
    }
}
