using System.Linq;
using NUnit.Framework;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    [TestFixture]
    public sealed class {{TYPE_NAME}}ModTests
    {
        [Test]
        public void LoadPublishesServiceAndUnloadReleasesIt()
        {
            var context = new FakeModContext();
            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);

            runner.Load();

            Assert.That(context.Extensions.TryGet<I{{TYPE_NAME}}Service>(out var service), Is.True);
            Assert.That(service!.Ping("hello"), Is.EqualTo("hello"));
            Assert.That(context.Logger.Entries.Any(entry => entry.Message.Contains("registered")), Is.True);

            runner.Unload();

            Assert.That(context.Extensions.TryGet<I{{TYPE_NAME}}Service>(out _), Is.False);
            context.AssertNoLeaks();
        }
    }
}
