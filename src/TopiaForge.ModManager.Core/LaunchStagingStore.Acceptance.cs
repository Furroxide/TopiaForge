using System;
using System.IO;
using System.Linq;
using System.Text;

namespace TopiaForge.ModManager.Core
{
    public sealed partial class LaunchStagingStore
    {
        internal string AcceptanceRequestPath(string requestId) => Child("acceptance-isolation-request-" + LaunchStorageKeys.Request(requestId) + ".json");
        internal string AcceptanceAckPath(string requestId) => Child("acceptance-isolation-ack-" + LaunchStorageKeys.Request(requestId) + ".json");
        internal ProfileLaunchConfigurationV4 AdmitAcceptance(string requestId, string? configuredProfilePath,
            Func<string> readObservation, DateTimeOffset now)
        {
            // Constructing the store and all reads below leave the existing layout untouched.
            var expectedProfilePath = RequestPath(requestId);
            if (configuredProfilePath == null || !AcceptanceIsolationJson.SamePath(configuredProfilePath, expectedProfilePath))
                throw new InvalidDataException("Acceptance requires its exact one-shot profile path.");
            AcceptanceIsolationJson.LocalPath(paths.Staging);
            var requestPath = AcceptanceRequestPath(requestId);
            var ackPath = AcceptanceAckPath(requestId);
            if (File.Exists(ackPath) || Directory.Exists(ackPath)) throw new InvalidDataException("Acceptance acknowledgement already exists.");
            var requestText = Read(requestPath, AcceptanceIsolationJson.MaximumBytes);
            var request = new AcceptanceIsolationJson(requestText, AcceptanceIsolationGate.RequestFields);
            // Malformed correlation never authorizes an acknowledgement destination.
            if (request.Integer("schemaVersion") != 1 || request.Text("requestId", 128) != requestId)
                throw new InvalidDataException("Acceptance request correlation is invalid.");
            var challenge = request.Digest("challenge");
            var profileText = Read(expectedProfilePath, LaunchTransportJson.MaxDocumentBytes);
            var observationText = readObservation();
            var observation = new AcceptanceIsolationJson(observationText, "process", "observedOsIdentity", "observedRoots");
            var roots = new AcceptanceIsolationJson(observation.Values["observedRoots"], AcceptanceIsolationGate.RootFields);
            if (!AcceptanceIsolationJson.SamePath(roots.Text("managerRoot"), paths.Root))
                throw new InvalidDataException("Measured manager root does not match staging.");
            var reasons = AcceptanceIsolationGate.Validate(requestText, profileText, requestId, observationText, now);
            if (requestText != Read(requestPath, AcceptanceIsolationJson.MaximumBytes))
                throw new InvalidDataException("Acceptance sidecar changed during admission.");
            ProfileLaunchConfigurationV4? profile = null;
            if (reasons.Count == 0) profile = ConsumeRequest(expectedProfilePath, request.Digest("profileRequestSha256"));
            var ack = AcceptanceIsolationJson.Object(("schemaVersion", "1"), ("requestId", JsonUtil.Serialize(requestId)),
                ("challenge", JsonUtil.Serialize(challenge)), ("requestSha256", JsonUtil.Serialize(AcceptanceIsolationJson.Hash(requestText))),
                ("status", JsonUtil.Serialize(profile == null ? "rejected" : "admitted")),
                ("process", observation.Values["process"]), ("observedOsIdentity", observation.Values["observedOsIdentity"]),
                ("observedRoots", observation.Values["observedRoots"]), ("reasons", "[" + string.Join(",", reasons.Select(JsonUtil.Serialize)) + "]"));
            WriteAcceptanceOnce(ackPath, ack);
            return profile ?? throw new InvalidDataException("Acceptance isolation was rejected: " + string.Join(",", reasons));
        }
        private void WriteAcceptanceOnce(string path, string json)
        {
            // The staging directory must already exist; denial never provisions manager storage.
            EnsureStaging(create: false); AcceptanceIsolationJson.LocalPath(paths.Staging);
            if (!Directory.Exists(paths.Staging) || File.Exists(path) || Directory.Exists(path)) throw new InvalidDataException("Acceptance destination is not fresh guarded staging.");
            var bytes = new UTF8Encoding(false, true).GetBytes(json);
            if (bytes.Length > AcceptanceIsolationJson.MaximumBytes) throw new InvalidDataException("Acceptance acknowledgement exceeds its limit.");
            var temporary = Child(Path.GetFileName(path) + ".tmp-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
                { stream.Write(bytes, 0, bytes.Length); stream.Flush(true); }
                beforeCommit?.Invoke(); EnsureStaging(create: false); AcceptanceIsolationJson.LocalPath(paths.Staging); EnsureRegularFile(temporary);
                File.Move(temporary, path); // No overwrite: a replay cannot replace a prior admission.
            }
            finally
            {
                EnsureStaging(create: false); AcceptanceIsolationJson.LocalPath(paths.Staging);
                if (File.Exists(temporary)) { EnsureRegularFile(temporary); File.Delete(temporary); }
            }
        }
    }
}
