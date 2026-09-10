using System;
using System.Reflection;
using TopiaForge.Mods;
using TopiaForge.Zombies;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class ZombiesControllerTests
    {
        internal static void RunCleanupRegression()
        {
            var config = FastConfig();
            config.PlayerIntegrity = 100f;
            using var harness = new Harness(config);
            harness.Advance(0.01f);
            harness.Controller.TestingSetWavePhase();
            Assert(harness.Controller.TestingSpawn(ZombieKind.Grunt, new Vec3(0f, 0f, 4f)),
                "cleanup regression must first allocate a live enemy");
            harness.Controller.TestingDamagePlayer(40f);
            var field = typeof(ZombiesController).GetField("updateSubscription", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var original = (IDisposable)field.GetValue(harness.Controller)!;
            field.SetValue(harness.Controller, new ThrowAfterDispose(original));
            Exception? failure = null;
            try { harness.Controller.Dispose(); } catch (Exception exception) { failure = exception; }
            Assert(failure != null && failure.ToString().Contains("injected update cleanup failure"),
                "cleanup must retain the first disposer failure");
            Assert(harness.Context.LocalPlayer.Health!.Current == 100f
                && harness.Robots.Agents.ActiveAgents.Count == 0
                && harness.Context.Ui.Surfaces.Count == 0
                && harness.Context.Input.ActiveActionCount == 0,
                "an early throwing disposer must not skip native health restoration, enemy cleanup, input, or HUD disposal");
        }

        private sealed class ThrowAfterDispose : IDisposable
        {
            private readonly IDisposable inner;
            internal ThrowAfterDispose(IDisposable inner) { this.inner = inner; }
            public void Dispose()
            { inner.Dispose(); throw new InvalidOperationException("injected update cleanup failure"); }
        }
    }
}
