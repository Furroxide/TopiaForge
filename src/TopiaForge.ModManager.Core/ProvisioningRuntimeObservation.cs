using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace TopiaForge.ModManager.Core
{
    /// <summary>One-shot provisioning observation; never authorizes manager initialization or acceptance.</summary>
    internal static class ProvisioningRuntimeObservation
    {
        internal const string EnvironmentVariable = "TOPIAFORGE_PROVISIONING_PROBE_ID";
        internal static string RequestName(string id) => "provisioning-request-" + Identifier(id) + ".json";
        internal static string ObservationName(string id) => "provisioning-observation-" + Identifier(id) + ".json";

        internal static string Observe(string id, string? acceptanceId, string? profilePath,
            string gameRoot, string bepInExRoot, string managerRoot, Func<string> readObservation, DateTimeOffset now)
        {
            Identifier(id);
            if (acceptanceId != null || profilePath != null)
                throw new InvalidDataException("Provisioning cannot combine with a launch or acceptance request.");
            var staging = AcceptanceIsolationJson.LocalPath(Path.Combine(managerRoot, "staging"));
            if (!Directory.Exists(staging)) throw new InvalidDataException("Provisioning requires existing staging.");
            if (!AcceptanceIsolationJson.SamePath(bepInExRoot, Path.Combine(gameRoot, "BepInEx"))
                || !AcceptanceIsolationJson.SamePath(managerRoot, Path.Combine(bepInExRoot, "TopiaForge")))
                throw new InvalidDataException("Provisioning runtime layout differs.");
            var requestPath = Path.Combine(staging, RequestName(id));
            var outputPath = Path.Combine(staging, ObservationName(id));
            if (File.Exists(outputPath) || Directory.Exists(outputPath))
                throw new InvalidDataException("Provisioning observation already exists.");
            var text = Read(requestPath);
            var request = new AcceptanceIsolationJson(text, "schemaVersion", "kind", "requestId", "challenge",
                "issuedAtUtc", "expiresAtUtc", "expectedIdentity", "expectedProcess", "gameRoot");
            if (request.Integer("schemaVersion") != 1 || request.Text("kind") != "sandbox-provisioning-request-v1"
                || request.Text("requestId") != id || !AcceptanceIsolationJson.SamePath(request.Text("gameRoot"), gameRoot))
                throw new InvalidDataException("Provisioning request correlation differs.");
            var challenge = request.Digest("challenge");
            ValidateTime(request.Text("issuedAtUtc"), request.Text("expiresAtUtc"), now);
            var measured = new AcceptanceIsolationJson(readObservation(), "process", "observedOsIdentity", "observedRoots");
            var actualIdentity = new AcceptanceIsolationJson(measured.Values["observedOsIdentity"], AcceptanceIsolationGate.IdentityFields);
            var expectedIdentity = new AcceptanceIsolationJson(request.Values["expectedIdentity"], AcceptanceIsolationGate.IdentityFields);
            ValidateIdentity(actualIdentity, expectedIdentity);
            var actualProcess = new AcceptanceIsolationJson(measured.Values["process"], "pid", "nativeStartToken", "executablePath");
            var expectedProcess = new AcceptanceIsolationJson(request.Values["expectedProcess"], "pid", "nativeStartToken", "executablePath");
            if (actualProcess.Integer("pid") <= 0 || actualProcess.Integer("pid") != expectedProcess.Integer("pid")
                || !Regex.IsMatch(actualProcess.Text("nativeStartToken"), @"\Awindows:[1-9][0-9]{0,19}\z")
                || actualProcess.Text("nativeStartToken") != expectedProcess.Text("nativeStartToken")
                || !AcceptanceIsolationJson.SamePath(actualProcess.Text("executablePath"), expectedProcess.Text("executablePath"))
                || !AcceptanceIsolationJson.SamePath(actualProcess.Text("executablePath"), Path.Combine(gameRoot, "Robotopia.exe")))
                throw new InvalidDataException("Provisioning original process differs.");
            var roots = new AcceptanceIsolationJson(measured.Values["observedRoots"], AcceptanceIsolationGate.RootFields);
            if (!AcceptanceIsolationJson.SamePath(roots.Text("gameRoot"), gameRoot)
                || !AcceptanceIsolationJson.SamePath(roots.Text("bepInExRoot"), bepInExRoot)
                || !AcceptanceIsolationJson.SamePath(roots.Text("managerRoot"), managerRoot)
                || !AcceptanceIsolationJson.Within(roots.Text("persistentDataRoot"), actualIdentity.Text("localAppDataLow")))
                throw new InvalidDataException("Measured runtime roots are not isolated.");
            if (Read(requestPath) != text) throw new InvalidDataException("Provisioning request changed.");
            var result = AcceptanceIsolationJson.Object(("schemaVersion", "1"),
                ("kind", Q("sandbox-provisioning-runtime-observation-v1")), ("requestId", Q(id)),
                ("challenge", Q(challenge)), ("requestSha256", Q(AcceptanceIsolationJson.Hash(text))),
                ("observedAtUtc", Q(now.UtcDateTime.ToString("O", CultureInfo.InvariantCulture))),
                ("process", measured.Values["process"]), ("observedOsIdentity", measured.Values["observedOsIdentity"]),
                ("observedRoots", measured.Values["observedRoots"]), ("managerInitialized", "false"),
                ("isolationAdmitted", "false"), ("qualifiesRelease", "false"));
            WriteNew(outputPath, result);
            return outputPath;
        }

        internal static void ValidateTime(string issuedText, string expiresText, DateTimeOffset now)
        {
            DateTimeOffset Parse(string text)
            {
                if (!text.EndsWith("Z", StringComparison.Ordinal)
                    || !DateTimeOffset.TryParseExact(text, "yyyy-MM-ddTHH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value))
                    throw new InvalidDataException("Provisioning timestamp is not canonical UTC.");
                return value;
            }
            var issued = Parse(issuedText); var expires = Parse(expiresText);
            if (issued > now || expires <= now || expires <= issued || expires - issued > TimeSpan.FromSeconds(120))
                throw new InvalidDataException("Provisioning challenge is not current and bounded.");
        }

        private static void ValidateIdentity(AcceptanceIsolationJson actual, AcceptanceIsolationJson expected)
        {
            if (!Regex.IsMatch(actual.Text("userSid"), @"\AS-1-5-21-(?:[0-9]+-){2}[0-9]+-[0-9]+\z")
                || !Regex.IsMatch(actual.Text("logonId"), @"\A[0-9a-f]{16}\z") || actual.Integer("sessionId") <= 0
                || actual.Text("userSid") != expected.Text("userSid") || actual.Text("logonId") != expected.Text("logonId")
                || actual.Integer("sessionId") != expected.Integer("sessionId")
                || !AcceptanceIsolationJson.SamePath(actual.Text("userProfile"), expected.Text("userProfile"))
                || !AcceptanceIsolationJson.SamePath(actual.Text("localAppDataLow"), expected.Text("localAppDataLow"))
                || !AcceptanceIsolationJson.Within(actual.Text("localAppDataLow"), actual.Text("userProfile")))
                throw new InvalidDataException("Provisioning primary identity differs.");
        }

        private static string Identifier(string value)
        {
            if (!Regex.IsMatch(value ?? "", @"\Aprobe-[0-9a-f]{32}\z"))
                throw new InvalidDataException("Invalid provisioning request identifier.");
            return value!;
        }
        private static string Q(string value) => AcceptanceIsolationJson.Quote(value);
        private static string Read(string path)
        {
            AcceptanceIsolationJson.LocalPath(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length <= 0 || stream.Length > AcceptanceIsolationJson.MaximumBytes)
                throw new InvalidDataException("Provisioning request exceeds its limit.");
            using var memory = new MemoryStream();
            var buffer = new byte[8192]; int read;
            while ((read = stream.Read(buffer, 0, Math.Min(buffer.Length, AcceptanceIsolationJson.MaximumBytes + 1 - (int)memory.Length))) != 0)
            {
                memory.Write(buffer, 0, read);
                if (memory.Length > AcceptanceIsolationJson.MaximumBytes) throw new InvalidDataException("Provisioning request grew.");
            }
            return new UTF8Encoding(false, true).GetString(memory.ToArray());
        }
        private static void WriteNew(string path, string value)
        {
            AcceptanceIsolationJson.LocalPath(path);
            var bytes = new UTF8Encoding(false, true).GetBytes(value);
            if (bytes.Length > AcceptanceIsolationJson.MaximumBytes) throw new InvalidDataException("Provisioning result exceeds its limit.");
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(bytes, 0, bytes.Length); stream.Flush(true);
        }
    }
}
