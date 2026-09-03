using System;
using System.Linq;
using NUnit.Framework;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    [TestFixture]
    public sealed class {{TYPE_NAME}}ModTests
    {
        /// <summary>
        /// The factory the manifest names must answer with the id the manifest declares. That pairing
        /// is the whole point of the V6 binding: a gamemode that says it is one thing while its
        /// declaration says another is exactly what nothing used to catch.
        /// </summary>
        [Test]
        public void TheFactoryAnswersWithTheDeclaredGamemodeId()
        {
            Assert.That(new {{TYPE_NAME}}Gamemode().GamemodeId, Is.EqualTo({{TYPE_NAME}}Mod.GamemodeId));
        }

        [Test]
        public void LifecycleRunsAndCleansUpWithoutRegisteringItsOwnDeclaration()
        {
            var context = new FakeModContext();
            var worlds = new FakeWorldGamemodeService(context.Lifetime);
            Assert.That(worlds.RegisterWorld(new WorldDefinition(
                WellKnownWorldIds.OpenSandboxWorld,
                "Open Sandbox",
                "Deterministic test world.",
                sceneName: "UgcPlay")).Succeeded, Is.True);

            // Stands in for the runtime publishing what the manifest declares. The mod itself must not
            // publish it: the gamemode, its world policy and its launch target live in the manifest,
            // and a second copy in code is a second source of truth.
            Assert.That(worlds.RegisterGamemode(new GamemodeDefinition(
                {{TYPE_NAME}}Mod.GamemodeId,
                "{{DISPLAY_NAME}}",
                "Custom gamemode scaffolded from the gamemode template.")).Succeeded, Is.True);
            Assert.That(context.Extensions.Register<IWorldGamemodeService>(worlds).Succeeded, Is.True);

            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();

            Assert.Multiple(() =>
            {
                Assert.That(worlds.Gamemodes.Select(item => item.Id), Is.EqualTo(new[] { {{TYPE_NAME}}Mod.GamemodeId }));
                Assert.That(worlds.MenuEntries, Is.Empty);
            });

            var loaded = worlds.LoadAsync(new WorldLoadRequest(
                WellKnownWorldIds.OpenSandboxWorld,
                {{TYPE_NAME}}Mod.GamemodeId)).GetAwaiter().GetResult();
            Assert.That(loaded.Succeeded, Is.True);
            context.AdvanceFrame(TimeSpan.FromMilliseconds(16));
            Assert.That(context.Logger.Entries.Any(entry => entry.Message.Contains("session started")), Is.True);

            Assert.That(worlds.EndSession(WorldSessionEndReason.EndedByGamemode).Value, Is.True);
            runner.Unload();

            Assert.That(context.Logger.Entries.Any(entry => entry.Message.Contains("session ended")), Is.True);
            context.AssertNoLeaks();
        }
    }
}
