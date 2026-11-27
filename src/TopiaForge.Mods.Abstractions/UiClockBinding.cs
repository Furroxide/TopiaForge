using System;
using System.Runtime.CompilerServices;

namespace TopiaForge.Mods
{
    /// <summary>Attaches a deadline to its first timeline without changing the authored deadline.</summary>
    internal static class UiClockBinding
    {
        // Only the runtime's unique initial epoch is adopted, once, when its first solo provider attaches.
        // Weak keys keep the identity alive only while its epoch or a bound deadline still needs it.
        private static readonly ConditionalWeakTable<string, EpochIdentity> identities = new ConditionalWeakTable<string, EpochIdentity>();
        internal sealed class EpochIdentity
        {
            internal EpochIdentity(string value) { Value = value; }
            internal string Value { get; private set; }
            internal void Adopt(string value) { Value = value; }
        }

        private static EpochIdentity Identity(string epoch) => identities.GetValue(epoch, value => new EpochIdentity(value));

        internal static void AdoptInitialEpoch(string initialEpoch, string providerEpoch)
        {
            var identity = Identity(initialEpoch);
            if (identity.Value != initialEpoch)
                throw new InvalidOperationException("The initial UI clock was already adopted.");
            identity.Adopt(providerEpoch);
        }

        internal static bool IsCurrent(UiNode? node, string epoch)
        {
            if (node == null) return true;
            if (node is UiCountdown countdown) return countdown.ClockEpoch == epoch;
            if (node is UiActionSlot slot) return Matches(slot.Cooldown, epoch) && Matches(slot.Charges?.Refill, epoch);
            if (node is UiPips pips) return Matches(pips.Refill, epoch);
            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++)
            {
                if (!IsCurrent(children[index], epoch)) return false;
            }
            return true;
        }

        private static bool Matches(UiCooldown? cooldown, string epoch) =>
            cooldown == null || StringComparer.Ordinal.Equals(cooldown.ClockEpoch, epoch);

        internal static void Bind(UiNode? node, string epoch)
        {
            UiComposition.Validate(node);
            Check(node, epoch);
            Commit(node, epoch);
        }

        internal static void Check(UiNode? node, string epoch) => Visit(node, epoch, false);

        internal static void Commit(UiNode? node, string epoch) => Visit(node, epoch, true);

        private static void Visit(UiNode? node, string epoch, bool commit)
        {
            if (node == null) return;
            if (node is UiCountdown countdown)
            {
                if (countdown.ClockEpoch != null && countdown.ClockEpoch != epoch)
                    throw new ArgumentException("UiCountdown belongs to an ended clock epoch. Create a new deadline from IUiService.ClockTime.");
                if (commit) countdown.ClockIdentity = Identity(epoch);
                return;
            }
            if (node is UiActionSlot slot)
            {
                Visit(slot.Cooldown, epoch, commit);
                Visit(slot.Charges?.Refill, epoch, commit);
                return;
            }
            if (node is UiPips pips)
            {
                Visit(pips.Refill, epoch, commit);
                return;
            }
            var children = UiNodeChildren.Of(node);
            for (var index = 0; index < children.Count; index++) Visit(children[index], epoch, commit);
        }

        private static void Visit(UiCooldown? cooldown, string epoch, bool commit)
        {
            if (cooldown == null) return;
            if (commit) cooldown.CommitClock(Identity(epoch));
            else cooldown.CheckClock(epoch);
        }
    }
}
