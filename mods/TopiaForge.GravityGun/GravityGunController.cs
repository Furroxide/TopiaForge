using System;
using TopiaForge.Mods;

namespace TopiaForge.GravityGun
{
    /// <summary>
    /// SDK-only gameplay controller used as the V1 acceptance example for named input, player aim, physics queries,
    /// opaque entities, fixed-step motion, and lifetime cleanup.
    /// </summary>
    internal sealed class GravityGunController : IDisposable
    {
        private readonly GravityGunConfig config;
        private readonly ILocalPlayerService player;
        private readonly IEntityService entities;
        private readonly IPhysicsService physics;
        private readonly IModLogger logger;
        private readonly IInputAction? grab;
        private readonly IInputAction? throwAction;
        private readonly IInputAction? distance;
        private IEntityMotion? held;
        private float holdDistance;
        private bool disposed;

        public GravityGunController(IModContext context, GravityGunConfig config)
            : this(
                config,
                context?.Input ?? throw new ArgumentNullException(nameof(context)),
                context.LocalPlayer,
                context.Entities,
                context.Physics,
                context.Events,
                context.Lifetime,
                context.Logger)
        {
        }

        internal GravityGunController(
            GravityGunConfig config,
            IInputService input,
            ILocalPlayerService player,
            IEntityService entities,
            IPhysicsService physics,
            IModEvents events,
            IModLifetime lifetime,
            IModLogger logger)
        {
            this.config = config ?? throw new ArgumentNullException(nameof(config));
            this.player = player ?? throw new ArgumentNullException(nameof(player));
            this.entities = entities ?? throw new ArgumentNullException(nameof(entities));
            this.physics = physics ?? throw new ArgumentNullException(nameof(physics));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
            holdDistance = config.DefaultHoldDistance;

            grab = RegisterAction(input, logger, new InputActionDefinition(
                "grab",
                "Grab or release object",
                new[] { InputBinding.MouseButton(InputMouseButton.Secondary) }));
            throwAction = RegisterAction(input, logger, new InputActionDefinition(
                "throw",
                "Throw held object",
                new[] { InputBinding.MouseButton(InputMouseButton.Primary) }));
            distance = RegisterAction(input, logger, new InputActionDefinition(
                "hold-distance",
                "Adjust hold distance",
                new[] { InputBinding.Axis(InputAxis.Scroll) }));

            events.SubscribeUpdate(OnFrame);
            events.SubscribeFixedUpdate(OnFixedUpdate);
            events.SubscribeSceneLoaded(OnSceneLoaded);
            lifetime.Track(this);
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Release();
        }

        private void OnFrame(float deltaTime)
        {
            if (disposed)
            {
                return;
            }

            var distanceValue = distance?.Value ?? 0f;
            if (Math.Abs(distanceValue) > 0.0001f)
            {
                holdDistance = Clamp(
                    holdDistance + distanceValue * config.ScrollStep,
                    config.MinHoldDistance,
                    config.MaxHoldDistance);
            }

            if (held == null)
            {
                if (grab?.WasPressed == true)
                {
                    TryAcquire();
                }

                return;
            }

            if (!held.IsAlive || grab?.WasReleased == true || grab?.IsHeld == false)
            {
                Release();
                return;
            }

            if (throwAction?.WasPressed == true)
            {
                ThrowHeld();
            }
        }

        private static IInputAction? RegisterAction(
            IInputService input,
            IModLogger logger,
            InputActionDefinition definition)
        {
            var result = input.RegisterAction(definition);
            if (result.TryGetValue(out var action))
            {
                return action;
            }

            logger.Warn(
                "Gravity Gun input '" + definition.Name + "' is unavailable (" +
                result.ErrorCode + "): " + result.ErrorMessage);
            return null;
        }

        private void OnFixedUpdate(GameTimeSample time)
        {
            if (disposed || held == null)
            {
                return;
            }

            if (!held.IsAlive || !player.TryGetSnapshot(out var snapshot) || snapshot == null)
            {
                Release();
                return;
            }

            var responsiveness = Clamp(config.PullStrength * 0.08f, 2f, 30f);
            var result = held.MoveToward(
                snapshot.AimRay.GetPoint(holdDistance),
                responsiveness,
                config.Damping,
                config.MaxVelocity,
                time.DeltaTime > 0f ? time.DeltaTime : 0.02f);
            if (!result.Succeeded)
            {
                logger.Debug("Gravity Gun released its target: " + result.ErrorMessage);
                Release();
            }
        }

        private void OnSceneLoaded(string sceneName)
        {
            Release();
            logger.Debug("Gravity Gun scene refresh: " + sceneName);
        }

        private void TryAcquire()
        {
            if (!player.TryGetSnapshot(out var snapshot) || snapshot == null ||
                !physics.TryRaycast(snapshot.AimRay, config.MaxRange, out var hit) || hit == null)
            {
                return;
            }

            var result = entities.AcquireMotion(hit.Entity);
            if (!result.TryGetValue(out var motion))
            {
                logger.Debug("Gravity Gun target cannot be controlled: " + result.ErrorMessage);
                return;
            }

            held = motion;
            holdDistance = Clamp(hit.Distance, config.MinHoldDistance, config.MaxHoldDistance);
            logger.Debug("Gravity Gun acquired target: " + hit.Entity.Name);
        }

        private void ThrowHeld()
        {
            if (held == null || !player.TryGetSnapshot(out var snapshot) || snapshot == null)
            {
                Release();
                return;
            }

            var current = held;
            held = null;
            var result = current.Throw(snapshot.AimRay.Direction, config.ThrowVelocity);
            current.Dispose();
            if (!result.Succeeded)
            {
                logger.Debug("Gravity Gun could not throw its target: " + result.ErrorMessage);
            }
        }

        private void Release()
        {
            var current = held;
            held = null;
            current?.Dispose();
        }

        private static float Clamp(float value, float minimum, float maximum)
        {
            return value < minimum ? minimum : value > maximum ? maximum : value;
        }
    }
}
