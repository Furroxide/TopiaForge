using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

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

        /// <summary>How often a refused channel write is tried again.</summary>
        internal const int ChannelWriteRetries = 5;

        // The journal's writers in this process, such as the display name's and voice chat's, take turns through it.
        private static readonly object JournalGate = new object();

        /// <summary>How old a claim must be before a sweep removes it. A live take holds its claim for milliseconds.</summary>
        internal static readonly TimeSpan StaleClaimAge = TimeSpan.FromHours(1);

        // This game's own claims: a join or a quit request claimed under its claim time (Unix milliseconds) and a random nonce.
        private static readonly Regex JoinClaim = new Regex(
            "^(?:session-join|launcher-quit)-[0-9a-f]{64}\\.json\\.taking-([0-9]{13})-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

        public string SessionHandoffPath(string requestId) => Child("session-handoff-" + LaunchStorageKeys.Request(requestId) + ".json");
        public string SessionJoinPath(string requestId) => Child("session-join-" + LaunchStorageKeys.Request(requestId) + ".json");

        /// <summary>The launcher's quit request for the process launched as <paramref name="requestId"/>.</summary>
        public string LauncherQuitPath(string requestId) => Child("launcher-quit-" + LaunchStorageKeys.Request(requestId) + ".json");
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
            WriteChannel(SessionHandoffPath(handoff.RequestId), LaunchTransportJson.WriteSessionHandoff(handoff));

        /// <summary>
        /// Takes the live join the launcher staged for the process launched as <paramref name="requestId"/>, once. The file is
        /// claimed under a private name before it is read, so a newer join the launcher writes meanwhile lands at the channel's
        /// name again and is never removed unread. A foreign or malformed join is removed without being acted on. Claims a
        /// crashed take left behind are swept first (<see cref="SweepStaleClaims()"/>).
        /// </summary>
        public SessionJoinRequest? TakeSessionJoin(string requestId) =>
            TakeChannel(SessionJoinPath(requestId), LaunchTransportJson.ReadSessionJoin, join => join.RequestId == requestId);

        /// <summary>
        /// Takes the quit request the launcher staged for the process launched as <paramref name="requestId"/>, once, as
        /// <see cref="TakeSessionJoin"/> takes a join. A foreign or malformed request is removed without being acted on, so only
        /// the process the launcher started, which alone knows its request id, ever quits on one.
        /// </summary>
        public LauncherQuitRequest? TakeLauncherQuit(string requestId) =>
            TakeChannel(LauncherQuitPath(requestId), LaunchTransportJson.ReadLauncherQuit, quit => quit.RequestId == requestId);

        private T? TakeChannel<T>(string path, Func<string, T> parse, Func<T, bool> forThisProcess) where T : class
        {
            string claimed;
            try
            {
                EnsureStaging(create: false);
                if (!File.Exists(path)) return null;
                EnsureRegularFile(path);
                var now = DateTimeOffset.UtcNow;
                SweepStaleClaims(now);
                claimed = Child(Path.GetFileName(path) + ".taking-" + now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)
                    + "-" + Guid.NewGuid().ToString("N"));
                File.Move(path, claimed);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException)
            {
                return null;
            }

            try
            {
                afterClaim?.Invoke();
                var value = parse(Read(claimed, LaunchTransportJson.MaxChannelBytes));
                return forThisProcess(value) ? value : null;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException
                || error is ArgumentException)
            {
                return null;
            }
            finally
            {
                RemoveClaim(claimed);
            }
        }

        /// <summary>The launcher's preferences projection, or null when there is none or it cannot be read.</summary>
        public MultiplayerPreferencesProjection? ReadPreferences() => TryReadChannel(PreferencesPath, LaunchTransportJson.ReadPreferences);

        /// <summary>The changes this game recorded and the launcher has not dropped, or null when there are none.</summary>
        public MultiplayerPreferencesJournal? ReadPreferencesJournal() =>
            TryReadChannel(PreferencesJournalPath, LaunchTransportJson.ReadPreferencesJournal);

        /// <summary>Rewrites the journal of preferences changed in game, for the launcher to apply.</summary>
        public void WritePreferencesJournal(MultiplayerPreferencesJournal journal) =>
            WriteChannel(PreferencesJournalPath, LaunchTransportJson.WritePreferencesJournal(journal));

        /// <summary>
        /// Records one preference the player changed in game, for the launcher to take in, and returns the journal written.
        /// <paramref name="change"/> receives the change's sequence. The journal on disk keeps its epoch; a new journal, under a
        /// fresh epoch (<see cref="MultiplayerPreferencesJournal.Start"/>), starts only when there is no journal file or the one
        /// there is not a journal the launcher could ever apply. A journal file that cannot be opened is retried briefly and
        /// then refused, so its changes are never dropped for a new journal. Calls from several threads of this process take
        /// turns, so two changes recorded at once never read the same journal and write over each other. Do not call it from
        /// the main thread: a refused read or write sleeps between tries, for about 310 ms in all.
        /// </summary>
        public MultiplayerPreferencesJournal RecordPreferenceChange(Func<int, MultiplayerPreferenceChange> change)
        {
            if (change == null) throw new ArgumentNullException(nameof(change));
            lock (JournalGate)
            {
                var projection = ReadPreferences();
                var journal = ReadJournalToAppend() ?? MultiplayerPreferencesJournal.Start();
                var next = journal.Append(change(journal.NextSequence(projection)), projection);
                WritePreferencesJournal(next);
                return next;
            }
        }

        /// <summary>
        /// Removes this game's join and quit-request claims that a crashed take left behind, and returns how many. Only its own
        /// claim patterns (<c>session-join-&lt;key&gt;.json.taking-&lt;milliseconds&gt;-&lt;nonce&gt;</c>, and the same for
        /// <c>launcher-quit-</c>) are touched; a rename keeps a file's
        /// timestamps, so a claim is dated by the claim time in its name, and only one more than an hour old is stale. A claim
        /// another process holds open is left: the sweep removes a file only through a handle that no other one shares. It
        /// never throws.
        /// </summary>
        public int SweepStaleClaims() => SweepStaleClaims(DateTimeOffset.UtcNow);

        internal int SweepStaleClaims(DateTimeOffset now)
        {
            string[] files;
            try
            {
                EnsureStaging(create: false);
                files = Directory.GetFiles(paths.Staging);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException)
            {
                return 0;
            }

            var removed = 0;
            foreach (var file in files.Where(file => IsStaleClaim(Path.GetFileName(file), now)))
            {
                try
                {
                    EnsureRegularFile(file);
                    // Opening shares nothing but deletion, so it fails while any other process reads or writes the claim.
                    using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Delete)) File.Delete(file);
                    removed++;
                }
                catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException)
                {
                    // In use, linked or already gone: a later sweep looks again.
                }
            }

            return removed;
        }

        private static bool IsStaleClaim(string name, DateTimeOffset now)
        {
            var match = JoinClaim.Match(name);
            return match.Success && long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var claimedAt)
                && now.ToUnixTimeMilliseconds() - claimedAt > (long)StaleClaimAge.TotalMilliseconds;
        }

        /// <summary>The journal to append to: null when there is none, or none the launcher could apply; refused while locked.</summary>
        private MultiplayerPreferencesJournal? ReadJournalToAppend()
        {
            var path = PreferencesJournalPath;
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    EnsureStaging(create: false);
                    return File.Exists(path) ? LaunchTransportJson.ReadPreferencesJournal(Read(path, LaunchTransportJson.MaxChannelBytes)) : null;
                }
                catch (InvalidDataException)
                {
                    return null;
                }
                catch (Exception error) when ((error is IOException || error is UnauthorizedAccessException) && attempt < ChannelWriteRetries)
                {
                    if (beforeRetry != null) beforeRetry(attempt);
                    else Thread.Sleep(10 << attempt);
                }
            }
        }

        /// <summary>
        /// Writes a channel the launcher reads, retrying briefly: Windows refuses to replace a file while the launcher reads it.
        /// </summary>
        private void WriteChannel(string path, string json)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    WriteAtomic(path, json);
                    return;
                }
                catch (Exception error) when ((error is IOException || error is UnauthorizedAccessException) && attempt < ChannelWriteRetries)
                {
                    if (beforeRetry != null) beforeRetry(attempt);
                    else Thread.Sleep(10 << attempt);
                }
            }
        }

        private void RemoveClaim(string claimed)
        {
            try
            {
                EnsureStaging(create: false);
                if (!File.Exists(claimed)) return;
                EnsureRegularFile(claimed);
                File.Delete(claimed);
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is UnauthorizedAccessException)
            {
                // A claim that cannot be removed holds no address, and the next launch of this request never reads it.
            }
        }

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
