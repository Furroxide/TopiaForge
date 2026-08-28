using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.Worlds
{
    public sealed partial class WorldsService
    {
        object IOwnerBoundExtensionFactory.CreateOwnerFacade(
            Type contractType,
            string ownerModId,
            IModLifetime lifetime)
        {
            if (contractType != typeof(IWorldGamemodeService))
            {
                throw new ArgumentException("Unsupported Worlds extension contract.", nameof(contractType));
            }

            return new OwnerFacade(this, lifetime);
        }
        private sealed class OwnerFacade : IWorldGamemodeService
        {
            private readonly WorldsService service;
            private readonly IModLifetime lifetime;
            private readonly object subscriptionSync = new object();
            private readonly List<OwnerEventSubscription<WorldSession>> changedSubscriptions =
                new List<OwnerEventSubscription<WorldSession>>();
            private readonly List<OwnerEventSubscription<WorldSessionEnd>> endedSubscriptions =
                new List<OwnerEventSubscription<WorldSessionEnd>>();

            public OwnerFacade(WorldsService service, IModLifetime lifetime)
            {
                this.service = service;
                this.lifetime = lifetime;
            }

            public IReadOnlyList<WorldDefinition> Worlds => service.Worlds;
            public IReadOnlyList<GamemodeDefinition> Gamemodes => service.Gamemodes;
            public IReadOnlyList<GamemodeMenuEntry> MenuEntries => service.MenuEntries;
            public WorldSession? CurrentSession => service.CurrentSession;

            public event Action<WorldSession>? SessionChanged
            {
                add
                {
                    if (value == null || lifetime.IsStopping)
                    {
                        return;
                    }

                    AddChanged(value);
                }
                remove
                {
                    if (value != null)
                    {
                        RemoveChanged(value);
                    }
                }
            }

            public event Action<WorldSessionEnd>? SessionEnded
            {
                add
                {
                    if (value == null || lifetime.IsStopping)
                    {
                        return;
                    }

                    AddEnded(value);
                }
                remove
                {
                    if (value != null)
                    {
                        RemoveEnded(value);
                    }
                }
            }

            public OperationResult<IWorldRegistration> RegisterWorld(
                WorldDefinition world,
                ICustomWorldContent? content = null) =>
                Track(service.RegisterWorld(world, content));

            public OperationResult<IWorldRegistration> RegisterGamemode(GamemodeDefinition gamemode) =>
                Track(service.RegisterGamemode(gamemode));

            public OperationResult<IWorldRegistration> RegisterMenuEntry(GamemodeMenuEntry entry) =>
                Track(service.RegisterMenuEntry(entry));

            public Task<OperationResult<WorldSession>> LoadAsync(
                WorldLoadRequest request,
                CancellationToken cancellationToken = default) =>
                RunWithLifetimeCancellation(token => service.LoadAsync(request, token), cancellationToken);

            public Task<OperationResult<WorldSession>> LaunchMenuEntryAsync(
                string entryId,
                CancellationToken cancellationToken = default) =>
                RunWithLifetimeCancellation(token => service.LaunchMenuEntryAsync(entryId, token), cancellationToken);

            public OperationResult<bool> EndSession(WorldSessionEndReason reason) => service.EndSession(reason);

            public OperationResult<IDisposable> RegisterAssetOverride(WorldAssetOverride assetOverride) =>
                TrackDisposable(service.RegisterAssetOverride(assetOverride));

            public OperationResult<IReadOnlyList<LocalWorldFile>> ListLocalWorlds() =>
                service.ListLocalWorlds();

            public OperationResult<bool> LoadLocalWorld(string requestedPath) =>
                service.LoadLocalWorld(requestedPath);

            private OperationResult<IDisposable> TrackDisposable(OperationResult<IDisposable> result)
            {
                if (lifetime.IsStopping)
                {
                    if (result.TryGetValue(out var stoppingResource))
                    {
                        stoppingResource.Dispose();
                    }

                    return OperationResult<IDisposable>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod is stopping and cannot register an asset override.");
                }

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
                        "The mod stopped before its asset override could be retained.");
                }
            }

            private OperationResult<IWorldRegistration> Track(OperationResult<IWorldRegistration> result)
            {
                if (lifetime.IsStopping)
                {
                    if (result.TryGetValue(out var stoppingRegistration))
                    {
                        stoppingRegistration.Dispose();
                    }

                    return OperationResult<IWorldRegistration>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod is stopping and cannot register world content.");
                }

                if (!result.TryGetValue(out var registration))
                {
                    return result;
                }

                try
                {
                    var ownedRegistration = new OwnerRegistration(
                        registration,
                        lifetime.Track(registration));
                    return OperationResult<IWorldRegistration>.Success(ownedRegistration);
                }
                catch (ObjectDisposedException)
                {
                    return OperationResult<IWorldRegistration>.Failure(
                        ModErrorCode.Cancelled,
                        "The mod stopped before its world registration could be retained.");
                }
            }

            private async Task<OperationResult<WorldSession>> RunWithLifetimeCancellation(
                Func<CancellationToken, Task<OperationResult<WorldSession>>> operation,
                CancellationToken cancellationToken)
            {
                using (var linked = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    lifetime.StoppingToken))
                {
                    return await operation(linked.Token);
                }
            }

            private void AddChanged(Action<WorldSession> handler)
            {
                OwnerEventSubscription<WorldSession>? subscription = null;
                subscription = new OwnerEventSubscription<WorldSession>(
                    handler,
                    wrapper => service.SessionChanged += wrapper,
                    wrapper => service.SessionChanged -= wrapper,
                    () => ForgetSubscription(changedSubscriptions, subscription!));
                TrackSubscription(changedSubscriptions, subscription);
            }

            private void AddEnded(Action<WorldSessionEnd> handler)
            {
                OwnerEventSubscription<WorldSessionEnd>? subscription = null;
                subscription = new OwnerEventSubscription<WorldSessionEnd>(
                    handler,
                    wrapper => service.SessionEnded += wrapper,
                    wrapper => service.SessionEnded -= wrapper,
                    () => ForgetSubscription(endedSubscriptions, subscription!));
                TrackSubscription(endedSubscriptions, subscription);
            }

            private void TrackSubscription<T>(
                List<OwnerEventSubscription<T>> subscriptions,
                OwnerEventSubscription<T> subscription)
            {
                lock (subscriptionSync)
                {
                    if (lifetime.IsStopping)
                    {
                        subscription.Dispose();
                        return;
                    }

                    subscriptions.Add(subscription);
                    try
                    {
                        subscription.AttachPublisher();
                        subscription.AttachLifetimeLease(lifetime.Track(subscription));
                    }
                    catch (ObjectDisposedException)
                    {
                        // Shutdown won the race after the IsStopping check. Track has already disposed the
                        // subscription; Dispose is idempotent and also removes the facade's bookkeeping node.
                        subscription.Dispose();
                    }
                    catch
                    {
                        subscription.Dispose();
                        throw;
                    }
                }
            }

            private void ForgetSubscription<T>(
                List<OwnerEventSubscription<T>> subscriptions,
                OwnerEventSubscription<T> subscription)
            {
                lock (subscriptionSync)
                {
                    subscriptions.Remove(subscription);
                }
            }

            private void RemoveChanged(Action<WorldSession> handler)
            {
                OwnerEventSubscription<WorldSession>? subscription = null;
                lock (subscriptionSync)
                {
                    for (var index = changedSubscriptions.Count - 1; index >= 0; index--)
                    {
                        if (changedSubscriptions[index].Matches(handler))
                        {
                            subscription = changedSubscriptions[index];
                            changedSubscriptions.RemoveAt(index);
                            break;
                        }
                    }
                }

                subscription?.Dispose();
            }

            private void RemoveEnded(Action<WorldSessionEnd> handler)
            {
                OwnerEventSubscription<WorldSessionEnd>? subscription = null;
                lock (subscriptionSync)
                {
                    for (var index = endedSubscriptions.Count - 1; index >= 0; index--)
                    {
                        if (endedSubscriptions[index].Matches(handler))
                        {
                            subscription = endedSubscriptions[index];
                            endedSubscriptions.RemoveAt(index);
                            break;
                        }
                    }
                }

                subscription?.Dispose();
            }

            private sealed class OwnerRegistration : IWorldRegistration
            {
                private readonly IWorldRegistration registration;
                private IDisposable? lifetimeLease;

                public OwnerRegistration(IWorldRegistration registration, IDisposable lifetimeLease)
                {
                    this.registration = registration;
                    this.lifetimeLease = lifetimeLease;
                }

                public string Id => registration.Id;
                public WorldRegistrationKind Kind => registration.Kind;
                public bool IsActive => lifetimeLease != null && registration.IsActive;

                public void Dispose()
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }

        }
    }
}
