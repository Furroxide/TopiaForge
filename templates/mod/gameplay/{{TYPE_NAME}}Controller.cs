using System;
using Robotopia.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>
    /// Per-frame gameplay logic, kept out of the mod entry class so it is easy to test and dispose. Read input,
    /// drive physics/HUD state here.
    /// </summary>
    internal sealed class {{TYPE_NAME}}Controller : IDisposable
    {
        private readonly {{TYPE_NAME}}Config config;
        private readonly IModLogger logger;

        public {{TYPE_NAME}}Controller({{TYPE_NAME}}Config config, IModLogger logger)
        {
            this.config = config;
            this.logger = logger;
        }

        public void Update(float deltaTime)
        {
            // Called every frame while the mod is loaded.
        }

        public void OnSceneLoaded(string sceneName)
        {
            logger.Debug("{{DISPLAY_NAME}}: scene loaded " + sceneName);
        }

        public void Dispose()
        {
            // Release any scene objects or handlers acquired in Update.
        }
    }
}
