using System;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    [TestFixture]
    public sealed class {{TYPE_NAME}}ModTests
    {
        [Test]
        public void LoadingPackageDoesNotStartGameplay()
        {
            using var context = new FakeModContext();
            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();
            Assert.That(context.Events.ActiveSubscriptionCount, Is.Zero);
            runner.Unload();
            context.AssertNoLeaks();
        }

        [Test]
        public async Task FactoryOwnsRoundResourcesAndRestartUsesItsSession()
        {
            using var context = new FakeModContext();
            var pause = new FakeWorldPauseMenuService(context.Lifetime);
            Assert.That(context.Extensions.Register<IWorldPauseMenuService>(pause).Succeeded, Is.True);
            var session = ReadySession(context);
            var result = await new {{TYPE_NAME}}Gamemode().StartAsync(session, CancellationToken.None);
            Assert.That(result.TryGetValue(out var controller), Is.True);
            Assert.That(context.Events.ActiveSubscriptionCount, Is.EqualTo(1));
            Assert.That(pause.ActiveActionCount, Is.EqualTo(1));
            Assert.That(pause.Invoke(session.GamemodeId + ".restart"), Is.True);
            Assert.That(session.RestartRequests, Is.EqualTo(1));
            controller!.Dispose();
            controller.Dispose();
            Assert.That(pause.ActiveActionCount, Is.Zero);
            Assert.That(context.Events.ActiveSubscriptionCount, Is.Zero);
            Assert.That(pause.Invoke(session.GamemodeId + ".restart"), Is.False);
            context.Dispose();
            context.AssertNoLeaks();
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CancellationBeforeStartAllocatesNoGameplayResources(bool cancelSession)
        {
            using var context = new FakeModContext();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            var baselineResources = context.Lifetime.TrackedResourceCount;
            var session = ReadySession(context, cancelSession ? cancellation.Token : CancellationToken.None);
            Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await new {{TYPE_NAME}}Gamemode().StartAsync(session,
                    cancelSession ? CancellationToken.None : cancellation.Token));
            Assert.That(context.Lifetime.TrackedResourceCount, Is.EqualTo(baselineResources));
            Assert.That(context.Events.ActiveSubscriptionCount, Is.Zero);
            context.Dispose();
            context.AssertNoLeaks();
        }

        private static FakeGamemodeSession ReadySession(FakeModContext context, CancellationToken token = default) =>
            new FakeGamemodeSession(context,
                new WorldReadiness(new WorldSceneIdentity(1, "TestWorld"), TransformState.Identity),
                gamemodeId: "{{MOD_ID}}.mode", cancellationToken: token);
    }
}
