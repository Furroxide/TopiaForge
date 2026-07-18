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
        private sealed class ModEvents : IModEvents, ISceneLoadEventSource
        {
            private readonly object sync = new object();
            private readonly List<Action<float>> updates = new List<Action<float>>();
            private readonly List<Action<GameTimeSample>> fixedUpdates = new List<Action<GameTimeSample>>();
            private readonly List<Action<GameTimeSample>> lateUpdates = new List<Action<GameTimeSample>>();
            private readonly List<Action<string>> scenes = new List<Action<string>>();
            private readonly List<Action<SceneLoadEvent>> detailedScenes = new List<Action<SceneLoadEvent>>();
            private readonly IModLifetime lifetime;
            private readonly IModLogger logger;

            public ModEvents(IModLifetime lifetime, IModLogger logger)
            {
                this.lifetime = lifetime;
                this.logger = logger;
            }

            public IDisposable SubscribeUpdate(Action<float> handler) => Subscribe(updates, handler);
            public IDisposable SubscribeFixedUpdate(Action<GameTimeSample> handler) => Subscribe(fixedUpdates, handler);
            public IDisposable SubscribeLateUpdate(Action<GameTimeSample> handler) => Subscribe(lateUpdates, handler);
            public IDisposable SubscribeSceneLoaded(Action<string> handler) => Subscribe(scenes, handler);
            public IDisposable SubscribeSceneLoaded(Action<SceneLoadEvent> handler) =>
                Subscribe(detailedScenes, handler);

            public void RaiseUpdate(float value) => Raise(updates, value, "Update");
            public void RaiseFixedUpdate(GameTimeSample value) => Raise(fixedUpdates, value, "FixedUpdate");
            public void RaiseLateUpdate(GameTimeSample value) => Raise(lateUpdates, value, "LateUpdate");
            public void RaiseSceneLoaded(SceneLoadEvent value)
            {
                Raise(scenes, value.SceneName, "SceneLoaded");
                Raise(detailedScenes, value, "SceneLoaded");
            }

            public void RaiseSceneActivated(SceneLoadEvent value) =>
                Raise(detailedScenes, value, "SceneActivated");

            private IDisposable Subscribe<T>(List<Action<T>> handlers, Action<T> handler)
            {
                if (handler == null) throw new ArgumentNullException(nameof(handler));
                lock (sync) handlers.Add(handler);
                return lifetime.Track(new EventSubscription(() =>
                {
                    lock (sync) handlers.Remove(handler);
                }));
            }

            private void Raise<T>(List<Action<T>> handlers, T value, string phase)
            {
                Action<T>[] snapshot;
                lock (sync) snapshot = handlers.ToArray();
                foreach (var handler in snapshot)
                {
                    try { handler(value); }
                    catch (Exception exception) { logger.Error(exception, "A mod event subscriber failed during " + phase + "."); }
                }
            }

            private sealed class EventSubscription : IDisposable
            {
                private Action? unsubscribe;
                public EventSubscription(Action unsubscribe) => this.unsubscribe = unsubscribe;
                public void Dispose() => Interlocked.Exchange(ref unsubscribe, null)?.Invoke();
            }
        }
    }
}
