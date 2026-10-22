using System;
using System.Collections.Generic;
using TopiaForge.Mods;

namespace TopiaForge.ModManager.Voice
{
    /// <summary>Why a control cannot be bound to push-to-talk.</summary>
    internal enum VoiceBindingProblem
    {
        None = 0,

        /// <summary>Push-to-talk does not know the control, such as a gamepad button.</summary>
        Unknown = 1,

        /// <summary>One of the keys the game's own actions use: V, B, Q, E or Z.</summary>
        GameKey = 2,

        /// <summary>One of TopiaForge's own keys: F5, F8, F10, or Esc, which closes its windows.</summary>
        TopiaForgeKey = 3,

        /// <summary>The mouse's primary or secondary button, which the game acts on.</summary>
        GameMouseButton = 4,

        /// <summary>A control the game's current bindings use (<see cref="NativeInputCensus"/>).</summary>
        BoundByGame = 5,

        /// <summary>The other push-to-talk slot holds it already.</summary>
        AlreadyBound = 6
    }

    /// <summary>
    /// The push-to-talk binding, the rebindable action <see cref="ActionId"/>: two slots, a key and a mouse button, Caps Lock
    /// and Mouse Forward by default. A slot never takes a control the game or TopiaForge keeps: V, B, Q, E and Z, which the
    /// game's own actions use; F5, F8 and F10, which TopiaForge's creator workbench, UI gallery and manager use, and Esc,
    /// which closes its windows; the primary and secondary mouse buttons; and whatever else the game binds now, by the
    /// <see cref="NativeInputCensus"/>. A slot whose control turns out to be taken falls back to the first free control of
    /// its kind, so push-to-talk works with no setup whatever the game binds. Unity-free.
    /// </summary>
    internal static class VoicePushToTalk
    {
        internal const string ActionId = "topiaforge.voice.push-to-talk";

        internal const int SlotCount = 2;

        internal static readonly InputBinding DefaultKey = InputBinding.Key("CapsLock");

        internal static readonly InputBinding DefaultMouseButton = InputBinding.MouseButton(InputMouseButton.Forward);

        private static readonly string[] GameKeys = { "V", "B", "Q", "E", "Z" };

        private static readonly string[] TopiaForgeKeys = { "F5", "F8", "F10", "Escape" };

        /// <summary>The keys a key slot falls back to, in order, when its own is taken.</summary>
        private static readonly string[] KeyFallbacks = { "CapsLock", "BackQuote", "T", "G", "H", "X", "C", "Y", "U", "N", "M", "K", "J", "L", "I", "O", "P" };

        /// <summary>The mouse buttons a mouse slot falls back to, in order.</summary>
        private static readonly InputBinding[] MouseFallbacks =
        {
            InputBinding.MouseButton(InputMouseButton.Forward), InputBinding.MouseButton(InputMouseButton.Back), InputBinding.MouseButton(InputMouseButton.Middle)
        };

        private static readonly string PrimaryButton = InputMouseButton.Primary.ToString();

        private static readonly string SecondaryButton = InputMouseButton.Secondary.ToString();

        /// <summary>Gets the default binding of <paramref name="slot"/>: a key for slot 0, a mouse button for slot 1.</summary>
        internal static InputBinding DefaultOf(int slot) => RequireSlot(slot) == 0 ? DefaultKey : DefaultMouseButton;

        /// <summary>
        /// Gets why <paramref name="candidate"/> cannot be bound to <paramref name="slot"/>, or <see cref="VoiceBindingProblem.None"/>.
        /// The reserved controls come first, then what the game binds now, then the other slot.
        /// </summary>
        internal static VoiceBindingProblem Check(InputBinding candidate, int slot, NativeInputCensus census, IReadOnlyList<InputBinding?> slots)
        {
            if (census == null) throw new ArgumentNullException(nameof(census));
            if (slots == null) throw new ArgumentNullException(nameof(slots));
            RequireSlot(slot);
            var reserved = Reserved(candidate);
            if (reserved != VoiceBindingProblem.None) return reserved;
            if (census.Uses(candidate)) return VoiceBindingProblem.BoundByGame;
            for (var other = 0; other < slots.Count; other++)
            {
                if (other != slot && slots[other].HasValue && Same(slots[other]!.Value, candidate)) return VoiceBindingProblem.AlreadyBound;
            }

            return VoiceBindingProblem.None;
        }

