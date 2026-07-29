using System;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.Chronos
{
    /// <summary>Publishes lifetime-owned time-control services.</summary>
    public sealed class ChronosMod : TopiaForgeMod
    {
        private TimeControlService? service;

        /// <inheritdoc />
        protected override void OnLoad()
        {
            service = new TimeControlService(Context.Identity.Id, Context.Logger, Context.LocalPlayer);
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

        private void OnUpdate(float deltaTime) => service?.Tick(Time.unscaledDeltaTime);

        private void OnSceneLoaded(SceneLoadEvent scene)
        {
            if (scene.IsWorldReplacement)
            {
                service?.OnSceneChanged();
            }
        }
    }
}
