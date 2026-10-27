using System;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>Registers the session's pause actions; their callbacks recheck their lifetime and use the existing screens.</summary>
    internal sealed class InSessionPauseEntries : IDisposable
    {
        private readonly ISessionScreen session;
        private readonly Action showSession;
        private readonly IDisposable?[] leases = new IDisposable?[3];
        private IWorldPauseMenuService? provider;
        private object? world;
        private int role;
        private int generation;

        internal InSessionPauseEntries(ISessionScreen session, Action showSession)
        {
            this.session = session;
            this.showSession = showSession;
        }

        internal void Tick(IWorldPauseMenuService? next, object? worldIdentity, bool gameplay)
        {
            var nextRole = gameplay ? session.CanEnd ? 1 : session.CanLeave ? 2 : 0 : 0;
            if (ReferenceEquals(next, provider) && ReferenceEquals(world, worldIdentity) && role == nextRole) return;
            Dispose();
            provider = next;
            world = worldIdentity;
            role = nextRole;
            if (provider == null || role == 0) return;
            var current = generation;
            Add(0, "invite", "INVITE", () => { if (current == generation) showSession(); });
            Add(1, "players", "PLAYERS", () => { if (current == generation) showSession(); });
            Add(2, "leave", role == 1 ? "END SESSION" : "DISCONNECT", () =>
            {
                if (current != generation) return;
                if (role == 1) session.EndForEveryone();
                else session.Leave();
            });
        }

        private void Add(int index, string id, string label, Action action)
        {
            // SessionPresenter owns the destructive confirmation and its current-session premise, avoiding two dialogs.
            if (provider!.RegisterAction(new WorldPauseAction("topiaforge.multiplayer." + id, label, action, order: 100 + index)).TryGetValue(out var lease)) leases[index] = lease;
        }

        public void Dispose()
        {
            generation++;
            for (var index = 0; index < leases.Length; index++) { leases[index]?.Dispose(); leases[index] = null; }
            provider = null;
            world = null;
            role = 0;
        }
    }
}
