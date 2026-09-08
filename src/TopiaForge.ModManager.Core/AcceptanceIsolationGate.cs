using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Core
{
    internal static class AcceptanceIsolationGate
    {
        internal const string EnvironmentVariable = "TOPIAFORGE_ACCEPTANCE_REQUEST_ID";
        internal static readonly string[] RequestFields = { "schemaVersion", "requestId", "challenge", "profileRequestSha256",
            "isolationEvidenceSha256", "issuedAtUtc", "expiresAtUtc", "expectedOsIdentity", "expectedRoots" };
        internal static readonly string[] IdentityFields = { "userSid", "logonId", "sessionId", "userProfile", "localAppDataLow" };
        internal static readonly string[] RootFields = { "gameRoot", "bepInExRoot", "managerRoot", "persistentDataRoot" };
        internal static IReadOnlyList<string> Validate(string requestJson, string profileJson,
            string requestId, string observationJson, DateTimeOffset now)
        {
            var reasons = new List<string>();
            try
            {
                LaunchStorageKeys.Request(requestId);
                var request = new AcceptanceIsolationJson(requestJson, RequestFields);
                if (request.Integer("schemaVersion") != 1) reasons.Add("unsupported-schema");
                if (request.Text("requestId", 128) != requestId) reasons.Add("request-mismatch");
                request.Digest("challenge"); request.Digest("isolationEvidenceSha256");
                var profile = LaunchTransportJson.ReadProfile(profileJson);
                if (profile.RequestId != requestId) reasons.Add("profile-request-mismatch");
                if (request.Digest("profileRequestSha256") != AcceptanceIsolationJson.Hash(profileJson)) reasons.Add("profile-bytes-mismatch");
                var issued = Date(request.Text("issuedAtUtc", 64)); var expires = Date(request.Text("expiresAtUtc", 64));
                if (issued > now.AddMinutes(1) || expires <= now || expires <= issued || expires - issued > TimeSpan.FromMinutes(15))
                    reasons.Add("expired-or-invalid-challenge");
                var observed = new AcceptanceIsolationJson(observationJson, "process", "observedOsIdentity", "observedRoots");
                var identity = new AcceptanceIsolationJson(observed.Values["observedOsIdentity"], IdentityFields);
                var expectedIdentity = new AcceptanceIsolationJson(request.Values["expectedOsIdentity"], IdentityFields);
                ValidateIdentity(identity); ValidateIdentity(expectedIdentity);
                foreach (var field in new[] { "userSid", "logonId" })
                    if (identity.Text(field) != expectedIdentity.Text(field)) reasons.Add("identity-" + field);
                if (identity.Integer("sessionId") != expectedIdentity.Integer("sessionId")) reasons.Add("identity-sessionId");
                foreach (var field in new[] { "userProfile", "localAppDataLow" })
                    if (!AcceptanceIsolationJson.SamePath(identity.Text(field), expectedIdentity.Text(field))) reasons.Add("identity-" + field);
                var roots = new AcceptanceIsolationJson(observed.Values["observedRoots"], RootFields);
                var expectedRoots = new AcceptanceIsolationJson(request.Values["expectedRoots"], RootFields);
                foreach (var field in RootFields)
                    if (!AcceptanceIsolationJson.SamePath(roots.Text(field), expectedRoots.Text(field))) reasons.Add("root-" + field);
                if (!AcceptanceIsolationJson.SamePath(roots.Text("bepInExRoot"), Path.Combine(roots.Text("gameRoot"), "BepInEx"))
                    || !AcceptanceIsolationJson.SamePath(roots.Text("managerRoot"), Path.Combine(roots.Text("bepInExRoot"), "TopiaForge")))
                    reasons.Add("loader-root-layout");
                if (!AcceptanceIsolationJson.Within(roots.Text("persistentDataRoot"), identity.Text("localAppDataLow")))
                    reasons.Add("native-persistence-outside-known-folder");
                var process = new AcceptanceIsolationJson(observed.Values["process"], "pid", "nativeStartToken", "executablePath");
                if (process.Integer("pid") <= 0 || !Regex.IsMatch(process.Text("nativeStartToken", 64), @"\Awindows:[1-9][0-9]*\z"))
                    reasons.Add("native-process-identity");
                if (!AcceptanceIsolationJson.SamePath(Path.GetDirectoryName(process.Text("executablePath"))!, roots.Text("gameRoot")))
                    reasons.Add("native-executable-root");
            }
            catch (Exception error) when (error is ArgumentException || error is FormatException || error is IOException || error is InvalidDataException || error is OverflowException
                || error is UnauthorizedAccessException || error is System.Runtime.Serialization.SerializationException || error is System.Xml.XmlException)
            { reasons.Add("invalid-isolation-document"); }
            return reasons.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        }
        private static void ValidateIdentity(AcceptanceIsolationJson identity)
        {
            if (!Regex.IsMatch(identity.Text("userSid", 256), @"\AS-[0-9]+(?:-[0-9]+)+\z")
                || !Regex.IsMatch(identity.Text("logonId", 16), @"\A[0-9a-f]{16}\z"))
                throw new InvalidDataException("Invalid primary token identity.");
            identity.Integer("sessionId");
            if (!AcceptanceIsolationJson.Within(identity.Text("localAppDataLow"), identity.Text("userProfile")))
                throw new InvalidDataException("Native known folder is outside the primary profile.");
        }
        private static DateTimeOffset Date(string value)
        {
            if (!(value.EndsWith("Z", StringComparison.Ordinal) || value.EndsWith("+00:00", StringComparison.Ordinal))
                || !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result) || result.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Acceptance timestamps must be UTC.");
            return result;
        }
    }
}
