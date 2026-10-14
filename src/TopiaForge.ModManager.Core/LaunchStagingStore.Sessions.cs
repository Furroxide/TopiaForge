using System;
using System.IO;

namespace TopiaForge.ModManager.Core
{
    /// <summary>
    /// Request V5 and the session channels under manager staging, with the same guards as every launch record: immediate
    /// children of staging only, regular files only, bounded strict UTF-8, atomic replacement.
    /// </summary>
    public sealed partial class LaunchStagingStore
    {
        public const string PreferencesFileName = "multiplayer-preferences-v1.json";
        public const string PreferencesJournalFileName = "multiplayer-preferences-journal-v1.json";

        public string SessionHandoffPath(string requestId) => Child("session-handoff-" + LaunchStorageKeys.Request(requestId) + ".json");
        public string SessionJoinPath(string requestId) => Child("session-join-" + LaunchStorageKeys.Request(requestId) + ".json");
        public string PreferencesPath => Child(PreferencesFileName);
        public string PreferencesJournalPath => Child(PreferencesJournalFileName);

        /// <summary>
        /// Consumes the one-shot request at <paramref name="path"/> whatever its version: V4 as <see cref="ConsumeRequest(string)"/>
        /// does, or V5 with the session to host or join. Either way the file is removed once read, so a direct target's
        /// address leaves staging with it.
        /// </summary>
        public ConsumedLaunchRequest ConsumeLaunchRequest(string path)
        {
            var fullPath = ValidateChild(path);
            var json = Read(fullPath, LaunchTransportJson.MaxDocumentBytes);
            var result = LaunchTransportJson.ReadLaunchRequest(json);
            if (!string.Equals(fullPath, RequestPath(result.Profile.RequestId), FileSystemComparison))
                throw new InvalidDataException("The launch request identity does not match its owned filename.");
            if (!string.Equals(json, Read(fullPath, LaunchTransportJson.MaxDocumentBytes), StringComparison.Ordinal))
                throw new InvalidDataException("The launch request changed before consumption.");
            EnsureStaging(create: false);
            EnsureRegularFile(fullPath);
            File.Delete(fullPath);
            return result;
        }

        /// <summary>Hands the launcher that owns this process a join it cannot make as it is.</summary>
        public void WriteSessionHandoff(SessionHandoff handoff) =>
            WriteAtomic(SessionHandoffPath(handoff.RequestId), LaunchTransportJson.WriteSessionHandoff(handoff));

        /// <summary>
        /// Takes the live join the launcher staged for the process launched as <paramref name="requestId"/>, once. A foreign
        /// or malformed join is never acted on and stays inspectable.
        /// </summary>
        public SessionJoinRequest? TakeSessionJoin(string requestId)
        {
            var path = SessionJoinPath(requestId);
            try
            {
                if (!File.Exists(path)) return null;
                var json = Read(path, LaunchTransportJson.MaxChannelBytes);
                var join = LaunchTransportJson.ReadSessionJoin(json);
                if (join.RequestId != requestId || !string.Equals(json, Read(path, LaunchTransportJson.MaxChannelBytes), StringComparison.Ordinal))
                    return null;
                EnsureStaging(create: false);
                EnsureRegularFile(path);
                File.Delete(path);
                return join;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
                || error is ArgumentException)
            {
                return null;
            }
        }

        /// <summary>The launcher's preferences projection, or null when there is none or it cannot be read.</summary>
        public MultiplayerPreferencesProjection? ReadPreferences() => TryReadChannel(PreferencesPath, LaunchTransportJson.ReadPreferences);

        /// <summary>The changes this game recorded and the launcher has not dropped, or null when there are none.</summary>
        public MultiplayerPreferencesJournal? ReadPreferencesJournal() =>
            TryReadChannel(PreferencesJournalPath, LaunchTransportJson.ReadPreferencesJournal);

        /// <summary>Rewrites the journal of preferences changed in game, for the launcher to apply.</summary>
        public void WritePreferencesJournal(MultiplayerPreferencesJournal journal) =>
            WriteAtomic(PreferencesJournalPath, LaunchTransportJson.WritePreferencesJournal(journal));

        private T? TryReadChannel<T>(string path, Func<string, T> parse) where T : class
        {
            try
            {
                return File.Exists(path) ? parse(Read(path, LaunchTransportJson.MaxChannelBytes)) : null;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException)
            {
                return null;
            }
        }
    }
}
