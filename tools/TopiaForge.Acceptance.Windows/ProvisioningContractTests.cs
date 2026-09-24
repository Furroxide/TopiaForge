using System.Text.Json;
using System.Text.Json.Nodes;

namespace TopiaForge.Acceptance.Windows;

internal static class ProvisioningContractTests
{
    internal static void Run()
    {
        var count = 0;
        void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); count++; }
        void Refuse(Action action)
        {
            try { action(); }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException or JsonException or IOException or ArgumentException) { count++; return; }
            throw new InvalidOperationException("Provisioning contract should have refused.");
        }
        foreach (var path in new[] { ".doorstop_version", "Robotopia_Data/Managed/02 City Streets.dll", "Robotopia_Data/Resources/unity default resources" })
        { ProvisioningGameInputs.ValidateRelativePath(path); count++; }
        foreach (var path in new[] { "", ".", "..", "../outside", "/outside", @"folder\file", "folder//file", "folder/../file",
            ".secret", "folder/.doorstop_version", " leading/file", "folder/trailing ", "folder/trailing.", "folder/CON.txt",
            "folder/NUL", "folder/LPT9.log", "folder/file:stream", "folder/a?b", "folder/a*b", "folder/a|b", "folder/\"file", "folder/\u0000file" })
            Refuse(() => ProvisioningGameInputs.ValidateRelativePath(path));
        Check(ProvisioningGameInputs.AddLength(0, 1621538808L) == 1621538808L, "Measured large game asset is within the stream limit.");
        Check(ProvisioningGameInputs.AddLength(0, ProvisioningGameInputs.MaximumFileBytes) == ProvisioningGameInputs.MaximumFileBytes, "Exact 2 GiB file boundary.");
        long aggregate = 0;
        for (var i = 0; i < 4; i++) aggregate = ProvisioningGameInputs.AddLength(aggregate, ProvisioningGameInputs.MaximumFileBytes);
        Check(aggregate == ProvisioningGameInputs.MaximumTotalBytes, "Exact 8 GiB aggregate boundary.");
        foreach (var pair in new[] { (0L, ProvisioningGameInputs.MaximumFileBytes + 1), (aggregate, 1L), (-1L, 0L), (0L, -1L), (long.MaxValue, long.MaxValue) })
            Refuse(() => ProvisioningGameInputs.AddLength(pair.Item1, pair.Item2));
        using var fixture = new Fixture();
        Refuse(() => BoundedJson.Child(fixture.Root, ".doorstop_version")); // Evidence artifact grammar remains unchanged.
        var input = ProvisioningLaunch.Read(fixture.InputPath);
        input.VerifyFiles(); count++;
        Check(input.Files.Count == ProvisioningLaunch.LoaderNames.Length + 5, "Complete fixed fixture inventory.");
        using (var lease = new ProvisioningInputLease(input))
        {
            if (OperatingSystem.IsWindows())
                Refuse(() => File.AppendAllText(input.Executable, "changed"));
        }
        var nested = "BepInEx/plugins/TopiaForge.ModManager/nested/TopiaForge.ModManager.dll";
        var nestedPath = ProvisioningGameInputs.Child(input.GameRoot, nested);
        Directory.CreateDirectory(Path.GetDirectoryName(nestedPath)!);
        File.Copy(Path.Combine(input.GameRoot, "BepInEx/plugins/TopiaForge.ModManager/TopiaForge.ModManager.dll"), nestedPath);
        var nestedInput = fixture.Input.DeepClone();
        nestedInput["files"]!.AsArray().Add(JsonSerializer.SerializeToNode(new ProvisioningFile(nested, BoundedJson.Hash(nestedPath)), BoundedJson.Options));
        File.WriteAllText(fixture.InputPath, nestedInput.ToJsonString());
        Refuse(() => ProvisioningLaunch.Read(fixture.InputPath).VerifyFiles());
        File.Delete(nestedPath); // Exact test-owned duplicate only.
        File.WriteAllText(fixture.InputPath, fixture.Input.ToJsonString());
        var extra = Path.Combine(input.GameRoot, "unexpected.dll");
        File.WriteAllText(extra, "extra");
        Refuse(input.VerifyFiles);
        File.Delete(extra); // Exact synthetic fixture child, never QA data.
        var duplicate = fixture.Input.DeepClone().AsObject();
        duplicate["files"]!.AsArray().Add(duplicate["files"]![0]!.DeepClone());
        File.WriteAllText(fixture.InputPath, duplicate.ToJsonString());
        Refuse(() => ProvisioningLaunch.Read(fixture.InputPath));
        foreach (var field in new[] { "reviewerEvidence", "persistentDataRoot", "command", "timeoutSeconds", "isolationAdmitted" })
        {
            var invalid = fixture.Input.DeepClone().AsObject(); invalid[field] = "unapproved";
            File.WriteAllText(fixture.InputPath, invalid.ToJsonString());
            Refuse(() => ProvisioningLaunch.Read(fixture.InputPath));
        }
        foreach (var field in new[] { "gameRoot", "outputRoot", "sourceGameRoot" })
        {
            var invalid = fixture.Input.DeepClone().AsObject(); invalid[field] = fixture.Root;
            File.WriteAllText(fixture.InputPath, invalid.ToJsonString());
            Refuse(() => ProvisioningLaunch.Read(fixture.InputPath));
        }
        var sameUser = fixture.Input.DeepClone().AsObject(); sameUser["normalUserSid"] = input.Identity.UserSid;
        File.WriteAllText(fixture.InputPath, sameUser.ToJsonString());
        Refuse(() => ProvisioningLaunch.Read(fixture.InputPath));
        File.WriteAllText(fixture.InputPath, fixture.Input.ToJsonString());

        var rule = new ProvisioningFirewall.Rule(true, 2, 0, int.MaxValue, 256, input.Executable, "*", "*", "*", "*", "All", "", true);
        ProvisioningFirewall.Validate(rule, input.Executable); count++;
        foreach (var invalid in new[]
        {
            rule with { Enabled = false }, rule with { Direction = 1 }, rule with { Action = 1 }, rule with { Profiles = 1 },
            rule with { Protocol = 6 }, rule with { Application = Path.Combine(input.SourceGameRoot, "Robotopia.exe") },
            rule with { LocalAddresses = "127.0.0.1" }, rule with { RemoteAddresses = "10.0.0.1" },
            rule with { LocalPorts = "443" }, rule with { RemotePorts = "443" }, rule with { InterfaceTypes = "Wireless" },
            rule with { Service = "some-service" }, rule with { AllProfilesEnabled = false }
        }) Refuse(() => ProvisioningFirewall.Validate(invalid, input.Executable));

        var process = new ProcessStamp(123, "windows:123456", input.Executable);
        var issued = new DateTime(2026, 9, 9, 20, 0, 0, DateTimeKind.Utc);
        var root = JsonSerializer.SerializeToNode(new
        {
            schemaVersion = 1, kind = "sandbox-provisioning-runtime-observation-v1",
            requestId = "probe-" + new string('a', 32), challenge = new string('b', 64), requestSha256 = new string('c', 64),
            observedAtUtc = issued.AddSeconds(1).ToString("O"), process, observedOsIdentity = input.Identity,
            observedRoots = new { gameRoot = input.GameRoot, bepInExRoot = Path.Combine(input.GameRoot, "BepInEx"),
                managerRoot = input.ManagerRoot, persistentDataRoot = Path.Combine(input.Identity.LocalAppDataLow, "ActualPublisher", "ActualGame") },
            managerInitialized = false, isolationAdmitted = false, qualifiesRelease = false
        }, BoundedJson.Options)!;
        void Verify(JsonNode value) => ProvisioningLauncher.VerifyObservation(JsonSerializer.SerializeToElement(value),
            "probe-" + new string('a', 32), new string('b', 64), new string('c', 64), process, input.Identity, input.GameRoot, issued, issued.AddSeconds(120));
        Verify(root); count++;
        foreach (var field in new[] { "managerInitialized", "isolationAdmitted", "qualifiesRelease" })
        {
            var invalid = root.DeepClone(); invalid[field] = true; Refuse(() => Verify(invalid));
            invalid = root.DeepClone(); invalid[field] = "false"; Refuse(() => Verify(invalid));
        }
        foreach (var field in new[] { "requestId", "challenge", "requestSha256", "kind", "observedAtUtc" })
        { var invalid = root.DeepClone(); invalid[field] = "different"; Refuse(() => Verify(invalid)); }
        foreach (var time in new[] { issued.AddSeconds(-1), issued.AddSeconds(121) })
        { var invalid = root.DeepClone(); invalid["observedAtUtc"] = time.ToString("O"); Refuse(() => Verify(invalid)); }
        foreach (var field in new[] { "pid", "nativeStartToken", "executablePath" })
        {
            var invalid = root.DeepClone();
            invalid["process"]![field] = field == "pid" ? JsonValue.Create(999) : JsonValue.Create("different");
            Refuse(() => Verify(invalid));
        }
        var wrongIdentity = root.DeepClone(); wrongIdentity["observedOsIdentity"]!["sessionId"] = 99; Refuse(() => Verify(wrongIdentity));
        var wrongRoot = root.DeepClone(); wrongRoot["observedRoots"]!["persistentDataRoot"] = fixture.Root; Refuse(() => Verify(wrongRoot));
        var extraClaim = root.DeepClone(); extraClaim["acceptanceAcknowledgement"] = true; Refuse(() => Verify(extraClaim));

        // Diagnostic launch mode: one computed log destination, fixed headless flags, no operator arguments.
        var logRun = Directory.CreateTempSubdirectory("TopiaForgeProvisioningLog-").FullName;
        try
        {
            // The game executable is a fully qualified .exe path on the host running the checks; the contract is platform-neutral.
            var gameDirectory = Path.Combine(logRun, "Game");
            var executable = Path.Combine(gameDirectory, "Robotopia.exe");
            var log = ProvisioningProcess.ValidatePlayerLogPath(Path.Combine(logRun, "player.log"), logRun); count++;
            Check(ProvisioningProcess.BuildCommandLine(executable, log) == "\"" + executable + "\" -batchmode -nographics -noaudio -logFile \"" + log + "\"",
                "Fixed headless command line with the computed log destination.");
            Check(ProvisioningProcess.LaunchArguments(log).SequenceEqual(new[] { "-batchmode", "-nographics", "-noaudio", "-logFile", log }), "Recorded launch arguments.");
            Refuse(() => ProvisioningProcess.ValidatePlayerLogPath(Path.Combine(logRun, "other.log"), logRun));
            Refuse(() => ProvisioningProcess.ValidatePlayerLogPath(Path.Combine(logRun, "nested", "player.log"), logRun));
            Refuse(() => ProvisioningProcess.ValidatePlayerLogPath(Path.Combine(Path.GetTempPath(), "player.log"), logRun));
            File.WriteAllText(Path.Combine(logRun, "player.log"), "");
            Refuse(() => ProvisioningProcess.ValidatePlayerLogPath(Path.Combine(logRun, "player.log"), logRun));
            File.Delete(Path.Combine(logRun, "player.log"));
            Refuse(() => ProvisioningProcess.BuildCommandLine("Robotopia.exe", log));
            Refuse(() => ProvisioningProcess.BuildCommandLine(executable + " -extra", log));
            Refuse(() => ProvisioningProcess.BuildCommandLine(executable, Path.Combine(gameDirectory, "pl\"ayer.log")));
            Refuse(() => ProvisioningProcess.BuildCommandLine(executable, Path.Combine(gameDirectory, "other.log")));
            Refuse(() => ProvisioningProcess.BuildCommandLine(executable, "player.log"));
        }
        finally { Directory.Delete(logRun, true); }
        Check(ProvisioningDiagnostics.Port(0x5000) == 80 && ProvisioningDiagnostics.Port(0xbb01) == 443, "Endpoint ports decode from network byte order.");
        Check(ProvisioningDiagnostics.TcpState(3) == "SYN_SENT" && ProvisioningDiagnostics.TcpState(5) == "ESTABLISHED" && ProvisioningDiagnostics.TcpState(99) == "UNKNOWN_99", "TCP state names.");
        Check(ProvisioningRuntimeWait.Deadline == TimeSpan.FromSeconds(90) && ProvisioningRuntimeWait.SnapshotSeconds.SequenceEqual(new[] { 15, 45, 75 }), "Unchanged deadline with bounded snapshot schedule.");
        if (OperatingSystem.IsWindows())
        {
            // The read-only snapshot uses Windows process, Toolhelp and IP-helper APIs; hosted Linux contract runs skip only this self-observation.
            var self = System.Text.Json.JsonSerializer.SerializeToElement(ProvisioningDiagnostics.Snapshot(
                new Microsoft.Win32.SafeHandles.SafeProcessHandle(NativeMethods.GetCurrentProcess(), false), Environment.ProcessId, null, "self-test", 0, false, null), BoundedJson.Options);
            Check(self.GetProperty("threads").GetProperty("count").GetInt32() >= 1 && self.GetProperty("handleCount").GetUInt32() > 0
                && self.GetProperty("tcp").GetProperty("count").GetInt32() >= 0 && self.GetProperty("udp").GetProperty("count").GetInt32() >= 0
                && self.GetProperty("cpu").GetProperty("userMilliseconds").GetUInt64() >= 0 && self.GetProperty("memory").GetProperty("workingSetBytes").GetUInt64() > 0
                && self.GetProperty("playerLog").ValueKind == System.Text.Json.JsonValueKind.Null && self.GetProperty("notes").GetArrayLength() == 0,
                "Read-only self snapshot covers threads, handles, memory, endpoints and windows without failures.");
        }
        Check(PlayerLogFact.Read(null).Exists == false && PlayerLogFact.Read(Path.Combine(logRun, "player.log")).Exists == false, "Absent player log is recorded as absent.");
        Console.WriteLine("Provisioning launcher contract checks passed: " + count + ".");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root = Directory.CreateTempSubdirectory("TopiaForgeProvisioningContracts-").FullName;
        internal string InputPath => Path.Combine(Root, "input.json");
        internal JsonNode Input;
        internal Fixture()
        {
            var qa = Directory.CreateDirectory(Path.Combine(Root, "QA")).FullName;
            var game = Directory.CreateDirectory(Path.Combine(qa, "game")).FullName;
            var source = Directory.CreateDirectory(Path.Combine(qa, "source")).FullName;
            var output = Directory.CreateDirectory(Path.Combine(qa, "evidence")).FullName;
            var user = Directory.CreateDirectory(Path.Combine(Root, "user")).FullName;
            var low = Directory.CreateDirectory(Path.Combine(user, "AppData", "LocalLow")).FullName;
            var normal = Directory.CreateDirectory(Path.Combine(Root, "normal")).FullName;
            var rows = new List<ProvisioningFile>();
            foreach (var name in new[] { "Robotopia.exe", "UnityPlayer.dll", ".doorstop_version", "Robotopia_Data/Managed/02 City Streets.dll", "Robotopia_Data/Resources/unity default resources" }.Concat(
                ProvisioningLaunch.LoaderNames.Select(name => "BepInEx/plugins/TopiaForge.ModManager/" + name)))
            {
                var file = Path.Combine(game, name); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, "synthetic:" + name);
                rows.Add(new(name, BoundedJson.Hash(file)));
                if (!name.Contains('/')) File.Copy(file, Path.Combine(source, name));
            }
            Input = JsonSerializer.SerializeToNode(new
            {
                schemaVersion = 1, kind = "sandbox-provisioning-launch-v1", qaRoot = qa, gameRoot = game,
                sourceGameRoot = source, outputRoot = output, normalUserSid = "S-1-5-21-1-2-3-1000", normalUserProfile = normal,
                expectedIdentity = new IdentityStamp("S-1-5-21-1-2-3-1001", "0000000000000001", 1, user, low),
                outboundBlockRuleName = "synthetic-rule-never-applied", files = rows
            }, BoundedJson.Options)!;
            File.WriteAllText(InputPath, Input.ToJsonString());
        }
        public void Dispose() => Directory.Delete(Root, true);
    }
}
