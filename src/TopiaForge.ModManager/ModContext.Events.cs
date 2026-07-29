using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Interop.Unity;

namespace TopiaForge.ModManager
{
    internal sealed partial class ModContext
    {
        private sealed class ModEvents : IModEvents, ISceneLoadEventSource, ISceneLifecycleEventSource
        {
            private readonly EventChannel<float> updates;
            private readonly EventChannel<GameTimeSample> fixedUpdates;
            private readonly EventChannel<GameTimeSample> lateUpdates;
            private readonly EventChannel<string> scenes;
            private readonly EventChannel<SceneLoadEvent> detailedScenes;
            private readonly EventChannel<SceneLifecycleEvent> sceneLifecycle;

            public ModEvents(IModLifetime lifetime, IModLogger logger)
            {
                updates = new EventChannel<float>(lifetime, logger, "Update");
                fixedUpdates = new EventChannel<GameTimeSample>(lifetime, logger, "FixedUpdate");
                lateUpdates = new EventChannel<GameTimeSample>(lifetime, logger, "LateUpdate");
                scenes = new EventChannel<string>(lifetime, logger, "SceneLoaded");
                detailedScenes = new EventChannel<SceneLoadEvent>(lifetime, logger, "SceneLoaded");
                sceneLifecycle = new EventChannel<SceneLifecycleEvent>(lifetime, logger, "SceneLifecycle");
            }

            public IDisposable SubscribeUpdate(Action<float> handler) => updates.Subscribe(handler);
            public IDisposable SubscribeFixedUpdate(Action<GameTimeSample> handler) => fixedUpdates.Subscribe(handler);
            public IDisposable SubscribeLateUpdate(Action<GameTimeSample> handler) => lateUpdates.Subscribe(handler);
            public IDisposable SubscribeSceneLoaded(Action<string> handler) => scenes.Subscribe(handler);
            public IDisposable SubscribeSceneLoaded(Action<SceneLoadEvent> handler) =>
                detailedScenes.Subscribe(handler);
            public IDisposable SubscribeSceneLifecycle(Action<SceneLifecycleEvent> handler) =>
                sceneLifecycle.Subscribe(handler);

            public void RaiseUpdate(float value) => updates.Raise(value);
            public void RaiseFixedUpdate(GameTimeSample value) => fixedUpdates.Raise(value);
            public void RaiseLateUpdate(GameTimeSample value) => lateUpdates.Raise(value);
            public void RaiseSceneLoaded(SceneLoadEvent value)
            {
                scenes.Raise(value.SceneName);
                detailedScenes.Raise(value);
            }

            public void RaiseSceneActivated(SceneLoadEvent value) =>
                detailedScenes.Raise(value, "SceneActivated");

            public void RaiseSceneLifecycle(SceneLifecycleEvent value) =>
                sceneLifecycle.Raise(value, "Scene" + value.Phase);

            /// <summary>
            /// Copy-on-write event storage. Subscription changes are uncommon and may allocate; dispatch reads an
            /// immutable snapshot so frame, fixed-frame, and late-frame delivery have no steady-state allocations.
            /// </summary>
            private sealed class EventChannel<T>
            {
                private readonly object sync = new object();
                private readonly List<EventSubscriber<T>> subscribers = new List<EventSubscriber<T>>();
                private readonly IModLifetime lifetime;
                private readonly IModLogger logger;
                private readonly string phase;
                private EventSubscriber<T>[] snapshot = Array.Empty<EventSubscriber<T>>();

                public EventChannel(IModLifetime lifetime, IModLogger logger, string phase)
                {
                    this.lifetime = lifetime;
                    this.logger = logger;
                    this.phase = phase;
                }

                public IDisposable Subscribe(Action<T> handler)
                {
                    if (handler == null) throw new ArgumentNullException(nameof(handler));

                    var subscriber = new EventSubscriber<T>(handler, logger);
                    var subscription = new EventSubscription<T>(this, subscriber);
                    lock (sync)
                    {
                        subscribers.Add(subscriber);
                        Volatile.Write(ref snapshot, subscribers.ToArray());
                    }

                    IDisposable tracking;
                    try
                    {
                        // The subscriber remains pending while lifetime ownership is established. A dispatch may
                        // observe the new snapshot, but it cannot invoke the callback unless tracking succeeds.
                        tracking = lifetime.Track(subscription);
                    }
                    catch
                    {
                        subscription.Dispose();
                        throw;
                    }

                    if (!subscriber.Activate())
                    {
                        tracking.Dispose();
                        throw new ObjectDisposedException(
                            nameof(IModLifetime),
                            "The mod lifetime stopped while the event subscription was being registered.");
                    }

                    return tracking;
                }

                public void Raise(T value)
                {
                    Raise(value, phase);
                }

