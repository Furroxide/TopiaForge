using System;
using System.Runtime.CompilerServices;

namespace TopiaForge.Mods
{
    /// <summary>Attaches a deadline to its first timeline without changing the authored deadline.</summary>
    internal static class UiClockBinding
    {
        // Only the runtime's unique initial epoch is adopted, once, when its first solo provider attaches.
        // Weak keys keep the identity alive only while its epoch or a bound countdown still needs it.
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
            if (node is UiCountdown countdown) return countdown.ClockEpoch == epoch;
            if (node is UiLayoutNode layout)
            {
                for (var index = 0; index < layout.Children.Count; index++)
                    if (!IsCurrent(layout.Children[index], epoch)) return false;
            }
            else if (node is UiScroll scroll) return IsCurrent(scroll.Content, epoch);
            else if (node is UiSplitPane split) return IsCurrent(split.Primary, epoch) && IsCurrent(split.Secondary, epoch);
            return true;
        }

        internal static void Bind(UiNode? node, string epoch)
        {
            UiComposition.Validate(node);
            Visit(node, epoch, false);
            Visit(node, epoch, true);
        }

        private static void Visit(UiNode? node, string epoch, bool commit)
        {
            if (node is UiCountdown countdown)
            {
                if (countdown.ClockEpoch != null && countdown.ClockEpoch != epoch)
                    throw new ArgumentException("UiCountdown belongs to an ended clock epoch. Create a new deadline from IUiService.ClockTime.");
                if (commit) countdown.ClockIdentity = Identity(epoch);
            }
            else if (node is UiLayoutNode layout)
                foreach (var child in layout.Children) Visit(child, epoch, commit);
            else if (node is UiScroll scroll) Visit(scroll.Content, epoch, commit);
            else if (node is UiSplitPane split) { Visit(split.Primary, epoch, commit); Visit(split.Secondary, epoch, commit); }
        }
    }
}
