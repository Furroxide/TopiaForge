using NUnit.Framework;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    public sealed class {{TYPE_NAME}}ModTests
    {
        /// <summary>
        /// A world mod declares its world; it does not register one. The declaration in
        /// topiaforge.mod.json is what the launcher reads, so this mod registering anything at load
        /// would mean the same world existed twice, described two ways, with nothing keeping the two
        /// descriptions equal.
        /// </summary>
        [Test]
        public void Load_DeclaresItsWorldWithoutRegisteringAnything()
        {
            var context = new FakeModContext();
            var worlds = new FakeWorldGamemodeService(context.Lifetime);
            Assert.That(context.Extensions.Register<IWorldGamemodeService>(worlds).Succeeded, Is.True);

            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();

            Assert.Multiple(() =>
            {
                Assert.That(worlds.Worlds, Is.Empty);
                Assert.That(worlds.MenuEntries, Is.Empty);
                Assert.That(worlds.ActiveRegistrationCount, Is.Zero);
            });

            runner.Unload();

            Assert.That(worlds.ActiveRegistrationCount, Is.Zero);
            context.AssertNoLeaks();
        }
    }
}
