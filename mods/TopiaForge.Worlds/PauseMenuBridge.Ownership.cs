using System;
using TopiaForge.Mods;

namespace TopiaForge.Worlds
{
    internal sealed partial class PauseMenuBridge
    {
        private sealed class ActionRegistration : IDisposable
        {
            private readonly PauseMenuBridge owner;
            private bool disposed;

            public ActionRegistration(PauseMenuBridge owner, WorldPauseAction action)
            {
                this.owner = owner;
                Action = action;
            }

            public WorldPauseAction Action { get; }

            public void Dispose()
            {
                if (disposed)
                {
                    return;
                }

                disposed = true;
                owner.RemoveAction(this);
            }
        }

        private sealed class ExitInterceptorLease : IDisposable
        {
            private PauseMenuBridge? owner;
            private readonly Func<WorldPauseExitContext, WorldPauseExitDecision> interceptor;

            public ExitInterceptorLease(
                PauseMenuBridge owner,
                Func<WorldPauseExitContext, WorldPauseExitDecision> interceptor)
            {
                this.owner = owner;
                this.interceptor = interceptor;
            }

            public void Dispose()
            {
                var current = owner;
                owner = null;
                current?.RemoveExitInterceptor(interceptor);
            }
        }

        private sealed class OwnerFacade : IWorldPauseMenuService
        {
            private readonly PauseMenuBridge bridge;
            private readonly string ownerModId;
            private readonly IModLifetime lifetime;

            public OwnerFacade(PauseMenuBridge bridge, string ownerModId, IModLifetime lifetime)
            {
                this.bridge = bridge;
                this.ownerModId = ownerModId;
                this.lifetime = lifetime;
            }

            public bool IsAvailable => !lifetime.IsStopping && bridge.IsAvailable;

            public OperationResult<IDisposable> RegisterAction(WorldPauseAction action)
            {
                if (action == null)
                {
                    throw new ArgumentNullException(nameof(action));
                }

                if (lifetime.IsStopping) return Stopped();
                var ownedAction = new WorldPauseAction(
                    ownerModId + ":" + action.Id,
                    action.Label,
                    () => { if (!lifetime.IsStopping) action.Callback(); },
                    action.ClosePauseMenu,
                    action.Order,
                    action.Destructive);
                return Track(bridge.RegisterAction(ownedAction));
            }

            public OperationResult<IDisposable> InterceptExit(
                Func<WorldPauseExitContext, WorldPauseExitDecision> interceptor)
            {
                if (interceptor == null) throw new ArgumentNullException(nameof(interceptor));
                if (lifetime.IsStopping) return Stopped();
                return Track(bridge.InterceptExit(context => lifetime.IsStopping
                    ? WorldPauseExitDecision.Block : interceptor(context), ownerModId));
            }

            private static OperationResult<IDisposable> Stopped() =>
                OperationResult<IDisposable>.Failure(ModErrorCode.Cancelled, "The owning context is stopping.");

            private OperationResult<IDisposable> Track(OperationResult<IDisposable> result)
            {
                if (!result.TryGetValue(out var resource))
                {
                    return result;
                }

                try
                {
                    return OperationResult<IDisposable>.Success(lifetime.Track(resource));
                }
                catch (ObjectDisposedException)
                {
                    return OperationResult<IDisposable>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod stopped before its pause-menu customization could be retained.");
                }
            }
        }
    }
}