        /// <summary>Gets why push-to-talk never takes <paramref name="candidate"/>, whatever the game binds, or None.</summary>
        internal static VoiceBindingProblem Reserved(InputBinding candidate)
        {
            if (!VoiceControls.TryFind(candidate, out var control)) return VoiceBindingProblem.Unknown;
            if (control.IsMouseButton)
            {
                var primary = string.Equals(candidate.Control, PrimaryButton, StringComparison.Ordinal);
                var secondary = string.Equals(candidate.Control, SecondaryButton, StringComparison.Ordinal);
                return primary || secondary ? VoiceBindingProblem.GameMouseButton : VoiceBindingProblem.None;
            }

            if (Array.IndexOf(GameKeys, candidate.Control) >= 0) return VoiceBindingProblem.GameKey;
            return Array.IndexOf(TopiaForgeKeys, candidate.Control) >= 0 ? VoiceBindingProblem.TopiaForgeKey : VoiceBindingProblem.None;
        }

        /// <summary>Says, in the player's words, why <paramref name="candidate"/> cannot be push-to-talk; empty for no problem.</summary>
        internal static string Explain(VoiceBindingProblem problem, InputBinding candidate)
        {
            var name = VoiceControls.Describe(candidate);
            switch (problem)
            {
                case VoiceBindingProblem.Unknown:
                    return "Push-to-talk can use a keyboard key or a mouse button. Press another one.";
                case VoiceBindingProblem.GameKey:
                    return name + " is one of the game's own keys. Press another key.";
                case VoiceBindingProblem.TopiaForgeKey:
                    return name + " opens or closes TopiaForge's windows. Press another key.";
                case VoiceBindingProblem.GameMouseButton:
                    return "The left and right mouse buttons belong to the game. Press another button or a key.";
                case VoiceBindingProblem.BoundByGame:
                    return "The game already uses " + name + ". Press another key or button.";
                case VoiceBindingProblem.AlreadyBound:
                    return name + " is already push-to-talk.";
                default:
                    return string.Empty;
            }
        }

        /// <summary>
        /// Fills <paramref name="into"/> with the bindings push-to-talk uses: each slot's chosen control while it is free, or
        /// else the first free control of the same kind that the other slot does not hold, and nothing when none is left.
        /// Returns the number of slots that fell back, so the page can say so.
        /// </summary>
        internal static int Resolve(IReadOnlyList<InputBinding?> chosen, NativeInputCensus census, InputBinding?[] into)
        {
            if (chosen == null) throw new ArgumentNullException(nameof(chosen));
            if (census == null) throw new ArgumentNullException(nameof(census));
            if (into == null || into.Length != SlotCount) throw new ArgumentException("Push-to-talk has " + SlotCount + " slots.", nameof(into));
            var fellBack = 0;

            // Earlier slots win: a later slot that wants a control an earlier one holds falls back.
            Array.Clear(into, 0, into.Length);
            for (var slot = 0; slot < SlotCount; slot++)
            {
                var wanted = slot < chosen.Count ? chosen[slot] : null;
                if (wanted.HasValue && IsFree(wanted.Value, slot, census, into))
                {
                    into[slot] = wanted.Value;
                    continue;
                }

                if (!wanted.HasValue) continue;
                fellBack++;
                into[slot] = FirstFree(wanted.Value.Kind == InputBindingKind.MouseButton, slot, census, into);
            }

            return fellBack;
        }

        /// <summary>Gets whether two bindings name the same control.</summary>
        internal static bool Same(InputBinding left, InputBinding right) =>
            left.Kind == right.Kind && string.Equals(left.Control, right.Control, StringComparison.Ordinal);

        private static bool IsFree(InputBinding candidate, int slot, NativeInputCensus census, IReadOnlyList<InputBinding?> taken) =>
            Check(candidate, slot, census, taken) == VoiceBindingProblem.None;

        private static InputBinding? FirstFree(bool mouseButton, int slot, NativeInputCensus census, IReadOnlyList<InputBinding?> taken)
        {
            if (mouseButton)
            {
                foreach (var candidate in MouseFallbacks)
                {
                    if (IsFree(candidate, slot, census, taken)) return candidate;
                }

                return null;
            }

            foreach (var key in KeyFallbacks)
            {
                var candidate = InputBinding.Key(key);
                if (IsFree(candidate, slot, census, taken)) return candidate;
            }

            return null;
        }

        private static int RequireSlot(int slot)
        {
            if (slot < 0 || slot >= SlotCount) throw new ArgumentOutOfRangeException(nameof(slot));
            return slot;
        }
    }
}
