using System;
using System.Collections.Generic;

namespace TopiaForge.Zombies
{
    internal sealed partial class ZombiesController
    {
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            var failures = new List<Exception>();
            for (var index = registrations.Count - 1; index >= 0; index--)
                TryCleanup(failures, registrations[index].Dispose);
            registrations.Clear();
            TryCleanup(failures, updateSubscription.Dispose);
            // No update loop remains; the session scope retains and drains any late native work.
            TryCleanup(failures, spawnSearch.Forget);
            TryCleanup(failures, returnOperation.Forget);
            TryCleanup(failures, shop.Dispose);
            TryCleanup(failures, conversation.Dispose);
            hordeMotionSuspendedForConversation = false;
            TryCleanup(failures, gameOverPresenter.Dispose);
            TryCleanup(failures, () => fireAction?.Dispose());
            TryCleanup(failures, () => overrideAction?.Dispose());
            TryCleanup(failures, () => broadcastAction?.Dispose());
            TryCleanup(failures, () => shopAction?.Dispose());
            TryCleanup(failures, gameOverPause.Dispose);
            TryCleanup(failures, () => superhotDriver?.Dispose());
            superhotDriver = null;
            TryCleanup(failures, () => playerExemption?.Dispose());
            playerExemption = null;
            TryCleanup(failures, RestoreNativeHealth);
            TryCleanup(failures, ClearEnemies);
            TryCleanup(failures, hud.Dispose);
            if (failures.Count != 0) throw new AggregateException("Zombies cleanup failed.", failures);
        }

        private static void TryCleanup(List<Exception> failures, Action action)
        {
            try { action(); } catch (Exception exception) { failures.Add(exception); }
        }
    }
}
