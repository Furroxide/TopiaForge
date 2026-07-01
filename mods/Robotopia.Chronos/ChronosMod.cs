using Robotopia.Mods;
using UnityEngine;

namespace Robotopia.Chronos
{
    // Framework mod that publishes ITimeControlService — the single, leak-proof authority over game time. Mirrors the
    // RobotKit lifecycle discipline (OnLoad-register / Update-tick / SceneLoaded-reset / OnUnload-unregister+dispose).
    public sealed class ChronosMod : IRobotopiaMod
    {
        private IModContext? context;
        private TimeControlService? service;

        public void OnLoad(IModContext context)
        {
            this.context = context;
            service = new TimeControlService(context.ModId, context.Logger);

            var registry = context.GetService<IModServiceRegistry>();
            registry?.Register<ITimeControlService>(context.ModId, service);

            context.Update += OnUpdate;
            context.SceneLoaded += OnSceneLoaded;
            context.Logger.Info("Robotopia Chronos loaded; ITimeControlService registered (single-owner timeScale, leak-proof leases, Superhot/RTwP/turn-based ready).");
        }

        public void OnUnload()
        {
            if (context != null)
            {
                context.Update -= OnUpdate;
                context.SceneLoaded -= OnSceneLoaded;
                context.GetService<IModServiceRegistry>()?.UnregisterOwner(context.ModId);
            }

            service?.Dispose();
            service = null;
            context = null;
        }

        // The control plane (drivers + scheduler) runs on the UNSCALED clock — the runtime's deltaTime may already be
        // scaled by our own timeScale, which would stall the ramp as the world slows.
        private void OnUpdate(float deltaTime) => service?.Tick(Time.unscaledDeltaTime);

        private void OnSceneLoaded(string sceneName) => service?.OnSceneChanged();
    }
}
