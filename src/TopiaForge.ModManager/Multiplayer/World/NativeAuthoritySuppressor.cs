using System;
using System.Collections.Generic;

namespace TopiaForge.ModManager
{
    /// <summary>What one restore did with the switches the suppressor had changed.</summary>
    internal readonly struct NativeAuthorityRestore
    {
        internal NativeAuthorityRestore(int restored, int leftChanged, int gone, int failed, int unwritten)
        {
            Restored = restored;
            LeftChanged = leftChanged;
            Gone = gone;
            Failed = failed;
            Unwritten = unwritten;
        }

        /// <summary>Gets the switches written back to the value they had before suppression.</summary>
        internal int Restored { get; }

        /// <summary>Gets the switches something else changed after suppression, whose value was left as that change set it.</summary>
        internal int LeftChanged { get; }

        /// <summary>Gets the switches whose native object no longer exists.</summary>
        internal int Gone { get; }

        /// <summary>Gets the switches that threw while being read or written back.</summary>
        internal int Failed { get; }

        /// <summary>Gets the switches whose suppression write threw and that still had their own value.</summary>
        internal int Unwritten { get; }

        public override string ToString() =>
            "restored " + Restored + ", left changed " + LeftChanged + ", gone " + Gone + ", failed " + Failed + ", unwritten " + Unwritten;
    }

    /// <summary>
    /// Turns off native authority on a joined player, and undoes exactly that when the player leaves. For each switch it
    /// reads the value first, records it, and only then writes the suppressed value, so a failure part way through a
    /// census leaves a ledger that still names every change made. A switch already at the suppressed value is left
    /// untouched and never restored, and the same native setting is suppressed once however often a scene is censused.
    /// Restoring walks the ledger backwards: a switch still at the value written here gets its original back, a switch
    /// something else changed since keeps that change, and a switch that throws is counted and the rest are still restored.
    /// Afterwards nothing is left at a value only this process imposed. Main thread only.
    /// </summary>
    internal sealed class NativeAuthoritySuppressor
    {
        private readonly List<Change> changes = new List<Change>();
        private readonly HashSet<SwitchKey> seen = new HashSet<SwitchKey>();
        private int failures;

        /// <summary>Gets how many switches are suppressed now.</summary>
        internal int SuppressedCount => changes.Count;

        /// <summary>Gets how many reads and writes threw during suppression since the last restore.</summary>
        internal int SuppressionFailures => failures;

        /// <summary>Counts the suppressed switches of one kind.</summary>
        internal int CountSuppressed(NativeAuthorityKind kind)
        {
            var count = 0;
            foreach (var change in changes)
            {
                if (change.Switch.Kind == kind) count++;
            }

            return count;
        }

        /// <summary>
        /// Suppresses every target not yet seen. Returns how many switches changed. A switch that throws is counted, and the
        /// targets after it are still suppressed.
        /// </summary>
        internal int Suppress(IReadOnlyList<NativeAuthorityTarget> targets)
        {
            if (targets == null) throw new ArgumentNullException(nameof(targets));
            var changed = 0;
            for (var index = 0; index < targets.Count; index++)
            {
                if (Suppress(targets[index])) changed++;
            }

            return changed;
        }

        /// <summary>Suppresses one target, returning whether its switch is recorded as changed.</summary>
        internal bool Suppress(NativeAuthorityTarget target)
        {
            var nativeSwitch = target.Switch;
            bool original;
            try
            {
                if (!nativeSwitch.IsAlive) return false;
                if (!seen.Add(new SwitchKey(nativeSwitch))) return false;
                original = nativeSwitch.Read();
            }
            catch (Exception)
            {
                failures++;
                return false;
            }

            if (original == target.SuppressedValue) return false;

            // Recorded before the write, so a write that throws, or a failure after it, is still undone on restore.
            changes.Add(new Change(nativeSwitch, original, target.SuppressedValue, false));
            try
            {
                nativeSwitch.Write(target.SuppressedValue);
                changes[changes.Count - 1] = changes[changes.Count - 1].Written();
            }
            catch (Exception)
            {
                failures++;
            }

            return true;
        }

        /// <summary>Whether this process suppressed <paramref name="nativeSwitch"/> and has not restored it.</summary>
        internal bool IsSuppressed(INativeAuthoritySwitch nativeSwitch)
        {
            if (nativeSwitch == null) throw new ArgumentNullException(nameof(nativeSwitch));
            var key = new SwitchKey(nativeSwitch);
            foreach (var change in changes)
            {
                if (new SwitchKey(change.Switch).Equals(key)) return true;
            }

            return false;
        }

        /// <summary>
        /// Undoes every change, newest first, and forgets what it suppressed. A switch still at the suppressed value gets its
        /// original back, even one whose suppression write threw after all.
        /// </summary>
        internal NativeAuthorityRestore Restore()
        {
            int restored = 0, leftChanged = 0, gone = 0, failed = 0, unwritten = 0;
            for (var index = changes.Count - 1; index >= 0; index--)
            {
                var change = changes[index];
                try
                {
                    if (!change.Switch.IsAlive)
                    {
                        gone++;
                        continue;
                    }

                    if (change.Switch.Read() != change.Suppressed)
                    {
                        if (change.IsWritten) leftChanged++;
                        else unwritten++;
                        continue;
                    }

                    change.Switch.Write(change.Original);
                    restored++;
                }
                catch (Exception)
                {
                    failed++;
                }
            }

            changes.Clear();
            seen.Clear();
            failures = 0;
            return new NativeAuthorityRestore(restored, leftChanged, gone, failed, unwritten);
        }

        private readonly struct Change
        {
            internal Change(INativeAuthoritySwitch nativeSwitch, bool original, bool suppressed, bool written)
            {
                Switch = nativeSwitch;
                Original = original;
                Suppressed = suppressed;
                IsWritten = written;
            }

            internal INativeAuthoritySwitch Switch { get; }

            internal bool Original { get; }

            internal bool Suppressed { get; }

            /// <summary>Gets whether the suppression write returned.</summary>
            internal bool IsWritten { get; }

            internal Change Written() => new Change(Switch, Original, Suppressed, true);
        }

        /// <summary>A native setting: its object and what the switch controls on it.</summary>
        private readonly struct SwitchKey : IEquatable<SwitchKey>
        {
            private readonly object target;
            private readonly NativeAuthorityKind kind;

            internal SwitchKey(INativeAuthoritySwitch nativeSwitch)
            {
                target = nativeSwitch.Target ?? throw new InvalidOperationException("A native switch names its target.");
                kind = nativeSwitch.Kind;
            }

            public bool Equals(SwitchKey other) => kind == other.kind && Equals(target, other.target);

            public override bool Equals(object? obj) => obj is SwitchKey other && Equals(other);

            public override int GetHashCode() => ((int)kind * 397) ^ target.GetHashCode();
        }
    }
}
