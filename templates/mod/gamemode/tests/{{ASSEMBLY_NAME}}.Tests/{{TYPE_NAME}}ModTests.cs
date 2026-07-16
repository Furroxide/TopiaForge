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
        [Test]
        public void LifecycleRegistersRunsAndCleansUpGamemode()
        {
            var context = new FakeModContext();
            var worlds = new FakeWorldGamemodeService(context.Lifetime);
            var world = worlds.RegisterWorld(new WorldDefinition(
                WellKnownWorldIds.OpenSandboxWorld,
                "Open Sandbox",
                "Deterministic test world.",
                sceneName: "UgcPlay"));
            Assert.That(world.Succeeded, Is.True);
            Assert.That(context.Extensions.Register<IWorldGamemodeService>(worlds).Succeeded, Is.True);
            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);

            runner.Load();

            Assert.That(worlds.Gamemodes.Select(item => item.Id), Does.Contain({{TYPE_NAME}}Mod.GamemodeId));
            Assert.That(worlds.MenuEntries.Single().WorldId, Is.EqualTo(WellKnownWorldIds.OpenSandboxWorld));

            var loaded = worlds.LoadAsync(new WorldLoadRequest(
                WellKnownWorldIds.OpenSandboxWorld,
                {{TYPE_NAME}}Mod.GamemodeId)).GetAwaiter().GetResult();
            Assert.That(loaded.Succeeded, Is.True);
            context.AdvanceFrame(TimeSpan.FromMilliseconds(16));
            Assert.That(context.Logger.Entries.Any(entry => entry.Message.Contains("session started")), Is.True);

            Assert.That(worlds.EndSession(WorldSessionEndReason.EndedByGamemode).Value, Is.True);
            runner.Unload();

            Assert.That(worlds.Worlds, Is.Empty);
            Assert.That(worlds.Gamemodes, Is.Empty);
            Assert.That(worlds.MenuEntries, Is.Empty);
            Assert.That(worlds.ActiveRegistrationCount, Is.Zero);
            context.AssertNoLeaks();
        }
    }
}
