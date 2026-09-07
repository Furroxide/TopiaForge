using System;
using System.IO;
using System.Text;

namespace TopiaForge.ModManager.Core
{
    /// <summary>Bounded, request-owned transport files beneath a verified installation directory.</summary>
    public sealed class LaunchStagingStore
    {
        private static readonly StringComparison FileSystemComparison = Environment.OSVersion.Platform == PlatformID.Win32NT ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly ManagerPaths paths;
        private readonly string installRoot;
        private readonly Action? beforeCommit;
        public LaunchStagingStore(ManagerPaths paths) : this(paths, null) { }
        internal LaunchStagingStore(ManagerPaths paths, Action? beforeCommit)
        {
            this.beforeCommit = beforeCommit;
            this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
            installRoot = Path.GetDirectoryName(paths.BepInExRoot) ?? throw new InvalidDataException("The manager has no installation root.");
            EnsureStaging(create: false);
        }
        public string RequestPath(string requestId) => Child("launch-profile-" + LaunchStorageKeys.Request(requestId) + ".json");
        public string ProgressPath(string requestId) => Child("launch-progress-" + LaunchStorageKeys.Request(requestId) + ".json");
        public string OutcomePath(string requestId, bool terminalSession = false) => Child((terminalSession ? "session-outcome-" : "launch-outcome-") + LaunchStorageKeys.Request(requestId) + ".json");
        public string ObservationPath(RuntimeObservationEnvelope observation) => Child("runtime-observation-" + LaunchStorageKeys.Observation(observation) + ".json");

        public ProfileLaunchConfigurationV4 ConsumeRequest(string path)
        {
            var fullPath = ValidateChild(path);
            var json = Read(fullPath, LaunchTransportJson.MaxDocumentBytes);
            var result = LaunchTransportJson.ReadProfile(json);
            if (!string.Equals(fullPath, RequestPath(result.RequestId), FileSystemComparison))
                throw new InvalidDataException("The launch request identity does not match its owned filename.");
            // Do not delete a different payload installed after the first read.
            if (!string.Equals(json, Read(fullPath, LaunchTransportJson.MaxDocumentBytes), StringComparison.Ordinal))
                throw new InvalidDataException("The launch request changed before consumption.");
            EnsureStaging(create: false);
            EnsureRegularFile(fullPath);
            File.Delete(fullPath);
            return result;
        }
        public LaunchRequestCorrelation? TryReadCorrelation(string path)
        {
            try
            {
                var fullPath = ValidateChild(path);
                var correlation = LaunchTransportJson.ReadCorrelation(Read(fullPath, LaunchTransportJson.MaxDocumentBytes));
                return string.Equals(fullPath, RequestPath(correlation.RequestId), FileSystemComparison) ? correlation : null;
            }
            catch (Exception error) when (error is IOException || error is InvalidDataException || error is System.ArgumentException
                || error is System.Runtime.Serialization.SerializationException || error is System.Xml.XmlException
                || error is System.FormatException || error is System.UnauthorizedAccessException)
            { return null; }
        }
        public void WriteProgress(LaunchProgress progress) => WriteAtomic(ProgressPath(progress.RequestId), LaunchTransportJson.WriteProgress(progress));
        public void WriteOutcome(LaunchOutcome outcome) => WriteAtomic(OutcomePath(outcome.RequestId, outcome.Kind == "session"), LaunchTransportJson.WriteOutcome(outcome));
        public void WriteObservation(RuntimeObservationEnvelope observation) => WriteAtomic(ObservationPath(observation), LaunchTransportJson.WriteObservation(observation));

        private string Read(string path, int maximumBytes)
        {
            EnsureStaging(create: false);
            EnsureRegularFile(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > maximumBytes) throw new InvalidDataException("Launch transport exceeds its byte limit.");
            var bytes = new byte[checked((int)stream.Length)];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var count = stream.Read(bytes, offset, bytes.Length - offset);
                if (count == 0) throw new EndOfStreamException("Launch transport was truncated.");
                offset += count;
            }
            if (stream.ReadByte() != -1) throw new InvalidDataException("Launch transport grew during its read.");
            try { return StrictUtf8.GetString(bytes); }
            catch (DecoderFallbackException error) { throw new InvalidDataException("Launch transport must be strict UTF-8.", error); }
        }
        private void WriteAtomic(string path, string json)
        {
            EnsureStaging(create: true);
            path = ValidateChild(path);
            if (File.Exists(path)) EnsureRegularFile(path);
            var temporary = Child(Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                var bytes = StrictUtf8.GetBytes(json);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                beforeCommit?.Invoke();
                EnsureStaging(create: false);
                EnsureRegularFile(temporary);
                if (File.Exists(path))
                { EnsureRegularFile(path); File.Replace(temporary, path, null); }
                else File.Move(temporary, path);
            }
            finally
            {
                EnsureStaging(create: false);
                if (File.Exists(temporary)) { EnsureRegularFile(temporary); File.Delete(temporary); }
            }
        }
        private string Child(string filename) => ValidateChild(Path.Combine(paths.Staging, filename));
        private string ValidateChild(string path)
        {
            var fullPath = Path.GetFullPath(path);
            var staging = Path.GetFullPath(paths.Staging);
            if (!string.Equals(Path.GetDirectoryName(fullPath), staging, FileSystemComparison))
                throw new InvalidDataException("Launch transport must be an immediate child of manager staging.");
            return fullPath;
        }
        private void EnsureStaging(bool create)
        {
            var root = Path.GetFullPath(installRoot);
            var staging = Path.GetFullPath(paths.Staging);
            if (!staging.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar,
                FileSystemComparison)) throw new InvalidDataException("Manager staging escapes its installation root.");
            var current = root;
            EnsureDirectory(current, create: false);
            foreach (var component in Path.GetRelativePath(root, staging).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                current = Path.Combine(current, component);
                if (!Directory.Exists(current) && !File.Exists(current) && !create) return;
                EnsureDirectory(current, create);
            }
        }
        private static void EnsureDirectory(string path, bool create)
        {
            if (!Directory.Exists(path) && !File.Exists(path) && create) Directory.CreateDirectory(path);
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("Launch transport rejects linked or non-directory ancestors: " + path);
        }
        private static void EnsureRegularFile(string path)
        {
            if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Device | FileAttributes.Directory)) != 0)
                throw new InvalidDataException("Launch transport must be a regular file: " + path);
        }
    }
}
