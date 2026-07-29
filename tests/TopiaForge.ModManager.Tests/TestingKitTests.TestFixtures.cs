using System;
using System.Collections.Generic;
using System.Threading;
using TopiaForge.Mods;
using TopiaForge.Mods.Testing;

namespace TopiaForge.ModManager.Tests
{
    internal static partial class TestingKitTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Testing kit test failed: " + message);
            }
        }

        private static void AssertThrows<TException>(Action action, string message)
            where TException : Exception
        {
            try
            {
                action();
            }
            catch (TException)
            {
                return;
            }

            throw new InvalidOperationException("Testing kit test failed: " + message);
        }

        private sealed class ProbeConfig
        {
            public int Value { get; set; }
        }

        private interface IProbeExtension
        {
        }

        private sealed class ProbeExtension : IProbeExtension
        {
        }

        private sealed class ProbeMod : TopiaForgeMod
        {
            private readonly List<string> order;

            public ProbeMod(List<string> order)
            {
                this.order = order;
            }

            protected override void OnLoad()
            {
                order.Add("load");
                Context.Lifetime.Defer(() => order.Add("first"));
                Context.Lifetime.Defer(() => order.Add("second"));
                Context.Events.SubscribeUpdate(_ => { });
                Assert(Context.Input.RegisterAction(new InputActionDefinition(
                    "test",
                    "Test",
                    new[] { InputBinding.Key("T") })).Succeeded,
                    "probe mod should register its input action");
            }

            protected override void OnUnload()
            {
                order.Add("unload");
            }
        }

        private sealed class FailingMod : TopiaForgeMod
        {
            private readonly List<string> order;

            public FailingMod(List<string> order)
            {
                this.order = order;
            }

            protected override void OnLoad()
            {
                order.Add("load");
                Context.Lifetime.Defer(() => order.Add("cleanup"));
                throw new InvalidOperationException("expected load failure");
            }

            protected override void OnUnload()
            {
                order.Add("unload");
            }
        }

        private sealed class ThrowingUnloadMod : TopiaForgeMod
        {
            protected override void OnLoad()
            {
            }

            protected override void OnUnload()
            {
                throw new InvalidOperationException("expected unload failure");
            }
        }

        private sealed class ResourceHeavyMod : TopiaForgeMod
        {
            private readonly IEntity entity;
            private readonly bool failAfterRegistration;

            public ResourceHeavyMod(IEntity entity, bool failAfterRegistration)
            {
                this.entity = entity;
                this.failAfterRegistration = failAfterRegistration;
            }

            protected override void OnLoad()
            {
                Context.Lifetime.Defer(() => { });
                Context.Events.SubscribeUpdate(_ => { });
                Context.Events.SubscribeFixedUpdate(_ => { });
                Context.Events.SubscribeLateUpdate(_ => { });
                Context.Events.SubscribeSceneLoaded(_ => { });
                Context.Scenes.SubscribeCheckpointChanged(_ => { });
                _ = Context.Scenes.LoadAsync(new SceneLoadRequest("PendingRoom"));
                Assert(Context.Input.RegisterAction(new InputActionDefinition(
                    "resource-probe",
                    "Resource probe",
                    new[] { InputBinding.Key("R"), InputBinding.GamepadButton(InputGamepadButton.North) })).Succeeded,
                    "resource-heavy mod should register its input action");
                Assert(Context.LocalPlayer.AcquireControl("resource test").Succeeded,
                    "resource-heavy mod should acquire player controls");
                Assert(Context.Entities.AcquireMotion(entity).Succeeded,
                    "resource-heavy mod should acquire entity motion");
                Assert(Context.Interactions.Register(
                    entity,
                    new InteractableDefinition("TEST"),
                    _ => { }).Succeeded,
                    "resource-heavy mod should register an interaction");

                var bundle = Context.Assets.LoadBundleAsync("assets/probe.bundle").Result.Value!;
                var prefab = Context.Assets.LoadPrefabAsync(bundle, "Probe").Result.Value!;
                Assert(Context.Assets.Spawn(new AssetSpawnRequest(prefab, TransformState.Identity)).Succeeded,
                    "resource-heavy mod should own a spawned entity");
                Assert(Context.Audio.Play(new AudioPlayRequest("test.probe", loop: true)).Succeeded,
                    "resource-heavy mod should own audio playback");
                Assert(Context.Ui.CreateSurface(new UiSurfaceRequest("probe", "Probe", "Ready")).Succeeded,
                    "resource-heavy mod should own a UI surface");
                Assert(Context.Ui.ShowModal(new UiModalRequest("Probe", "Continue?"), _ => { }).Succeeded,
                    "resource-heavy mod should own a modal");
                Assert(Context.Localization.Register(new LocalizationCatalog(
                    "en",
                    new Dictionary<string, string> { ["probe"] = "Probe" })).Succeeded,
                    "resource-heavy mod should own a localization catalog");
                Assert(Context.Commands.Register(
                    new CommandDefinition("probe", "Resource probe"),
                    _ => OperationResult<string>.Success(string.Empty)).Succeeded,
                    "resource-heavy mod should own a command");
                Assert(Context.Extensions.Register<IProbeExtension>(new ProbeExtension()).Succeeded,
                    "resource-heavy mod should own an extension provider");
                Assert(Context.Scheduler.Every(TimeSpan.FromSeconds(1), () => { }).Succeeded,
                    "resource-heavy mod should own repeating scheduled work");
                _ = Context.Scheduler.DelayAsync(TimeSpan.FromHours(1));
                _ = Context.Items.GiveAsync(new ItemGrantRequest("test.item"));

                if (failAfterRegistration)
                {
                    throw new InvalidOperationException("expected resource-heavy load failure");
                }
            }
        }
    }
}
