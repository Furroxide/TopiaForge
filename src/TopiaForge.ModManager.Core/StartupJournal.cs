using System;
using System.IO;
using System.Runtime.Serialization;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Decision derived from the previous process's bounded startup journal.</summary>
    public sealed class StartupRecoveryDecision
    {
        public static readonly StartupRecoveryDecision None = new StartupRecoveryDecision(false, string.Empty, string.Empty);

        public StartupRecoveryDecision(bool safeMode, string quarantineModId, string reason)
        {
            SafeMode = safeMode;
            QuarantineModId = quarantineModId ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        public bool SafeMode { get; }
        public string QuarantineModId { get; }
        public string Reason { get; }
    }

    /// <summary>
    /// Persists small state transitions around mod startup so a native crash or process termination cannot
    /// trap users in an invisible load loop. It intentionally assigns blame only while one mod's load callback
    /// was active; ambiguous failures enter one-shot safe mode instead.
    /// </summary>
    public sealed class StartupJournal
    {
        private const int CurrentSchemaVersion = 1;
        private const string Starting = "starting";
        private const string Loading = "loading";
        private const string Loaded = "loaded";
        private const string StartupComplete = "startup-complete";
        private const string CleanExit = "clean-exit";

        private readonly string path;
        private readonly StartupJournalDocument document;

        private StartupJournal(string path, StartupJournalDocument document)
        {
            this.path = path;
            this.document = document;
        }

        public string SessionId => document.SessionId;

        public static StartupJournal Begin(string path, out StartupRecoveryDecision recovery)
        {
            recovery = ReadRecovery(path);
            var document = new StartupJournalDocument
            {
                SchemaVersion = CurrentSchemaVersion,
                SessionId = Guid.NewGuid().ToString("N"),
                StartedAtUtc = DateTime.UtcNow.ToString("O"),
                UpdatedAtUtc = DateTime.UtcNow.ToString("O"),
                State = Starting
            };
            var journal = new StartupJournal(path, document);
            journal.Save();
            return journal;
        }

        public void MarkLoading(string modId)
        {
            Transition(Loading, modId);
        }

        public void MarkLoaded(string modId)
        {
            Transition(Loaded, modId);
        }

        public void MarkStartupComplete()
        {
            Transition(StartupComplete, string.Empty);
        }

        public void MarkCleanExit()
        {
            Transition(CleanExit, string.Empty);
        }

        private static StartupRecoveryDecision ReadRecovery(string path)
        {
            if (!File.Exists(path))
            {
                return StartupRecoveryDecision.None;
            }

            StartupJournalDocument previous;
            try
            {
                previous = JsonUtil.LoadFile(path, new StartupJournalDocument());
            }
            catch (Exception ex)
            {
                return new StartupRecoveryDecision(
                    safeMode: true,
                    quarantineModId: string.Empty,
                    reason: "The previous startup journal was unreadable: " + ex.Message);
            }

            if (previous.SchemaVersion != CurrentSchemaVersion)
            {
                return new StartupRecoveryDecision(
                    safeMode: true,
                    quarantineModId: string.Empty,
                    reason: "The previous startup journal used an unsupported schema.");
            }

            if (string.Equals(previous.State, Loading, StringComparison.Ordinal) &&
                ManifestValidator.IsValidId(previous.CurrentModId))
            {
                return new StartupRecoveryDecision(
                    safeMode: false,
                    quarantineModId: previous.CurrentModId,
                    reason: "The previous process ended while this mod was loading.");
            }

            if (string.Equals(previous.State, Starting, StringComparison.Ordinal) ||
                string.Equals(previous.State, Loaded, StringComparison.Ordinal))
            {
                return new StartupRecoveryDecision(
                    safeMode: true,
                    quarantineModId: string.Empty,
                    reason: "The previous process ended before TopiaForge startup completed.");
            }

            // Reaching startup-complete proves that no load callback was active, but it does not prove that
            // the previous process exited cleanly. Enter one-shot safe mode without blaming a mod so a
            // gameplay crash cannot become a crash loop. Only the explicit clean-exit transition bypasses
            // recovery.
            if (string.Equals(previous.State, StartupComplete, StringComparison.Ordinal))
            {
                return new StartupRecoveryDecision(
                    safeMode: true,
                    quarantineModId: string.Empty,
                    reason: "The previous process ended without recording a clean exit after startup completed.");
            }

            if (string.Equals(previous.State, CleanExit, StringComparison.Ordinal))
            {
                return StartupRecoveryDecision.None;
            }

            return new StartupRecoveryDecision(
                safeMode: true,
                quarantineModId: string.Empty,
                reason: "The previous startup journal contained an unknown state and was treated as untrusted.");
        }

        private void Transition(string state, string currentModId)
        {
            document.State = state;
            document.CurrentModId = currentModId ?? string.Empty;
            document.UpdatedAtUtc = DateTime.UtcNow.ToString("O");
            Save();
        }

        private void Save()
        {
            JsonUtil.SaveFile(path, document);
        }
    }

    [DataContract]
    internal sealed class StartupJournalDocument
    {
        [DataMember(Name = "schemaVersion")]
        public int SchemaVersion { get; set; }

        [DataMember(Name = "sessionId")]
        public string SessionId { get; set; } = string.Empty;

        [DataMember(Name = "state")]
        public string State { get; set; } = string.Empty;

        [DataMember(Name = "currentModId")]
        public string CurrentModId { get; set; } = string.Empty;

        [DataMember(Name = "startedAtUtc")]
        public string StartedAtUtc { get; set; } = string.Empty;

        [DataMember(Name = "updatedAtUtc")]
        public string UpdatedAtUtc { get; set; } = string.Empty;
    }
}
