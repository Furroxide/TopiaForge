using System;
using System.IO;
using TopiaForge.Mods;

namespace TopiaForge.ValidTestMod
{
    internal sealed class ValidTestService : IValidTestService
    {
        public string Ping(string message)
        {
            return message ?? string.Empty;
        }
    }

    public sealed class ValidMod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
        }
    }

    public abstract class AbstractMod : TopiaForgeMod
    {
    }

    public sealed class NoDefaultConstructorMod : TopiaForgeMod
    {
        public NoDefaultConstructorMod(string value)
        {
            _ = value;
        }

        protected override void OnLoad()
        {
        }
    }

    internal sealed class InternalMod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
        }
    }

    public sealed class RuntimeSuccessMod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            RuntimeTrace.Write("load");
            Context.Lifetime.Defer(() => RuntimeTrace.Write("cleanup-first"));
            Context.Lifetime.Defer(() => RuntimeTrace.Write("cleanup-second"));
            Context.Events.SubscribeUpdate(_ => throw new InvalidOperationException("expected subscriber failure"));
            Context.Events.SubscribeUpdate(_ => RuntimeTrace.Write("update-after-failure"));
            Context.Events.SubscribeSceneLoaded(scene => RuntimeTrace.Write("scene:" + scene));
        }

        protected override void OnUnload()
        {
            RuntimeTrace.Write("unload");
        }
    }

    public sealed class RuntimeFailingLoadMod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            RuntimeTrace.Write("load");
            Context.Lifetime.Defer(() => RuntimeTrace.Write("cleanup"));
            throw new InvalidOperationException("synthetic load failure");
        }

        protected override void OnUnload()
        {
            RuntimeTrace.Write("unload");
        }
    }

    public sealed class RuntimeFailingUnloadMod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            RuntimeTrace.Write("load");
            Context.Lifetime.Defer(() => RuntimeTrace.Write("cleanup"));
        }

        protected override void OnUnload()
        {
            RuntimeTrace.Write("unload");
            throw new InvalidOperationException("synthetic unload failure");
        }
    }

    public sealed class RuntimeDetailedSceneMod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            Context.Events.SubscribeSceneLoaded(sceneName => RuntimeTrace.Write("scene-legacy:" + sceneName));
            Context.Events.SubscribeSceneLoaded((SceneLoadEvent scene) => RuntimeTrace.Write(
                "scene-detail:" + scene.SceneName + ":" + scene.Mode + ":" +
                (scene.IsActive ? "active" : "background") + ":" +
                (scene.IsAuthoritativeReplacement ? "authoritative" : "additive")));
        }

        protected override void OnUnload()
        {
            RuntimeTrace.Write("unload");
        }
    }

    public sealed class RuntimeSceneLifecycleMod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            Context.Events.SubscribeSceneLifecycle(scene => RuntimeTrace.Write(
                "scene-lifecycle:" + scene.SceneName + ":" + scene.SceneInstanceId + ":" + scene.Phase + ":" +
                scene.Mode + ":" + (scene.IsActive ? "active" : "background") + ":" +
                (scene.IsInitial ? "initial" : "native")));
        }

        protected override void OnUnload()
        {
            RuntimeTrace.Write("unload");
        }
    }

    public sealed class RuntimeInitialSceneReplayMod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            Context.Events.SubscribeSceneLoaded(sceneName => RuntimeTrace.Write("initial-legacy:" + sceneName));
            Context.Events.SubscribeSceneLoaded((SceneLoadEvent scene) => RuntimeTrace.Write(
                "initial-detail:" + scene.SceneName + ":" + scene.Mode + ":" +
                (scene.IsActive ? "active" : "background")));
            Context.Events.SubscribeSceneLifecycle(scene => RuntimeTrace.Write(
                "initial-lifecycle:" + scene.SceneName + ":" + scene.SceneInstanceId + ":" + scene.Phase + ":" +
                scene.Mode + ":" + (scene.IsActive ? "active" : "background") + ":" +
                (scene.IsInitial ? "initial" : "native")));
        }

        protected override void OnUnload()
        {
            RuntimeTrace.Write("unload");
        }
    }

    public sealed class RuntimeDependentMod : TopiaForgeMod
    {
        protected override void OnLoad()
        {
            RuntimeTrace.Write("dependent-load");
        }
    }

    public sealed class RuntimeThrowingConstructorMod : TopiaForgeMod
    {
        public RuntimeThrowingConstructorMod()
        {
            RuntimeTrace.Write("constructor");
            throw new InvalidOperationException("synthetic constructor failure");
        }

        protected override void OnLoad()
        {
            RuntimeTrace.Write("unexpected-load");
        }
    }

    internal static class RuntimeTrace
    {
        public static void Write(string value)
        {
            var path = Environment.GetEnvironmentVariable("TOPIAFORGE_RUNTIME_TEST_TRACE");
            if (!string.IsNullOrWhiteSpace(path))
            {
                File.AppendAllText(path, value + Environment.NewLine);
            }
        }
    }
}
