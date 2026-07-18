using System;
using NUnit.Framework;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    public sealed class {{TYPE_NAME}}ModTests
    {
        [Test]
        public void Load_MigratesConfigAndRegistersLifetimeOwnedGreetingCommand()
        {
            var context = new FakeModContext();
            context.Config.Seed(1, new {{TYPE_NAME}}Config
            {
                Greeting = "  Hello from a migrated config!  "
            });

            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();

            Assert.That(context.Commands.ActiveCommandCount, Is.EqualTo(1));
            Assert.That(
                context.Commands.TryExecute("greet", Array.Empty<string>(), out var result),
                Is.True);
            Assert.That(result, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(result!.Succeeded, Is.True);
                Assert.That(result.Value, Is.EqualTo("Hello from a migrated config!"));
                Assert.That(
                    context.Logger.Entries,
                    Has.Some.Property("Message").EqualTo("Hello from a migrated config!"));
            });

            runner.Unload();

            Assert.That(context.Commands.ActiveCommandCount, Is.Zero);
            context.AssertNoLeaks();
        }
    }
}
