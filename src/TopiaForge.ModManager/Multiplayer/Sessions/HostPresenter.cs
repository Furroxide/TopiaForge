using System;
using System.Collections.Generic;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;
using TopiaForge.Mods.UnityUi;

namespace TopiaForge.ModManager.Sessions
{
    /// <summary>
    /// HOST: the session's name, size, password and visibility, the host policy the session engine supports today, and
    /// starting or ending the session. A running solo game is opened to other players in place; at the main menu the chosen
    /// game starts first and opens once it runs. Before anything starts, the disclosure shows if it is due, and a Public or
    /// Invite session, which in this build can only be reached directly, asks the per-session prompt while Hide my IP is on;
    /// saying no starts nothing. The password is used once and never kept.
    /// </summary>
    internal sealed partial class HostPresenter : IHostScreen
    {
        internal const string NameRule = "A server name is 1 to 48 printable characters.";
        internal const string EndTitle = "End session for everyone?";
        internal const string EndBody = "Every player leaves the session. You keep playing on your own.";
        internal const string EndConfirm = "END SESSION";

        private static readonly string[] VisibilityLabels = { "Public", "Invite", "LAN" };

        private static readonly InternalSessionVisibility[] VisibilityValues =
        {
            InternalSessionVisibility.Public, InternalSessionVisibility.Invite, InternalSessionVisibility.Lan
        };

        private static readonly HostPolicySeam[] Seams =
        {
            new HostPolicySeam("Guests can build", "Not available in this build."),
            new HostPolicySeam("Prop protection", "Not available in this build."),
            new HostPolicySeam("Per-player quotas", "Not available in this build.")
        };

        private readonly MultiplayerScreensContext context;
        private IReadOnlyList<HostTargetChoice> targets = Array.Empty<HostTargetChoice>();
        private IReadOnlyList<string> targetOptions = Array.Empty<string>();
        private HostTargetChoice? launching;
        private Stage stage;
        private bool defaultsApplied;
        private bool allowDirect;
        private string name = string.Empty;
        private string nameError = string.Empty;
        private string password = string.Empty;
        private string passwordError = string.Empty;
        private string progressText = string.Empty;
        private ScreenTone progressTone;
        private int maxPlayers = MultiplayerSettings.DefaultMaxPlayers;
        private int visibility = 2;
        private int selectedTarget;
        private bool voice;

        internal HostPresenter(MultiplayerScreensContext context)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
        }

        private enum Stage
        {
            Idle,
            Launching,
            WaitingForGame,
            Opening,
            Hosting
        }

        public IContentReadinessScreen Content => context.Content;

        public string ModeText
        {
            get
            {
                switch (Decision())
                {
                    case RealmDecision.PromoteInPlace:
                        return "Open this game to other players. It keeps running as it is.";
                    case RealmDecision.StartAsHost:
                        return "Choose a game to start and open to other players.";
                    case RealmDecision.AlreadyThere:
                        return "You are hosting this game.";
                    case RealmDecision.Busy:
                        return "Wait for the current game to finish starting or stopping.";
                    case RealmDecision.StandaloneRefused:
                        return "A mod that refuses multiplayer is enabled; disable it to host.";
                    default:
                        return "Leave the joined session before you host.";
                }
            }
        }

        public bool ChoosesTarget => Decision() == RealmDecision.StartAsHost;

        public IReadOnlyList<string> TargetOptions => targetOptions;

        public int SelectedTarget => selectedTarget;

        public string CurrentGameText => Decision() == RealmDecision.PromoteInPlace || stage == Stage.Hosting ? context.Environment.CurrentGameText : string.Empty;

        public string Name => name;

        public string NameError => nameError;

        public int MaxPlayers => maxPlayers;

        public int MinimumPlayers => InternalHostSettings.MinimumPlayers;

        public int MaximumPlayers => InternalHostSettings.MaximumPlayers;

        public string Password => password;

        public string PasswordError => passwordError;

        public IReadOnlyList<string> VisibilityOptions => VisibilityLabels;

        public int Visibility => visibility;

