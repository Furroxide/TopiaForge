using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.ModManager.Core.Multiplayer;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>
    /// The runtime's answer to a request to change realm: the realm rule's verdict and effect, and what came of it. A
    /// refusal by the rule keeps its verdict, so the interface can tell "Leave current game?"
    /// (<see cref="NetRealmTransitionVerdict.RequiresLeave"/>) from "try again shortly"
    /// (<see cref="NetRealmTransitionVerdict.Busy"/>) and from a standalone mod
    /// (<see cref="NetRealmTransitionVerdict.StandaloneRefused"/>). Its texts are this game's own and never hold an
    /// address.
    /// </summary>
    internal sealed class MultiplayerRealmChange
    {
        internal MultiplayerRealmChange(NetRealmTransition transition, OperationResult<bool> result)
        {
            Transition = transition;
            Result = result ?? throw new ArgumentNullException(nameof(result));
        }

        /// <summary>Gets the realm rule's decision.</summary>
        internal NetRealmTransition Transition { get; }

        /// <summary>Gets the realm rule's verdict: the code the interface branches on.</summary>
        internal NetRealmTransitionVerdict Verdict => Transition.Verdict;

        /// <summary>Gets what an allowed change does to the local session.</summary>
        internal NetRealmTransitionEffect Effect => Transition.Effect;

        /// <summary>Gets the provider's answer when the rule allowed the change, or the refusal.</summary>
        internal OperationResult<bool> Result { get; }

        internal bool Succeeded => Result.Succeeded;

        public override string ToString() => Transition + (Succeeded ? string.Empty : " (" + Result.ErrorCode + ")");
    }

    /// <summary>What the scene census at a promotion receives.</summary>
    internal sealed class MultiplayerPromotion
    {
        internal MultiplayerPromotion(NetRealmTransitionEffect effect, SessionPhase phase, IReadOnlyList<MultiplayerSpawnRecord> spawns)
        {
            if (effect != NetRealmTransitionEffect.PromoteInPlace && effect != NetRealmTransitionEffect.StartAsHost)
            {
                throw new ArgumentOutOfRangeException(nameof(effect));
            }

            Effect = effect;
            Phase = phase;
            Spawns = spawns ?? throw new ArgumentNullException(nameof(spawns));
        }

        /// <summary>
        /// Gets <see cref="NetRealmTransitionEffect.PromoteInPlace"/> when a world session was running as this process
        /// became a host ("Open to multiplayer"), and <see cref="NetRealmTransitionEffect.StartAsHost"/> otherwise.
        /// </summary>
        internal NetRealmTransitionEffect Effect { get; }

        /// <summary>Gets the local session's phase at the promotion.</summary>
        internal SessionPhase Phase { get; }

        /// <summary>Gets the live spawns recorded before the promotion, in the order they were recorded.</summary>
        internal IReadOnlyList<MultiplayerSpawnRecord> Spawns { get; }
    }

    /// <summary>
    /// The scene census taken when this process opens its session to other players. The network world (WS3.6) fills it:
    /// it keys the scene's objects and baselines the spawns the runtime recorded at spawn time, so players who join see
    /// what was there before the promotion. It runs once per promotion, on the main thread, after the pump that made this
    /// process a host and before that frame's world events.
    /// </summary>
    internal interface IMultiplayerPromotionCensus
    {
        void TakeCensus(MultiplayerPromotion promotion);
    }

    /// <summary>
    /// A slice that keeps state alongside the provider: the network world (WS3.6) and session follow (WS3.8). It hears, on
    /// the main thread, when a control attaches, when the realm changes and when the control is about to detach. A change
    /// of realm is told after the pump that shows it, before that frame's world events, and before a new host's census.
    /// None of these runs on an offline frame.
    /// </summary>
    internal interface IMultiplayerRuntimeObserver
    {
        /// <summary>A control attached; the runtime drives it from the next frame.</summary>
        void Attached(IInternalMultiplayerControl control);

        /// <summary>The realm changed from <paramref name="previous"/> to <paramref name="next"/>, as the last pump showed it.</summary>
        void RealmChanged(NetRealm previous, NetRealm next);

        /// <summary>The control is about to detach, after the realm has returned to offline.</summary>
        void Detaching(IInternalMultiplayerControl control);
    }
}
