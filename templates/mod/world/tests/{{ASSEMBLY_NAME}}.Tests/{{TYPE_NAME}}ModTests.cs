using NUnit.Framework;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    public sealed class {{TYPE_NAME}}ModTests
    {
        [Test]
        public void LoadingPackageLeavesWorldActivationToTheManifestBinding()
        {
            using var context = new FakeModContext();
            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();
            Assert.That(context.Assets.ActiveSpawnCount, Is.Zero);
            Assert.That(context.Events.ActiveSubscriptionCount, Is.Zero);
            runner.Unload();
            context.AssertNoLeaks();
        }
    }
}
