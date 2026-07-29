using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.Multiplayer
{
    internal sealed class LoopbackRateLimiter
    {
        private readonly object gate = new object();
        private readonly int maximumPerSecond;
        private readonly Dictionary<ParticipantId, RateWindow> windows =
            new Dictionary<ParticipantId, RateWindow>();

        internal LoopbackRateLimiter(int maximumPerSecond)
        {
            this.maximumPerSecond = maximumPerSecond;
        }

        internal OperationResult<bool> TryAcquire(ParticipantId senderId, long nowMilliseconds)
        {
            lock (gate)
            {
                var windowId = Math.Max(0, nowMilliseconds) / 1000;
                if (!windows.TryGetValue(senderId, out var window) || window.WindowId != windowId)
                {
                    windows[senderId] = new RateWindow(windowId, 1);
                    return OperationResult<bool>.Success(true);
                }

                if (window.Count >= maximumPerSecond)
                {
                    return OperationResult<bool>.Failure(
                        ModErrorCode.RateLimited,
                        "The authenticated sender exceeded the per-second rate limit.");
                }

                windows[senderId] = new RateWindow(windowId, window.Count + 1);
                return OperationResult<bool>.Success(true);
            }
        }

        internal void Clear()
        {
            lock (gate) windows.Clear();
        }

        private readonly struct RateWindow
        {
            internal RateWindow(long windowId, int count)
            {
                WindowId = windowId;
                Count = count;
            }

            internal long WindowId { get; }

            internal int Count { get; }
        }
    }

    internal interface ILoopbackObjectTypeRegistration : IReplicatedObjectTypeRegistration
    {
        OperationResult<bool> TryAcquire(ParticipantId senderId, long nowMilliseconds);
    }

    internal sealed class LoopbackObjectTypeRegistration<TState, TInput> : ILoopbackObjectTypeRegistration
        where TState : class
        where TInput : class
    {
        private readonly Action<string, ILoopbackObjectTypeRegistration> remove;
        private readonly LoopbackRateLimiter rateLimiter;
        private bool active = true;

        internal LoopbackObjectTypeRegistration(
            ReplicatedObjectTypeDefinition<TState, TInput> definition,
            Action<string, ILoopbackObjectTypeRegistration> remove)
        {
            Definition = definition;
            this.remove = remove;
            rateLimiter = new LoopbackRateLimiter(definition.MaximumPerSecond);
        }

        internal ReplicatedObjectTypeDefinition<TState, TInput> Definition { get; }

        public string TypeId => Definition.TypeId;

        public bool IsActive => active;

        public OperationResult<bool> TryAcquire(ParticipantId senderId, long nowMilliseconds)
        {
            if (!active)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidState,
                    "The replicated-object type registration is inactive.");
            }

            return rateLimiter.TryAcquire(senderId, nowMilliseconds);
        }

        public void Dispose()
        {
            if (!active) return;
            active = false;
            rateLimiter.Clear();
            remove(TypeId, this);
        }
    }

    internal interface ILoopbackPresentationRegistration : IPresentationEventRegistration
    {
        OperationResult<bool> Dispatch(byte[] bytes);
    }

    internal sealed class LoopbackPresentationRegistration<TEvent> : ILoopbackPresentationRegistration
        where TEvent : class
    {
        private readonly PresentationEventDefinition<TEvent> definition;
        private readonly Action<string, ILoopbackPresentationRegistration> remove;
        private bool active = true;

        internal LoopbackPresentationRegistration(
            PresentationEventDefinition<TEvent> definition,
            Action<string, ILoopbackPresentationRegistration> remove)
        {
            this.definition = definition;
            this.remove = remove;
        }

        public string Id => definition.Type.Id;

        public bool IsActive => active;

        public OperationResult<bool> Dispatch(byte[] bytes)
        {
            if (!active)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidState,
                    "The presentation-event registration is inactive.");
            }

            if (bytes.Length > definition.Type.Codec.MaximumEncodedBytes)
            {
                return OperationResult<bool>.Failure(
                    ModErrorCode.InvalidArgument,
                    "The presentation-event payload exceeded its declared maximum size.");
            }

            var decoded = definition.Type.Codec.Decode((byte[])bytes.Clone());
            if (!decoded.TryGetValue(out var value))
            {
                return OperationResult<bool>.Failure(decoded.ErrorCode, decoded.ErrorMessage);
            }

            definition.Handler?.Invoke(value);
            return OperationResult<bool>.Success(true);
        }

        public void Dispose()
        {
            if (!active) return;
            active = false;
            remove(Id, this);
        }
    }

    internal interface ILoopbackReplicatedObject : IDisposable
    {
        NetworkObjectId Id { get; }

        string OwnerModId { get; }

        string TypeId { get; }

        Func<object> CreateChangeFactory(ReplicatedObjectChangeKind kind);
    }
}
