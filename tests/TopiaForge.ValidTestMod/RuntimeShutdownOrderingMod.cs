using System;
using TopiaForge.Mods;

namespace TopiaForge.ValidTestMod
{
    public class RuntimeShutdownOrderingMod : TopiaForgeMod
    {
        private int loadedThread;

        protected override void OnLoad()
        {
            loadedThread = Environment.CurrentManagedThreadId;
            RuntimeTrace.Write("load");
            Context.Lifetime.StoppingToken.Register(() => RuntimeTrace.Write("stopping"));
            Context.Lifetime.Defer(() => RuntimeTrace.Write("cleanup"));
        }

        protected override void OnUnload() => RuntimeTrace.Write(
            Environment.CurrentManagedThreadId != loadedThread ? "unload:wrong-thread"
                : Context.Lifetime.IsStopping ? "unload:stopping" : "unload:active");
    }
    public sealed class RuntimeFailingLoadOrderingMod : RuntimeShutdownOrderingMod
    {
        protected override void OnLoad()
        {
            base.OnLoad();
            throw new InvalidOperationException("Synthetic partial startup failure.");
        }
    }
}
