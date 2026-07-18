using System;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    public sealed class {{TYPE_NAME}}ModTests
    {
        [Test]
        public async Task Load_RegistersBundleWorldAndLifetimeReleasesContentAndDefinitions()
        {
            var context = new FakeModContext();
            var worlds = new FakeWorldGamemodeService(context.Lifetime);
            var extension = context.Extensions.Register<IWorldGamemodeService>(worlds);
            Assert.That(extension.Succeeded, Is.True);

            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();

            var registeredWorld = worlds.Worlds.Single();
            var registeredMenuEntry = worlds.MenuEntries.Single();
            Assert.That(worlds.TryGetWorldContent(registeredWorld.Id, out var registeredContent), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(registeredContent, Is.TypeOf<BundleWorldContent>());
                Assert.That(registeredMenuEntry.GamemodeId, Is.EqualTo(WellKnownWorldIds.SandboxGamemode));
                Assert.That(worlds.ActiveRegistrationCount, Is.EqualTo(2));
            });

            var created = await registeredContent!.CreateAsync();
            Assert.That(created.TryGetValue(out var content), Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(content!.IsAlive, Is.True);
                Assert.That(context.Assets.ActiveSpawnCount, Is.EqualTo(1));
            });
            content!.Dispose();
            Assert.That(context.Assets.ActiveSpawnCount, Is.Zero);

            runner.Unload();

            Assert.That(worlds.ActiveRegistrationCount, Is.Zero);
            context.AssertNoLeaks();
        }
    }
}
