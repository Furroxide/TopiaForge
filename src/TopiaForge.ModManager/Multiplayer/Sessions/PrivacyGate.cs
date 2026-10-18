using System;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// The approved privacy prompts, in the order hosting and joining need them, with the approved wording from
    /// <see cref="MultiplayerPrivacyText"/>. The first-time disclosure shows before the first host or join, and again when
    /// its version changes; acknowledging it is stored in manager data. The per-session prompt shows whenever a session can
    /// only be reached directly while Hide my IP is on, and is never remembered. Saying no to either, or closing it, leaves
    /// Hide my IP on and calls the abandon path, so nothing is contacted.
    /// </summary>
    internal sealed class PrivacyGate
    {
        private readonly MultiplayerSettingsService settings;
        private readonly Action<ScreenPrompt> ask;

        /// <param name="settings">Where the acknowledged disclosure version and Hide my IP live.</param>
        /// <param name="ask">Shows a prompt; the gate continues when the player answers it.</param>
        internal PrivacyGate(MultiplayerSettingsService settings, Action<ScreenPrompt> ask)
        {
            this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
            this.ask = ask ?? throw new ArgumentNullException(nameof(ask));
        }

        /// <summary>
        /// Shows the disclosure if this player has not acknowledged its current version, then continues. While it shows,
        /// <paramref name="holds"/> says whether what it guards still applies; once it does not, the disclosure is withdrawn
        /// unacknowledged and <paramref name="abandoned"/> runs.
        /// </summary>
        internal void EnsureDisclosure(Action proceed, Action abandoned, Func<bool>? holds = null)
        {
            if (proceed == null) throw new ArgumentNullException(nameof(proceed));
            if (abandoned == null) throw new ArgumentNullException(nameof(abandoned));
            if (!settings.Current.NeedsDisclosure)
            {
                proceed();
                return;
            }

            ask(PrivacyPrompts.Disclosure(acknowledged =>
            {
                if (!acknowledged)
                {
                    abandoned();
                    return;
                }

                settings.Update(settings.Current.AcknowledgeDisclosure());
                proceed();
            }, holds));
        }

        /// <summary>
        /// Decides whether a session that can only be reached directly may be: at once when Hide my IP is off, otherwise
        /// only once the player allows it for this session. <paramref name="proceed"/> receives the session's choice. A
        /// prompt whose <paramref name="holds"/> stops holding is withdrawn, which allows nothing.
        /// </summary>
        internal void EnsureDirect(Action proceed, Action abandoned, Func<bool>? holds = null)
        {
            if (proceed == null) throw new ArgumentNullException(nameof(proceed));
            if (abandoned == null) throw new ArgumentNullException(nameof(abandoned));
            if (!settings.Current.HideMyIp)
            {
                proceed();
                return;
            }

            ask(PrivacyPrompts.Direct(allowed =>
            {
                if (allowed) proceed();
                else abandoned();
            }, holds));
        }

        /// <summary>
        /// Turns Hide my IP off only once the player acknowledges the disclosure again, which explains what direct sessions
        /// show; closing it leaves the setting on. Turning it on needs nothing.
        /// </summary>
        internal void SetHideMyIp(bool on, Action<bool>? changed = null)
        {
            if (on)
            {
                if (!settings.Current.HideMyIp) settings.Update(settings.Current.WithHideMyIp(true));
                changed?.Invoke(true);
                return;
            }

            if (!settings.Current.HideMyIp)
            {
                changed?.Invoke(false);
                return;
            }

            ask(PrivacyPrompts.Disclosure(acknowledged =>
            {
                if (acknowledged) settings.Update(settings.Current.AcknowledgeDisclosure().WithHideMyIp(false));
                changed?.Invoke(settings.Current.HideMyIp);
            }));
        }
    }
}
