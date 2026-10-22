using System;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Chronos
{
    /// <summary>Publishes lifetime-owned time-control services.</summary>
    public sealed class ChronosMod : TopiaForgeMod
    {
        private TimeControlService? service;

        /// <inheritdoc />
        protected override void OnLoad()
        {
            // The runtime's seam gives Chronos the game's clock, the realm and the session's time; a context without one
            // is single player on Unity's own clock.
            var replication = (Context as IInternalReplicationContext)?.Replication;
            service = new TimeControlService(Context.Identity.Id, Context.Logger, Context.LocalPlayer, replication);
            Context.Lifetime.Track(service);
            var registration = Context.Extensions.Register<ITimeControlService>(service);
            if (!registration.Succeeded)
            {
                throw new InvalidOperationException(registration.ErrorMessage);
            }

            Context.Events.SubscribeUpdate(OnUpdate);
            Context.Events.SubscribeSceneLoaded(OnSceneLoaded);
            Context.Logger.Info("TopiaForge Chronos loaded; time-control extension registered.");
        }

        private void OnUpdate(float deltaTime)
        {
            var current = service;
            current?.Tick(current.ControlDeltaTime);
        }

        private void OnSceneLoaded(SceneLoadEvent scene)
        {
            if (scene.IsWorldReplacement)
            {
                service?.OnSceneChanged();
            }
        }
    }
}
