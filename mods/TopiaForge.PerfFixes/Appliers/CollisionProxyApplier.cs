using System.Collections.Generic;
using TopiaForge.Mods;
using UnityEngine;
using UnityEngine.Pool;

namespace TopiaForge.PerfFixes.Appliers
{
    /// <summary>
    /// FIX 3 — pooled, non-allocating <c>CollisionEventProxy</c> dispatch. The original
    /// <c>OnCollisionEnter/Exit</c> call <c>IterReceivers()</c>, which does
    /// <c>GetComponentsInParent&lt;ICollisionEventReceiver&gt;()</c> (a fresh array) inside a <c>yield</c>
    /// iterator (a state-machine object) — two heap allocations per body-part collision callback. We
    /// replace the dispatch with a pooled <c>List</c> + the non-allocating
    /// <c>GetComponentsInParent(false, list)</c> overload (identical component set, order, the
    /// <c>includeInactive:false</c> default, the self-GameObject skip, and the <c>enabled</c> guard are all
    /// preserved), removing the garbage with no behavior change. Any structural mismatch on a future game
    /// build self-disables this fix and falls back to the original method.
    /// </summary>
    internal sealed class CollisionProxyApplier : PerfApplierBase
    {
        private static bool active;

        private readonly PerfFixesConfig config;
        private readonly IModLogger logger;
        private readonly HarmonyLib.Harmony harmony;

        public CollisionProxyApplier(PerfFixesConfig config, IModLogger logger, HarmonyLib.Harmony harmony)
        {
            this.config = config;
            this.logger = logger;
            this.harmony = harmony;
        }

        public override string Name => "CollisionProxy";

        public override void Apply()
        {
            // Self-correct the persisted static: a disabled config must force the fix off regardless of any
            // state left by a prior load (the assembly never unloads under Mono).
            active = false;

            if (!config.CollisionProxyPooled)
            {
                return;
            }

            var collisionArg = new[] { typeof(Collision) };
            var patchedEnter = PatchUtil.TryPatchPrefix(harmony, logger, "CollisionEventProxy", "OnCollisionEnter",
                collisionArg, PatchUtil.Own(typeof(CollisionProxyApplier), nameof(OnCollisionEnterPrefix)));
            var patchedExit = PatchUtil.TryPatchPrefix(harmony, logger, "CollisionEventProxy", "OnCollisionExit",
                collisionArg, PatchUtil.Own(typeof(CollisionProxyApplier), nameof(OnCollisionExitPrefix)));

            if (patchedEnter || patchedExit)
            {
                active = true;
                logger.Info("PerfFixes: collision-proxy dispatch is now pooled (no per-collision GC allocation).");
            }
        }

        public override void Revert()
        {
            // Patch removal is centralized in the mod's UnpatchSelf; just stop serving.
            active = false;
        }

        private static bool OnCollisionEnterPrefix(object __instance, Collision collision)
        {
            return Dispatch(__instance, collision, enter: true);
        }

        private static bool OnCollisionExitPrefix(object __instance, Collision collision)
        {
            return Dispatch(__instance, collision, enter: false);
        }

        // Returns false to skip the original (we handled dispatch); true to fall back to the original.
        // This reproduces the original method exactly — same component set/order, self-skip, enabled guard,
        // and receiver calls — so a receiver's own exception propagates identically to vanilla. The only
        // structural difference (not our type) falls back to the original; nothing is dispatched first, so
        // there is never a double dispatch.
        private static bool Dispatch(object instance, Collision collision, bool enter)
        {
            if (!active || !(instance is Behaviour behaviour))
            {
                return true;
            }

            if (!behaviour.enabled)
            {
                return false; // original early-out: a disabled proxy dispatches nothing
            }

            var proxyObject = behaviour.gameObject;
            using (CollectionPool<List<global::ICollisionEventReceiver>, global::ICollisionEventReceiver>.Get(out var receivers))
            {
                // includeInactive:false matches the original no-arg GetComponentsInParent<T>() overload.
                behaviour.GetComponentsInParent(false, receivers);
                for (var i = 0; i < receivers.Count; i++)
                {
                    var receiver = receivers[i];
                    if (((MonoBehaviour)receiver).gameObject == proxyObject)
                    {
                        continue; // identical self-skip filter
                    }

                    if (enter)
                    {
                        receiver.OnProxyCollisionEnter(collision, proxyObject);
                    }
                    else
                    {
                        receiver.OnProxyCollisionExit(collision, proxyObject);
                    }
                }
            }

            return false;
        }
    }
}
