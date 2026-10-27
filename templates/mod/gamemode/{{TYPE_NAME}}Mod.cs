using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;

namespace {{ASSEMBLY_NAME}}
{
    /// <summary>Loads package services; the manifest declares the launch target and factory.</summary>
    public sealed class {{TYPE_NAME}}Mod : TopiaForgeMod
    {
        protected override void OnLoad() => Context.Logger.Info("{{DISPLAY_NAME}} package loaded.");
    }

    /// <summary>Starts one controller after the selected world and spawn are ready.</summary>
    public sealed class {{TYPE_NAME}}Gamemode : IGamemodeFactory
    {
        public Task<OperationResult<IGamemodeController>> StartAsync(
            IGamemodeSession session, CancellationToken cancellationToken)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            cancellationToken.ThrowIfCancellationRequested();
            session.CancellationToken.ThrowIfCancellationRequested();
            session.Lifetime.StoppingToken.ThrowIfCancellationRequested();
            return Task.FromResult(OperationResult<IGamemodeController>.Success(new {{TYPE_NAME}}Controller(session)));
        }
    }

    /// <summary>Owns one round; all service allocations use the supplied session context.</summary>
    internal sealed class {{TYPE_NAME}}Controller : IGamemodeController
    {
        private readonly IGamemodeSession session;
        private readonly IDisposable updateSubscription;
        private readonly IDisposable? pauseAction;
        private bool disposed;
#if TOPIAFORGE_AUTHORED_MAP
        private readonly IInteractableRegistration mapInteraction;
#endif

        public {{TYPE_NAME}}Controller(IGamemodeSession session)
        {
            this.session = session;
#if TOPIAFORGE_AUTHORED_MAP
            var map = session.World.RequireMap();
            // Replace this surveyed id when choosing a different object; anchors handle republished maps.
            var target = map.Entity("replace-with-surveyed-id");
            var registeredInteraction = session.Context.Interactions.Register(target.Entity,
                new InteractableDefinition("Inspect"), _ => session.Context.Ui.ShowToast("Object inspected.", UiTone.Neutral));
            if (!registeredInteraction.TryGetValue(out var interaction))
                throw new InvalidOperationException(registeredInteraction.ErrorMessage);
            mapInteraction = interaction;
#endif
            updateSubscription = session.Context.Events.SubscribeUpdate(OnUpdate);
            if (session.Context.TryGetExtension<IWorldPauseMenuService>(out var pause))
            {
                var registered = pause.RegisterAction(new WorldPauseAction(
                    session.GamemodeId + ".restart", "RESTART ROUND",
                    () => _ = RestartAsync(), destructive: true));
                if (registered.TryGetValue(out var action)) pauseAction = action;
                else session.Context.Logger.Warn("Restart action unavailable: " + registered.ErrorMessage);
            }
            session.Context.Logger.Info("{{DISPLAY_NAME}} session started in world " + session.WorldId + ".");
        }

        private async Task RestartAsync()
        {
            try
            {
                var result = await session.RestartAsync();
                if (!result.Succeeded && !session.CancellationToken.IsCancellationRequested)
                    session.Context.Ui.ShowToast(result.ErrorMessage, UiTone.Warning);
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { session.Context.Logger.Warn("Restart failed: " + error.Message); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var failures = new List<Exception>();
#if TOPIAFORGE_AUTHORED_MAP
            try { mapInteraction.Dispose(); } catch (Exception error) { failures.Add(error); }
#endif
            try { pauseAction?.Dispose(); } catch (Exception error) { failures.Add(error); }
            try { updateSubscription.Dispose(); } catch (Exception error) { failures.Add(error); }
            if (failures.Count != 0) throw new AggregateException(failures);
        }

        private void OnUpdate(float deltaTime)
        {
            if (disposed || session.CancellationToken.IsCancellationRequested) return;
            // Per-round timers, scoring and win conditions belong here. For robots,
            // add the RobotKit dependency and resolve its service from session.Context.
        }
    }
}
