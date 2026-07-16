using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic Worlds module fake with controllable asynchronous loads.</summary>
    public sealed class FakeWorldGamemodeService : IWorldGamemodeService, IWorldTransitionState
    {
        private readonly FakeModLifetime lifetime;
        private readonly Dictionary<string, WorldEntry> worlds =
            new Dictionary<string, WorldEntry>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GamemodeDefinition> gamemodes =
            new Dictionary<string, GamemodeDefinition>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, GamemodeMenuEntry> entries =
            new Dictionary<string, GamemodeMenuEntry>(StringComparer.OrdinalIgnoreCase);
        private PendingLoad? pending;

        /// <summary>Creates a fake Worlds service owned by a mod lifetime.</summary>
        public FakeWorldGamemodeService(FakeModLifetime lifetime)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
        }

        /// <summary>Gets or sets whether valid loads complete immediately. Disable to use completion controls.</summary>
        public bool AutoCompleteLoads { get; set; } = true;

        /// <summary>Gets the number of active world, gamemode, and menu registrations.</summary>
        public int ActiveRegistrationCount => worlds.Count + gamemodes.Count + entries.Count;

        /// <summary>Gets whether one manually controlled load is waiting.</summary>
        public bool HasPendingLoad => pending != null;

        /// <inheritdoc />
        public bool IsTransitionInFlight => pending != null;

        /// <inheritdoc />
        public IReadOnlyList<WorldDefinition> Worlds => SortedValues(worlds, value => value.Definition);

        /// <inheritdoc />
        public IReadOnlyList<GamemodeDefinition> Gamemodes => SortedValues(gamemodes, value => value);

        /// <inheritdoc />
        public IReadOnlyList<GamemodeMenuEntry> MenuEntries => SortedValues(entries, value => value);

        /// <inheritdoc />
        public WorldSession? CurrentSession { get; private set; }

        /// <summary>Gets the content factory registered for a world, when one exists.</summary>
        public bool TryGetWorldContent(string worldId, out ICustomWorldContent? content)
        {
            if (worlds.TryGetValue(worldId ?? string.Empty, out var entry))
            {
                content = entry.Content;
                return true;
            }

            content = null;
            return false;
        }

        /// <inheritdoc />
        public event Action<WorldSession>? SessionChanged;

        /// <inheritdoc />
        public event Action<WorldSessionEnd>? SessionEnded;

        /// <inheritdoc />
        public OperationResult<IWorldRegistration> RegisterWorld(
            WorldDefinition world,
            ICustomWorldContent? content = null)
        {
            if (world == null)
            {
                throw new ArgumentNullException(nameof(world));
            }

            if (worlds.ContainsKey(world.Id))
            {
                return Conflict("world", world.Id);
            }

            var registration = new Registration(
                world.Id,
                WorldRegistrationKind.World,
                value =>
                {
                    worlds.Remove(value.Id);
                    if (string.Equals(CurrentSession?.WorldId, value.Id, StringComparison.OrdinalIgnoreCase))
                    {
                        EndSession(WorldSessionEndReason.ProviderUnloading);
                    }
                });
            worlds.Add(world.Id, new WorldEntry(world, content));
            return lifetime.TrackResult<IWorldRegistration>(
                registration,
                registration.AttachLifetimeLease,
                "The fake mod stopped before the world could be registered.");
        }

        /// <inheritdoc />
        public OperationResult<IWorldRegistration> RegisterGamemode(GamemodeDefinition gamemode)
        {
            if (gamemode == null)
            {
                throw new ArgumentNullException(nameof(gamemode));
            }

            if (gamemodes.ContainsKey(gamemode.Id))
            {
                return Conflict("gamemode", gamemode.Id);
            }

            return AddRegistration(
                gamemode.Id,
                gamemode,
                gamemodes,
                WorldRegistrationKind.Gamemode);
        }

        /// <inheritdoc />
        public OperationResult<IWorldRegistration> RegisterMenuEntry(GamemodeMenuEntry entry)
        {
            if (entry == null)
            {
                throw new ArgumentNullException(nameof(entry));
            }

            if (entries.ContainsKey(entry.Id))
            {
                return Conflict("menu entry", entry.Id);
            }

            return AddRegistration(entry.Id, entry, entries, WorldRegistrationKind.MenuEntry);
        }

        /// <inheritdoc />
        public Task<OperationResult<WorldSession>> LoadAsync(
            WorldLoadRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (cancellationToken.IsCancellationRequested || lifetime.StoppingToken.IsCancellationRequested)
            {
                return Task.FromResult(Cancelled());
            }

            if (!worlds.ContainsKey(request.WorldId))
            {
                return Task.FromResult(OperationResult<WorldSession>.Failure(
                    ModErrorCode.NotFound,
                    "No fake world is registered as '" + request.WorldId + "'."));
            }

            if (!gamemodes.ContainsKey(request.GamemodeId))
            {
                return Task.FromResult(OperationResult<WorldSession>.Failure(
                    ModErrorCode.NotFound,
                    "No fake gamemode is registered as '" + request.GamemodeId + "'."));
            }

            if (pending != null)
            {
                return Task.FromResult(OperationResult<WorldSession>.Failure(
                    ModErrorCode.Conflict,
                    "A fake world transition is already pending."));
            }

            if (AutoCompleteLoads)
            {
                return Task.FromResult(CompleteLoad(request));
            }

            var operation = new PendingLoad(request);
            pending = operation;
            operation.AttachCancellation(
                cancellationToken,
                lifetime.StoppingToken,
                CancelPending);
            return operation.Task;
        }

        /// <inheritdoc />
        public Task<OperationResult<WorldSession>> LaunchMenuEntryAsync(
            string entryId,
            CancellationToken cancellationToken = default)
        {
            if (cancellationToken.IsCancellationRequested || lifetime.StoppingToken.IsCancellationRequested)
            {
                return Task.FromResult(Cancelled());
            }

            if (!entries.TryGetValue(entryId ?? string.Empty, out var entry))
            {
                return Task.FromResult(OperationResult<WorldSession>.Failure(
                    ModErrorCode.NotFound,
                    "No fake menu entry is registered as '" + entryId + "'."));
            }

            return LoadAsync(new WorldLoadRequest(entry.WorldId, entry.GamemodeId), cancellationToken);
        }

        /// <summary>Completes the currently controlled load successfully.</summary>
        public bool CompletePendingLoad()
        {
            var operation = TakePending();
            return operation != null && operation.Complete(CompleteLoad(operation.Request));
        }

        /// <summary>Fails the currently controlled load with a stable expected error.</summary>
        public bool FailPendingLoad(ModErrorCode errorCode, string message)
        {
            if (errorCode == ModErrorCode.None)
            {
                throw new ArgumentOutOfRangeException(nameof(errorCode));
            }

            var operation = TakePending();
            return operation != null && operation.Complete(
                OperationResult<WorldSession>.Failure(errorCode, message));
        }

        /// <inheritdoc />
        public OperationResult<bool> EndSession(WorldSessionEndReason reason)
        {
            var ended = CurrentSession;
            if (ended == null)
            {
                return OperationResult<bool>.Success(false);
            }

            CurrentSession = null;
            SessionEnded?.Invoke(new WorldSessionEnd(ended, reason));
            return OperationResult<bool>.Success(true);
        }

        private OperationResult<WorldSession> CompleteLoad(WorldLoadRequest request)
        {
            EndSession(WorldSessionEndReason.Superseded);
            var world = worlds[request.WorldId].Definition;
            var session = new WorldSession(
                request.WorldId,
                request.GamemodeId,
                "fake",
                world.SceneName,
                DateTimeOffset.UnixEpoch);
            CurrentSession = session;
            SessionChanged?.Invoke(session);
            return OperationResult<WorldSession>.Success(session);
        }

        private PendingLoad? TakePending()
        {
            var value = pending;
            pending = null;
            value?.DetachCancellation();
            return value;
        }

        private void CancelPending()
        {
            var operation = TakePending();
            operation?.Complete(Cancelled());
        }

        private static OperationResult<WorldSession> Cancelled() =>
            OperationResult<WorldSession>.Failure(ModErrorCode.Cancelled, "The fake world load was cancelled.");

        private OperationResult<IWorldRegistration> AddRegistration<T>(
            string id,
            T definition,
            Dictionary<string, T> values,
            WorldRegistrationKind kind)
        {
            var registration = new Registration(id, kind, value => values.Remove(value.Id));
            values.Add(id, definition);
            return lifetime.TrackResult<IWorldRegistration>(
                registration,
                registration.AttachLifetimeLease,
                "The fake mod stopped before the world content could be registered.");
        }

        private static OperationResult<IWorldRegistration> Conflict(string kind, string id) =>
            OperationResult<IWorldRegistration>.Failure(
                ModErrorCode.Conflict,
                "A fake " + kind + " is already registered as '" + id + "'.");

        private static IReadOnlyList<TValue> SortedValues<TStored, TValue>(
            Dictionary<string, TStored> source,
            Func<TStored, TValue> select)
        {
            var keys = new List<string>(source.Keys);
            keys.Sort(StringComparer.OrdinalIgnoreCase);
            var values = new List<TValue>(keys.Count);
            foreach (var key in keys)
            {
                values.Add(select(source[key]));
            }

            return values.AsReadOnly();
        }

        private sealed class WorldEntry
        {
            public WorldEntry(WorldDefinition definition, ICustomWorldContent? content)
            {
                Definition = definition;
                Content = content;
            }

            public WorldDefinition Definition { get; }
            public ICustomWorldContent? Content { get; }
        }

        private sealed class Registration : IWorldRegistration
        {
            private Action<Registration>? release;
            private IDisposable? lifetimeLease;

            public Registration(string id, WorldRegistrationKind kind, Action<Registration> release)
            {
                Id = id;
                Kind = kind;
                this.release = release;
            }

            public string Id { get; }
            public WorldRegistrationKind Kind { get; }
            public bool IsActive => release != null;

            public void AttachLifetimeLease(IDisposable lease)
            {
                lifetimeLease = lease ?? throw new ArgumentNullException(nameof(lease));
            }

            public void Dispose()
            {
                var callback = release;
                release = null;
                try
                {
                    callback?.Invoke(this);
                }
                finally
                {
                    Interlocked.Exchange(ref lifetimeLease, null)?.Dispose();
                }
            }
        }

        private sealed class PendingLoad
        {
            private readonly TaskCompletionSource<OperationResult<WorldSession>> completion =
                new TaskCompletionSource<OperationResult<WorldSession>>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            private CancellationTokenRegistration callerCancellation;
            private CancellationTokenRegistration lifetimeCancellation;

            public PendingLoad(WorldLoadRequest request)
            {
                Request = request;
            }

            public void AttachCancellation(
                CancellationToken callerToken,
                CancellationToken lifetimeToken,
                Action cancel)
            {
                if (callerToken.CanBeCanceled)
                {
                    callerCancellation = callerToken.Register(cancel);
                    if (Task.IsCompleted)
                    {
                        callerCancellation.Dispose();
                        return;
                    }
                }

                if (lifetimeToken.CanBeCanceled)
                {
                    lifetimeCancellation = lifetimeToken.Register(cancel);
                    if (Task.IsCompleted)
                    {
                        lifetimeCancellation.Dispose();
                    }
                }
            }

            public WorldLoadRequest Request { get; }
            public Task<OperationResult<WorldSession>> Task => completion.Task;
            public bool Complete(OperationResult<WorldSession> result) => completion.TrySetResult(result);
            public void DetachCancellation()
            {
                callerCancellation.Dispose();
                lifetimeCancellation.Dispose();
            }
        }
    }
}