                public void Raise(T value, string dispatchPhase)
                {
                    var current = Volatile.Read(ref snapshot);
                    for (var index = 0; index < current.Length; index++)
                    {
                        current[index].Invoke(value, dispatchPhase);
                    }
                }

                public void Unsubscribe(EventSubscriber<T> subscriber)
                {
                    lock (sync)
                    {
                        if (!subscribers.Remove(subscriber))
                        {
                            return;
                        }

                        Volatile.Write(
                            ref snapshot,
                            subscribers.Count == 0
                                ? Array.Empty<EventSubscriber<T>>()
                                : subscribers.ToArray());
                    }
                }
            }

            /// <summary>
            /// Isolates one subscriber and opens its circuit after three consecutive failures. A success before the
            /// threshold resets the streak. An opened circuit remains disabled until its lease is disposed and a new
            /// subscription is created, making recovery explicit and deterministic.
            /// </summary>
            private sealed class EventSubscriber<T>
            {
                private const int ConsecutiveFailureLimit = 3;
                private const int SuccessfulCallsToRearmFailureLog = 60;
                private readonly Action<T> handler;
                private readonly IModLogger logger;
                private readonly string description;
                private int consecutiveFailures;
                private int consecutiveSuccessesAfterFailure;
                private int failureLogArmed = 1;
                private int disabled;
                private int registrationState;

                public EventSubscriber(Action<T> handler, IModLogger logger)
                {
                    this.handler = handler;
                    this.logger = logger;
                    description = Describe(handler);
                }

                public void Invoke(T value, string phase)
                {
                    if (Volatile.Read(ref registrationState) != 1 ||
                        Volatile.Read(ref disabled) != 0)
                    {
                        return;
                    }

                    try
                    {
                        handler(value);
                        if (Volatile.Read(ref consecutiveFailures) != 0)
                        {
                            Interlocked.Exchange(ref consecutiveFailures, 0);
                        }

                        if (Volatile.Read(ref failureLogArmed) == 0 &&
                            Interlocked.Increment(ref consecutiveSuccessesAfterFailure) >=
                            SuccessfulCallsToRearmFailureLog)
                        {
                            Interlocked.Exchange(ref consecutiveSuccessesAfterFailure, 0);
                            Volatile.Write(ref failureLogArmed, 1);
                        }
                    }
                    catch (Exception exception)
                    {
                        Interlocked.Exchange(ref consecutiveSuccessesAfterFailure, 0);
                        var failure = Interlocked.Increment(ref consecutiveFailures);
                        if (Interlocked.CompareExchange(ref failureLogArmed, 0, 1) == 1)
                        {
                            SafeLogError(
                                exception,
                                "Mod event subscriber '" + description + "' failed during " + phase +
                                " (1/" + ConsecutiveFailureLimit +
                                "). Repeated exception details are suppressed until its circuit opens or " +
                                SuccessfulCallsToRearmFailureLog + " consecutive healthy callbacks rearm diagnostics.");
                        }

                        if (failure < ConsecutiveFailureLimit ||
                            Interlocked.CompareExchange(ref disabled, 1, 0) != 0)
                        {
                            return;
                        }

                        SafeLogError(
                            exception,
                            "Mod event subscriber '" + description + "' was disabled during " + phase +
                            " after " + ConsecutiveFailureLimit +
                            " consecutive failures. Future callbacks are suppressed until the subscription is disposed and recreated.");
                    }
                }

                public bool Activate() =>
                    Interlocked.CompareExchange(ref registrationState, 1, 0) == 0;

                public void CancelPending() =>
                    Interlocked.CompareExchange(ref registrationState, 2, 0);

                private void SafeLogError(Exception exception, string message)
                {
                    try
                    {
                        logger.Error(exception, message);
                    }
                    catch
                    {
                        // A broken logging sink must not defeat subscriber isolation or starve later callbacks.
                    }
                }

                private static string Describe(Action<T> callback)
                {
                    var method = callback.Method;
                    var declaringType = method.DeclaringType;
                    return (declaringType?.FullName ?? "<unknown-type>") + "." + method.Name;
                }
            }

            private sealed class EventSubscription<T> : IDisposable
            {
                private EventChannel<T>? channel;
                private readonly EventSubscriber<T> subscriber;

                public EventSubscription(EventChannel<T> channel, EventSubscriber<T> subscriber)
                {
                    this.channel = channel;
                    this.subscriber = subscriber;
                }

                public void Dispose()
                {
                    // Pending registrations are made permanently inert before removal. Active subscriptions retain
                    // copy-on-write snapshot semantics: disposal during a dispatch affects the next dispatch.
                    subscriber.CancelPending();
                    Interlocked.Exchange(ref channel, null)?.Unsubscribe(subscriber);
                }
            }
        }
    }
}
