using System.Collections.Generic;

namespace TopiaForge.Chronos
{
    // One actor in the turn order: an opaque token, a relative speed, and accumulated energy. Seq is the
    // registration order, used as a stable tie-break so equal-energy actors resolve deterministically.
    internal sealed class TurnActor
    {
        public TopiaForge.Mods.TurnActorId Id;
        public float Speed;
        public float Energy;
        public long Seq;
    }

    // Pure (Unity-free) initiative/energy ordering — the roguelike "speed clock": each tick every actor gains
    // speed × dt of energy; an actor is ready when it crosses the per-turn threshold; the readiest acts, then spends
    // a threshold's worth (carryover preserved, so faster actors act more often). Deterministic tie-break by
    // registration order. Isolated here so the ordering unit-tests with no engine and no frame loop.
    internal sealed class TurnOrder
    {
        private readonly List<TurnActor> actors = new List<TurnActor>();
        private readonly float energyPerTurn;
        private long seqCounter;

        public TurnOrder(float energyPerTurn)
        {
            this.energyPerTurn = energyPerTurn > 0f ? energyPerTurn : 1f;
        }

        public int Count => actors.Count;

        public bool Register(TopiaForge.Mods.TurnActorId actor, float speed)
        {
            if (Find(actor) != null)
            {
                return false;
            }

            actors.Add(new TurnActor
            {
                Id = actor,
                Speed = speed > 0.01f ? speed : 0.01f,
                Energy = 0f,
                Seq = seqCounter++,
            });
            return true;
        }

        public bool Unregister(TopiaForge.Mods.TurnActorId actor)
        {
            for (var index = 0; index < actors.Count; index++)
            {
                if (actors[index].Id == actor)
                {
                    actors.RemoveAt(index);
                    return true;
                }
            }

            return false;
        }

        public bool Contains(TopiaForge.Mods.TurnActorId actor) => Find(actor) != null;

        // Accumulate energy for every actor (call only while waiting for the next turn, not mid-action, so a stalled
        // turn can't let others jump the queue).
        public void AddEnergy(float deltaTime)
        {
            if (deltaTime <= 0f)
            {
                return;
            }

            for (var index = 0; index < actors.Count; index++)
            {
                actors[index].Energy += actors[index].Speed * deltaTime;
            }
        }

        // The actor that should act now: the one at/over the threshold with the most energy (tie-break: earliest
        // registered). Null when nobody is ready yet.
        public TopiaForge.Mods.TurnActorId? NextReady()
        {
            TurnActor? best = null;
            for (var index = 0; index < actors.Count; index++)
            {
                var a = actors[index];
                if (a.Energy < energyPerTurn)
                {
                    continue;
                }

                if (best == null || a.Energy > best.Energy || (a.Energy == best.Energy && a.Seq < best.Seq))
                {
                    best = a;
                }
            }

            return best?.Id;
        }

        // Spend one turn's worth of energy on the actor that just acted (carryover kept so speed maps to frequency).
        public void SpendTurn(TopiaForge.Mods.TurnActorId actor)
        {
            var found = Find(actor);
            if (found != null)
            {
                found.Energy -= energyPerTurn;
            }
        }

        private TurnActor? Find(TopiaForge.Mods.TurnActorId actor)
        {
            for (var index = 0; index < actors.Count; index++)
            {
                if (actors[index].Id == actor)
                {
                    return actors[index];
                }
            }

            return null;
        }
    }
}
