using NUnit.Framework;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    public sealed class {{TYPE_NAME}}ModTests
    {
        [Test]
        public void SceneLoad_LoadsPrefabAndLifetimeReleasesEveryAssetHandle()
        {
            var context = new FakeModContext();
            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();

            context.Scenes.Load("PuzzleRoom");

            Assert.Multiple(() =>
            {
                Assert.That(context.Assets.ActiveBundleCount, Is.EqualTo(1));
                Assert.That(context.Assets.ActivePrefabCount, Is.EqualTo(1));
                Assert.That(context.Assets.ActiveSpawnCount, Is.EqualTo(1));
                Assert.That(
                    context.Logger.Entries,
                    Has.Some.Property("Message").Contains("Spawned"));
            });

            runner.Unload();

            Assert.Multiple(() =>
            {
                Assert.That(context.Assets.ActiveBundleCount, Is.Zero);
                Assert.That(context.Assets.ActivePrefabCount, Is.Zero);
                Assert.That(context.Assets.ActiveSpawnCount, Is.Zero);
            });
            context.AssertNoLeaks();
        }
    }
}