        public string VisibilityNote => VisibilityValues[visibility] == InternalSessionVisibility.Lan
            ? "Players on your local network can find this session and join it."
            : "Public listings and invites need the TopiaForge service, which is not configured in this build. You are asked before anyone can connect directly.";

        public bool Voice => voice;

        public IReadOnlyList<HostPolicySeam> PendingPolicies => Seams;

        public bool FormEnabled => stage == Stage.Idle && context.Status.Role == InternalSessionRole.Solo;

        public bool CanHost
        {
            get
            {
                if (!context.CanStartSession || !FormEnabled || nameError.Length != 0 || passwordError.Length != 0) return false;
                var decision = Decision();
                return decision == RealmDecision.PromoteInPlace || (decision == RealmDecision.StartAsHost && selectedTarget < targets.Count);
            }
        }

        public string HostLabel => Decision() == RealmDecision.PromoteInPlace ? "OPEN TO PLAYERS" : "START AND HOST";

        public bool CanEnd => context.Status.Role == InternalSessionRole.ListenHost;

        public string ProgressText => progressText;

        public ScreenTone ProgressTone => progressTone;

        public void SelectTarget(int index)
        {
            if (index >= 0 && index < targets.Count) selectedTarget = index;
        }

        public void SetName(string value)
        {
            name = value ?? string.Empty;
            nameError = InternalText.IsDisplayText(name, InternalHostSettings.MaximumNameLength) ? string.Empty : NameRule;
        }

        /// <summary>Takes a player count, held to the 2 to 16 a session allows.</summary>
        public void SetMaxPlayers(int value) =>
            maxPlayers = Math.Min(Math.Max(value, InternalHostSettings.MinimumPlayers), InternalHostSettings.MaximumPlayers);

        public void SetPassword(string value)
        {
            password = value ?? string.Empty;
            passwordError = password.Length == 0 || IsPassword(password) ? string.Empty : ConnectingPresenter.PasswordRule;
        }

        public void SetVisibility(int index)
        {
            if (index >= 0 && index < VisibilityValues.Length) visibility = index;
        }

        public void SetVoice(bool value) => voice = value;

        /// <summary>Gets whether a password can be used: 1 to 64 characters with no control characters.</summary>
        internal static bool IsPassword(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            foreach (var character in value)
            {
                if (char.IsControl(character)) return false;
            }

            return true;
        }

        /// <summary>Reads the games the player may start again, as when the tab shows.</summary>
        internal void RefreshTargets()
        {
            try
            {
                targets = context.Environment.ReadHostTargets();
            }
            catch (Exception)
            {
                targets = Array.Empty<HostTargetChoice>();
            }

            var labels = new string[targets.Count];
            for (var index = 0; index < labels.Length; index++) labels[index] = targets[index].Label;
            targetOptions = labels;
            if (selectedTarget >= targets.Count) selectedTarget = 0;
        }

        /// <summary>Takes the remembered hosting settings once the multiplayer settings load.</summary>
        internal void ApplyDefaults(MultiplayerSettings settings)
        {
            if (defaultsApplied) return;
            defaultsApplied = true;
            SetName(settings.ServerName ?? DefaultName(settings.DisplayName));
            SetMaxPlayers(settings.HostMaxPlayers);
            visibility = Array.IndexOf(VisibilityValues, settings.HostVisibility);
            if (visibility < 0) visibility = 2;
            voice = settings.HostVoice;
        }

        /// <summary>Gets a server name from the player's name, such as "Sam's session".</summary>
        internal static string DefaultName(string displayName)
        {
            var proposed = displayName + "'s session";
            return InternalText.IsDisplayText(proposed, InternalHostSettings.MaximumNameLength) ? proposed : "TopiaForge session";
        }

        private RealmDecision Decision() =>
            SessionRealmGate.ForHost(context.Status.Role, context.Environment.GamePhase, context.Environment.StandaloneModName != null);

        /// <summary>Bounds a gamemode or world name to what a session announces, or null when there is none.</summary>
        private static string? Announced(string? value)
        {
            var bounded = TopiaForgePlainText.Sanitize(value, InternalHostSettings.MaximumNameLength);
            return InternalText.IsDisplayText(bounded, InternalHostSettings.MaximumNameLength) ? bounded : null;
        }
    }
}
