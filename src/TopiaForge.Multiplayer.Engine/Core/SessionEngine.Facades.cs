using System;
using System.Collections.Generic;

namespace TopiaForge.Multiplayer.Engine
{
    /// <summary>
    /// How facades open and close. A mod has one facade for its own context and one for each scope of it that resolves the
    /// session, such as a gamemode's server controller or a player's participant controller. Closing a facade retires what
    /// that facade made; only the owner's last facade retires the owner as a whole.
    /// </summary>
    internal sealed partial class SessionEngine
    {
        private const string FacadeClosedMessage = "The part of the mod that submitted this stopped.";

        private readonly object facadeGate = new object();
        private readonly Dictionary<string, int> openFacades = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>Gets how many facades of <paramref name="owner"/> are open and not yet retired.</summary>
        internal int OpenFacadeCount(string owner)
        {
            lock (facadeGate) return openFacades.TryGetValue(owner, out var count) ? count : 0;
        }

        /// <summary>Counts a facade its owner opened. Any thread; the count only grows here.</summary>
        internal void OpenFacade(string owner)
        {
            lock (facadeGate)
            {
                openFacades.TryGetValue(owner, out var count);
                openFacades[owner] = count + 1;
            }
        }

        /// <summary>
        /// Retires what a closing facade made, at the next settle point. On a host, the objects it spawned leave the
        /// session canonically, so every peer sees them go; on a client, the commands it submitted that still wait for the
        /// host complete as cancelled. Every other facade of the same owner keeps what it made, as a mod keeps its own
        /// drone while one of its session scopes stops. Once the owner's last facade has closed, the mod has left the
        /// session, and everything else of the owner retires too: a host's remaining objects, a client's copies of the
        /// owner's objects, and its replicated-object inputs, which belong to the owner as a whole.
        /// </summary>
        internal void CloseFacade(EngineSessionFacade facade) => Defer(() => CloseFacadeNow(facade));

        private void CloseFacadeNow(EngineSessionFacade facade)
        {
            bool last;
            lock (facadeGate)
            {
                openFacades.TryGetValue(facade.OwnerModId, out var count);
                last = count <= 1;
                if (last) openFacades.Remove(facade.OwnerModId);
                else openFacades[facade.OwnerModId] = count - 1;
            }

            if (last) RetireOwnerNow(facade.OwnerModId);
            else RetireFacadeNow(facade);
        }

        private void RetireFacadeNow(EngineSessionFacade facade)
        {
            if (ended) return;
            if (Role == EngineRole.Host)
            {
                var retired = new List<ObjectRecord>();
                foreach (var record in objectList)
                {
                    if (record.Life == ObjectLife.Live && ReferenceEquals(record.Spawner, facade)) retired.Add(record);
                }

                DespawnCanonically(retired);
                return;
            }

            foreach (var submission in pending.ToArray())
            {
                if (ReferenceEquals(submission.Submitter, facade)) CancelPending(submission, FacadeClosedMessage);
            }
        }
    }
}
