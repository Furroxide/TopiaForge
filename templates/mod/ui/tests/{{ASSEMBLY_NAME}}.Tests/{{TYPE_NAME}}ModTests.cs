using System;
using NUnit.Framework;
using TopiaForge.Mods.Testing;

namespace {{ASSEMBLY_NAME}}.Tests
{
    public sealed class {{TYPE_NAME}}ModTests
    {
        [Test]
        public void ToggleAction_CreatesShowsAndHidesSafeUiSurface()
        {
            var context = new FakeModContext();
            using var runner = ModLifecycleRunner.Create<{{TYPE_NAME}}Mod>(context);
            runner.Load();

            Assert.Multiple(() =>
            {
                Assert.That(context.Input.ActiveActionCount, Is.EqualTo(1));
                Assert.That(context.Ui.Surfaces, Is.Empty);
            });

            PressToggle(context);
            var surface = context.Ui.Surfaces[0];
            Assert.Multiple(() =>
            {
                Assert.That(context.Ui.Surfaces, Has.Count.EqualTo(1));
                Assert.That(surface.IsVisible, Is.True);
                Assert.That(surface.Content, Is.Not.Null);
                Assert.That(surface.TryFindNode({{TYPE_NAME}}Mod.NotifyButtonId, out _), Is.True);
            });

            Assert.That(surface.ChangeText({{TYPE_NAME}}Mod.GreetingInputId, "Hello, Robotopia!" ).Succeeded, Is.True);
            Assert.That(surface.ChangeDropdown({{TYPE_NAME}}Mod.ToneDropdownId, "success").Succeeded, Is.True);
            Assert.That(surface.SelectListItem({{TYPE_NAME}}Mod.TopicListId, "testing").Succeeded, Is.True);
            Assert.That(surface.ChangeSlider({{TYPE_NAME}}Mod.UiScaleSliderId, 1.25f).Succeeded, Is.True);
            Assert.That(surface.ActivateButton({{TYPE_NAME}}Mod.NotifyButtonId).Succeeded, Is.True);
            Assert.Multiple(() =>
            {
                Assert.That(surface.Body, Does.Contain("Hello, Robotopia!"));
                Assert.That(surface.Body, Does.Contain("testing"));
                Assert.That(context.Ui.Accessibility.UiScale, Is.EqualTo(1.25f));
                Assert.That(context.Ui.Toasts, Has.Count.EqualTo(1));
                Assert.That(context.Ui.Toasts[0].Tone, Is.EqualTo(TopiaForge.Mods.UiTone.Success));
            });

            Assert.That(surface.ChangeToggle({{TYPE_NAME}}Mod.NotificationsToggleId, false).Succeeded, Is.True);
            Assert.That(surface.ActivateButton({{TYPE_NAME}}Mod.NotifyButtonId).Succeeded, Is.True);
            Assert.That(context.Ui.Toasts, Has.Count.EqualTo(1),
                "the toggle callback should disable new notifications");

            PressToggle(context);
            Assert.That(surface.IsVisible, Is.False);

            PressToggle(context);
            Assert.That(surface.IsVisible, Is.True);

            runner.Unload();

            Assert.Multiple(() =>
            {
                Assert.That(context.Input.ActiveActionCount, Is.Zero);
                Assert.That(context.Ui.Surfaces, Is.Empty);
            });
            context.AssertNoLeaks();
        }

        private static void PressToggle(FakeModContext context)
        {
            context.Input.SetValue({{TYPE_NAME}}Mod.ToggleActionName, 0f);
            context.AdvanceFrame(TimeSpan.FromMilliseconds(16));
            context.Input.SetValue({{TYPE_NAME}}Mod.ToggleActionName, 1f);
            context.AdvanceFrame(TimeSpan.FromMilliseconds(16));
        }
    }
}
