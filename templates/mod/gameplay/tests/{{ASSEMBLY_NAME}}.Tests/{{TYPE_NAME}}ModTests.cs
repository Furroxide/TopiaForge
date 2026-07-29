using System;
using NUnit.Framework;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    public sealed class {{TYPE_NAME}}ModTests
    {
        [Test]
        public void ScanAction_RaycastsFromPlayerAimAndReportsHit()
        {
            var context = new FakeModContext();
            var target = context.Entities.Create("Puzzle Robot", new Vec3(0f, 0f, 8f));
            context.LocalPlayer.Snapshot = new PlayerSnapshot(
                Vec3.Zero,
                new Ray(Vec3.Zero, new Vec3(0f, 0f, 1f)));
            context.Physics.RaycastHit = new PhysicsHit(
                target,
                new Vec3(0f, 0f, 8f),
                new Vec3(0f, 0f, -1f),
                8f);

            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();

            Assert.That(context.Input.ActiveActionCount, Is.EqualTo(1));
            context.Input.SetValue({{TYPE_NAME}}Controller.ScanActionName, 1f);
            context.AdvanceFrame(TimeSpan.FromMilliseconds(16));

            Assert.Multiple(() =>
            {
                Assert.That(context.Physics.RaycastCount, Is.EqualTo(1));
                Assert.That(context.Physics.LastMaximumDistance, Is.EqualTo(30f));
                Assert.That(context.Ui.Toasts, Has.Count.EqualTo(1));
                Assert.That(context.Ui.Toasts[0].Tone, Is.EqualTo(UiTone.Success));
                Assert.That(context.Ui.Toasts[0].Message, Does.Contain("Puzzle Robot"));
                Assert.That(
                    context.Logger.Entries,
                    Has.Some.Property("Message").Contains("Puzzle Robot"));
            });

            runner.Unload();

            Assert.That(context.Input.ActiveActionCount, Is.Zero);
            context.AssertNoLeaks();
        }
    }
}
