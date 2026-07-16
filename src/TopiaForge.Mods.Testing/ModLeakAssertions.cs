using System.Collections.Generic;

namespace TopiaForge.Mods.Testing
{
    /// <summary>Runner-neutral assertions for resources owned by a fake mod context.</summary>
    public static class ModLeakAssertions
    {
        /// <summary>Throws when any tracked SDK resource remains active.</summary>
        /// <param name="context">The context that has completed unload or failed-load cleanup.</param>
        public static void AssertNoLeaks(FakeModContext context)
        {
            if (context == null)
            {
                throw new System.ArgumentNullException(nameof(context));
            }

            var leaks = new List<string>();
            Add(leaks, "lifetime resources", context.Lifetime.TrackedResourceCount);
            Add(leaks, "event subscriptions", context.Events.ActiveSubscriptionCount);
            Add(leaks, "input actions", context.Input.ActiveActionCount);
            Add(leaks, "player-control leases", context.Player.ActiveControlLeaseCount);
            Add(leaks, "entity-motion leases", context.Entities.ActiveMotionCount);
            Add(leaks, "scheduled operations", context.Scheduler.PendingCount);
            Add(leaks, "pending scene loads", context.Scenes.PendingLoadCount);
            Add(leaks, "checkpoint subscriptions", context.Scenes.ActiveCheckpointSubscriptionCount);
            Add(leaks, "interactions", context.Interactions.ActiveRegistrationCount);
            Add(leaks, "asset bundles", context.Assets.ActiveBundleCount);
            Add(leaks, "prefab handles", context.Assets.ActivePrefabCount);
            Add(leaks, "spawned entities", context.Assets.ActiveSpawnCount);
            Add(leaks, "audio playbacks", context.Audio.ActivePlaybacks.Count);
            Add(leaks, "UI surfaces", context.Ui.Surfaces.Count);
            Add(leaks, "UI modals", context.Ui.Modals.Count);
            Add(leaks, "localization catalogs", context.Localization.ActiveCatalogCount);
            Add(leaks, "commands", context.Commands.ActiveCommandCount);
            Add(leaks, "extension providers", context.Extensions.ActiveProviderCount);
            if (leaks.Count != 0)
            {
                throw new ModTestAssertionException("Detected leaked mod resources: " + string.Join(", ", leaks) + ".");
            }
        }

        private static void Add(List<string> leaks, string name, int count)
        {
            if (count != 0)
            {
                leaks.Add(name + "=" + count);
            }
        }
    }
}
