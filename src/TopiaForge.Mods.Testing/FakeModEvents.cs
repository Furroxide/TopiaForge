using System;
using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Deterministic event source whose subscriptions are owned by a fake mod lifetime.</summary>
    public sealed class FakeModEvents : IModEvents
    {
        private readonly FakeModLifetime lifetime;
        private readonly CapturedModLogger logger;
        private readonly List<Action<float>> updates = new List<Action<float>>();
        private readonly List<Action<GameTimeSample>> fixedUpdates = new List<Action<GameTimeSample>>();
        private readonly List<Action<GameTimeSample>> lateUpdates = new List<Action<GameTimeSample>>();
        private readonly List<Action<string>> sceneLoads = new List<Action<string>>();

        /// <summary>Creates a deterministic event source.</summary>
        public FakeModEvents(FakeModLifetime lifetime, CapturedModLogger logger)
        {
            this.lifetime = lifetime ?? throw new ArgumentNullException(nameof(lifetime));
            this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>Gets the number of currently active SDK subscriptions.</summary>
        public int ActiveSubscriptionCount =>
            updates.Count + fixedUpdates.Count + lateUpdates.Count + sceneLoads.Count;

        internal CapturedModLogger Logger => logger;

        /// <inheritdoc/>
        public IDisposable SubscribeUpdate(Action<float> handler) => Subscribe(updates, handler);

        /// <inheritdoc/>
        public IDisposable SubscribeFixedUpdate(Action<GameTimeSample> handler) => Subscribe(fixedUpdates, handler);

        /// <inheritdoc/>
        public IDisposable SubscribeLateUpdate(Action<GameTimeSample> handler) => Subscribe(lateUpdates, handler);

        /// <inheritdoc/>
        public IDisposable SubscribeSceneLoaded(Action<string> handler) => Subscribe(sceneLoads, handler);

        /// <summary>Raises one ordinary rendered-frame update.</summary>
        /// <param name="deltaTime">Scaled frame duration in seconds.</param>
        public void RaiseUpdate(float deltaTime) => Raise(updates, deltaTime, "update");

        /// <summary>Raises one fixed-physics update.</summary>
        public void RaiseFixedUpdate(GameTimeSample sample) => Raise(fixedUpdates, sample, "fixed update");

        /// <summary>Raises one late-frame update.</summary>
        public void RaiseLateUpdate(GameTimeSample sample) => Raise(lateUpdates, sample, "late update");

        /// <summary>Raises a successful scene-load notification.</summary>
        public void RaiseSceneLoaded(string sceneName) =>
            Raise(sceneLoads, sceneName ?? string.Empty, "scene loaded");

        private IDisposable Subscribe<T>(List<Action<T>> handlers, Action<T> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            handlers.Add(handler);
            return lifetime.Track(new Subscription(() => handlers.Remove(handler)));
        }

        private void Raise<T>(List<Action<T>> handlers, T value, string eventName)
        {
            foreach (var handler in handlers.ToArray())
            {
                try
                {
                    handler(value);
                }
                catch (Exception exception)
                {
                    logger.Error(exception, "A fake " + eventName + " subscriber threw.");
                }
            }
        }

        private sealed class Subscription : IDisposable
        {
            private Action? unsubscribe;

            public Subscription(Action unsubscribe)
            {
                this.unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                var action = unsubscribe;
                unsubscribe = null;
                action?.Invoke();
            }
        }
    }
}
