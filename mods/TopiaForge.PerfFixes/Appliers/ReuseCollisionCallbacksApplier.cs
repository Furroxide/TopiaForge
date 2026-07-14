using System;
using TopiaForge.Mods;
using UnityEngine;

namespace TopiaForge.PerfFixes.Appliers
{
    /// <summary>
    /// FIX 1 — set <c>Physics.reuseCollisionCallbacks = true</c>. Unity defaults it to false, allocating a
    /// fresh managed <c>Collision</c> object for every OnCollisionEnter/Exit callback. The game never sets
    /// it and every collision handler consumes the <c>Collision</c> synchronously (no OnCollisionStay, no
    /// <c>.contacts</c> retained past the callback — the one async path copies a <c>ContactPoint</c> struct
    /// first), so reusing the buffer is behavior-identical and just removes the per-collision GC garbage.
    /// </summary>
    internal sealed class ReuseCollisionCallbacksApplier : PerfApplierBase
    {
        private readonly PerfFixesConfig config;
        private readonly IModLogger logger;
        private bool original;
        private bool captured;

        public ReuseCollisionCallbacksApplier(PerfFixesConfig config, IModLogger logger)
        {
            this.config = config;
            this.logger = logger;
        }

        public override string Name => "ReuseCollisionCallbacks";

        public override void Apply()
        {
            if (!config.ReuseCollisionCallbacks)
            {
                return;
            }

            try
            {
                original = Physics.reuseCollisionCallbacks;
                captured = true;
                Physics.reuseCollisionCallbacks = true;
                logger.Info("PerfFixes: reuseCollisionCallbacks enabled (removes per-collision GC allocation).");
            }
            catch (Exception ex)
            {
                logger.Warn("PerfFixes: could not set reuseCollisionCallbacks: " + ex.Message);
            }
        }

        public override void OnSceneLoaded(string sceneName)
        {
            // Sticky engine flag; re-assert cheaply in case anything reset it.
            if (captured && config.ReuseCollisionCallbacks)
            {
                try
                {
                    Physics.reuseCollisionCallbacks = true;
                }
                catch
                {
                    // Non-fatal.
                }
            }
        }

        public override void Revert()
        {
            if (!captured)
            {
                return;
            }

            try
            {
                Physics.reuseCollisionCallbacks = original;
            }
            catch
            {
                // Best-effort.
            }
        }
    }
}
