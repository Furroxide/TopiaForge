using System;
using System.Collections.Generic;
using TopiaForge.ModManager.Core;
using TopiaForge.Mods;
using TopiaForge.Mods.Internal;

namespace TopiaForge.ModManager
{
    /// <summary>Where the multiplayer runtime writes: the manager log in the game, a recorder in tests.</summary>
    internal interface IMultiplayerLog
    {
        void Info(string message);

        void Warn(string message);

        void Error(string message);
    }

    /// <summary>The process controls multiplayer needs from the game engine. CoreGameplayServices implements it over Unity.</summary>
    internal interface IMultiplayerPlatform
    {
        /// <summary>Gets or sets whether the game keeps running while its window is in the background.</summary>
        bool RunInBackground { get; set; }
    }

    /// <summary>A platform without a game engine behind it: the setting is only remembered.</summary>
    internal sealed class DetachedMultiplayerPlatform : IMultiplayerPlatform
    {
        public bool RunInBackground { get; set; } = true;
    }

    /// <summary>One redacted diagnostic, kept for the diagnostics bundle.</summary>
    internal sealed class MultiplayerDiagnosticRecord
    {
        internal MultiplayerDiagnosticRecord(InternalDiagnosticSeverity severity, string code, string text)
        {
            Severity = severity;
            Code = code;
            Text = text;
        }

        internal InternalDiagnosticSeverity Severity { get; }

        internal string Code { get; }

        /// <summary>Gets the redacted text, as it was logged.</summary>
        internal string Text { get; }
    }

    /// <summary>
    /// The runtime's side of the internal multiplayer SPI: this player's identity in sessions, the packages that loaded,
    /// admission through the Core planner, the background setting and the redacted diagnostics. ModManager's
    /// <see cref="MultiplayerRuntime"/> hands it to the provider's control. Main thread only.
    /// </summary>
    internal sealed class MultiplayerRuntimeHost : IInternalMultiplayerRuntimeHost
    {
        internal const string DefaultDisplayName = "Player";
        internal const int RecentDiagnosticCapacity = 64;

        private readonly MultiplayerContentInventory inventory;
        private readonly Queue<MultiplayerDiagnosticRecord> recent = new Queue<MultiplayerDiagnosticRecord>();
        private readonly IMultiplayerLog log;
        private readonly IMultiplayerPlatform platform;
        private string displayName = DefaultDisplayName;
        private bool? savedRunInBackground;

        /// <param name="gameBuild">The Robotopia build, such as <c>2545</c>; anything else is described as unknown.</param>
        /// <param name="runtimeVersion">The TopiaForge runtime's semantic version.</param>
        /// <param name="log">Where diagnostics go once redacted.</param>
        /// <param name="platform">The game engine's process controls; null remembers the setting only.</param>
        /// <param name="contentScheduler">Where packages are identified; null uses a background thread, never the caller's.</param>
        internal MultiplayerRuntimeHost(
            string gameBuild,
            string runtimeVersion,
            IMultiplayerLog log,
            IMultiplayerPlatform? platform = null,
            IMultiplayerWorkScheduler? contentScheduler = null)
        {
            GameBuild = IsGameBuild(gameBuild) ? gameBuild : "unknown";
            RuntimeVersion = string.IsNullOrWhiteSpace(runtimeVersion) ? TopiaForgeVersions.LoaderVersion : runtimeVersion;
            this.log = log ?? throw new ArgumentNullException(nameof(log));
            this.platform = platform ?? new DetachedMultiplayerPlatform();
            inventory = new MultiplayerContentInventory(contentScheduler ?? MultiplayerWorkSchedulers.Background);
        }

        public string GameBuild { get; }

        public string RuntimeVersion { get; }

        public string DisplayName => displayName;

        /// <summary>
        /// Gets or sets the TopiaForge service's origin: the session launch's own when it names one, otherwise the
        /// manager's configuration, the launcher's multiplayer preferences. Null when neither names one; the provider then
        /// contacts no service.
        /// </summary>
        public string? ServiceOrigin { get; internal set; }

