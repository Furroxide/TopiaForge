using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class AcceptanceIsolationTests
    {
        internal static void Run()
        {
            using var fixture = new Fixture();
            var passed = 0;
            void Check(bool value, string message)
            {
                if (!value) throw new InvalidOperationException(message);
                passed++;
            }
            Check(fixture.Check().Count == 0, "A measured isolated request must be admitted.");
            foreach (var field in new[] { "challenge", "profileRequestSha256", "isolationEvidenceSha256" })
            {
                var request = fixture.Request(); request[field] = "invalid";
                Check(fixture.Check(request).Count != 0, "Reject malformed " + field);
            }
            foreach (var value in new object?[] { null, "1", 1.5 })
            {
                var request = fixture.Request(); request["schemaVersion"] = value;
                Check(fixture.Check(request).Count != 0, "Reject scalar coercion.");
            }
            foreach (var field in new[] { "userSid", "logonId", "sessionId", "userProfile", "localAppDataLow" })
            {
                var request = fixture.Request(); var identity = fixture.Identity();
                identity[field] = field == "sessionId" ? 99 : "different";
                request["expectedOsIdentity"] = identity;
                Check(fixture.Check(request).Count != 0, "Reject mismatched primary identity " + field);
            }
            foreach (var field in new[] { "gameRoot", "bepInExRoot", "managerRoot", "persistentDataRoot" })
            {
                var request = fixture.Request(); var roots = fixture.Roots();
                roots[field] = fixture.Root.FullName;
                request["expectedRoots"] = roots;
                Check(fixture.Check(request).Count != 0, "Reject mismatched root " + field);
            }
            var expired = fixture.Request(); expired["issuedAtUtc"] = fixture.Now.AddMinutes(-16).ToString("O");
            expired["expiresAtUtc"] = fixture.Now.AddMinutes(-1).ToString("O");
            Check(fixture.Check(expired).Count != 0, "Reject expired challenges.");
            var future = fixture.Request(); future["issuedAtUtc"] = fixture.Now.AddHours(1).ToString("O");
            Check(fixture.Check(future).Count != 0, "Reject future challenges.");
            var wrong = fixture.Request(); wrong["requestId"] = "Another-Request";
            Check(fixture.Check(wrong).Count != 0, "Reject foreign request identity.");
            var extra = fixture.Request(); extra["unknown"] = true;
            Check(fixture.Check(extra).Count != 0, "Reject unknown request fields.");
            var raw = JsonSerializer.Serialize(fixture.Request());
            Check(AcceptanceIsolationGate.Validate(raw.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1"),
                fixture.Profile, "Request-A", fixture.Observation, fixture.Now).Count != 0, "Reject duplicate fields.");
            Check(AcceptanceIsolationGate.Validate(raw, fixture.Profile + " ", "Request-A", fixture.Observation, fixture.Now).Count != 0,
                "Hash must bind exact profile bytes, not equivalent JSON.");
            Check(AcceptanceIsolationGate.Validate(new string(' ', 65537), fixture.Profile, "Request-A", fixture.Observation, fixture.Now).Count != 0,
                "Reject oversized private sidecars.");
            if (OperatingSystem.IsWindows())
            {
                var acceptedAliases = new List<string>();
                foreach (var component in new[] { "ordinary.", "ordinary ", "ordinary:stream", "NUL.txt", "COM¹", "LPT2.log" })
                {
                    try { AcceptanceIsolationJson.LocalPath(Path.Combine(fixture.Root.FullName, component, "child")); acceptedAliases.Add(component); }
                    catch (Exception error) when (error is IOException || error is InvalidDataException) { passed++; }
                }
                Check(acceptedAliases.Count == 0, "Windows aliases were admitted: " + string.Join(", ", acceptedAliases));
                Check(AcceptanceIsolationJson.SamePath(fixture.Game.ToUpperInvariant(), fixture.Game), "Windows path case must remain equivalent.");
            }
            Console.WriteLine("Acceptance isolation: " + passed + " checks passed.");
        }
        internal sealed class Fixture : IDisposable
        {
            internal DirectoryInfo Root = CreateRoot();
            internal DateTimeOffset Now = new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);
            internal string Game => Path.Combine(Root.FullName, "game");
            internal string User => Path.Combine(Root.FullName, "qa");
            internal string Low => Path.Combine(User, "AppData", "LocalLow");
            internal string Profile = LaunchTransportJson.WriteProfile(new ProfileLaunchConfigurationV4("QA", 1, "Request-A", "main-menu",
                Array.Empty<PackageIdentity>(), PackageSetDigest.Of(Array.Empty<PackageIdentity>()), false, false,
                Array.Empty<string>(), new Dictionary<string, string>()));
            private static DirectoryInfo CreateRoot()
            {
                var owned = Directory.CreateTempSubdirectory("TopiaForgeIsolation-");
                if (!OperatingSystem.IsWindows()) return owned;
                try
                {
                    // Windows runners may spell TEMP with 8.3 components. Only our freshly
                    // created fixture root is normalized; raw admission paths remain strict.
                    var buffer = new StringBuilder(32768);
                    var length = GetLongPathNameW(owned.FullName, buffer, (uint)buffer.Capacity);
                    if (length == 0 || length >= buffer.Capacity) throw new IOException("Cannot canonicalize the owned test directory.");
                    return new DirectoryInfo(buffer.ToString());
                }
                catch { owned.Delete(true); throw; }
            }
            [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
            private static extern uint GetLongPathNameW(string path, StringBuilder result, uint capacity);
            internal Fixture() { Directory.CreateDirectory(Game); Directory.CreateDirectory(Low); }
            internal Dictionary<string, object?> Identity() => new Dictionary<string, object?>
            {
                ["userSid"] = "S-1-5-21-100",
                ["logonId"] = "0000000000000001",
                ["sessionId"] = 1,
                ["userProfile"] = User,
                ["localAppDataLow"] = Low
            };
            internal Dictionary<string, object?> Roots() => new Dictionary<string, object?>
            {
                ["gameRoot"] = Game,
                ["bepInExRoot"] = Path.Combine(Game, "BepInEx"),
                ["managerRoot"] = Path.Combine(Game, "BepInEx", "TopiaForge"),
                ["persistentDataRoot"] = Path.Combine(Low, "Publisher", "Game")
            };
            internal Dictionary<string, object?> Request() => new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["requestId"] = "Request-A",
                ["challenge"] = new string('a', 64),
                ["profileRequestSha256"] = Hash(Profile),
                ["isolationEvidenceSha256"] = new string('b', 64),
                ["issuedAtUtc"] = Now.AddMinutes(-1).ToString("O"),
                ["expiresAtUtc"] = Now.AddMinutes(14).ToString("O"),
                ["expectedOsIdentity"] = Identity(),
                ["expectedRoots"] = Roots()
            };
            internal string Observation => JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["process"] = new Dictionary<string, object?> { ["pid"] = 123, ["nativeStartToken"] = "windows:1234", ["executablePath"] = Path.Combine(Game, "Robotopia.exe") },
                ["observedOsIdentity"] = Identity(),
                ["observedRoots"] = Roots()
            });
            internal IReadOnlyList<string> Check(Dictionary<string, object?>? request = null) => AcceptanceIsolationGate.Validate(
                JsonSerializer.Serialize(request ?? Request()), Profile, "Request-A", Observation, Now);
            internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
            public void Dispose() => Root.Delete(true);
        }
    }
}