        /// <summary>
        /// Sets <see cref="ServiceOrigin"/> at startup from the session launch's origin (launch request V5) and the
        /// manager's configured one: the launch's comes first. The launch ticket itself stays the provider's to take.
        /// </summary>
        internal void ConfigureServiceOrigin(string? launch, string? configured) => ServiceOrigin = launch ?? configured;

        /// <summary>Gets or sets the name this player hosts and joins as; the in-game settings set it.</summary>
        internal string PreferredDisplayName
        {
            get => displayName;
            set
            {
                if (!InternalText.IsDisplayText(value, InternalJoinSettings.MaximumDisplayNameLength))
                {
                    throw new ArgumentException("A display name is 1 to 24 printable characters.", nameof(value));
                }

                displayName = value;
            }
        }

        /// <summary>Gets the packages that loaded, which ModRuntime records as each one comes up.</summary>
        internal MultiplayerContentInventory Inventory => inventory;

        /// <summary>Gets the most recent redacted diagnostics, oldest first, at most 64.</summary>
        internal IReadOnlyList<MultiplayerDiagnosticRecord> RecentDiagnostics => recent.ToArray();

        /// <summary>Gets whether multiplayer currently keeps the game running in the background.</summary>
        internal bool HoldsRunInBackground => savedRunInBackground.HasValue;

        public IReadOnlyList<InternalContentDescriptor> LocalContent => inventory.Descriptors;

        public InternalContentReadiness ContentReadiness => inventory.Readiness;

        public InternalContentVerdict EvaluateAdmission(InternalContentOffer offer) =>
            MultiplayerContentAdmission.Evaluate(GameBuild, inventory.AdmissionMods, offer);

        public InternalContentVerdict CheckHostContent(IReadOnlyList<InternalHostPackage> hostPackages) =>
            MultiplayerContentAdmission.CheckHost(inventory.Descriptors, hostPackages);

        /// <summary>
        /// Turns running in the background on while online. Turning it off restores whatever the game had before, which for
        /// Robotopia is already on, so single player behaves exactly as it did before multiplayer existed.
        /// </summary>
        public void SetRunInBackground(bool enabled)
        {
            if (enabled)
            {
                if (!savedRunInBackground.HasValue) savedRunInBackground = platform.RunInBackground;
                platform.RunInBackground = true;
                return;
            }

            if (!savedRunInBackground.HasValue) return;
            platform.RunInBackground = savedRunInBackground.Value;
            savedRunInBackground = null;
        }

        public void ReportDiagnostic(InternalMultiplayerDiagnostic diagnostic)
        {
            if (diagnostic == null) throw new ArgumentNullException(nameof(diagnostic));
            var text = MultiplayerDiagnosticRedactor.Describe(diagnostic, out var code);
            recent.Enqueue(new MultiplayerDiagnosticRecord(diagnostic.Severity, code, text));
            while (recent.Count > RecentDiagnosticCapacity) recent.Dequeue();
            switch (diagnostic.Severity)
            {
                case InternalDiagnosticSeverity.Info:
                    log.Info(text);
                    break;
                case InternalDiagnosticSeverity.Warning:
                    log.Warn(text);
                    break;
                default:
                    log.Error(text);
                    break;
            }
        }

        /// <summary>Derives the build a descriptor names from the game's version: 0.0.2545 is build 2545.</summary>
        internal static string GameBuildOf(IRuntimeInfo runtimeInfo)
        {
            if (runtimeInfo == null) throw new ArgumentNullException(nameof(runtimeInfo));
            return runtimeInfo.TryGetGameVersion(out var version)
                ? version.Patch.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : "unknown";
        }

        private static bool IsGameBuild(string? value)
        {
            if (string.IsNullOrEmpty(value) || value!.Length > 16) return false;
            foreach (var character in value)
            {
                var allowed = (character >= '0' && character <= '9') || (character >= 'a' && character <= 'z') ||
                              (character >= 'A' && character <= 'Z') || character == '.' || character == '_' || character == '-';
                if (!allowed) return false;
            }

            return true;
        }
    }
}
