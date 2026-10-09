using System.Text.Json;
using System.Text.RegularExpressions;

namespace TopiaForge.Acceptance.Windows;

internal sealed record ProcessStamp(int Pid, string NativeStartToken, string ExecutablePath);
internal sealed record IdentityStamp(string UserSid, string LogonId, int SessionId, string UserProfile, string LocalAppDataLow);
internal sealed record BrokerRequest(JsonElement Document, string RunId, string Challenge, string ManagerSessionId,
    ProcessStamp Process, IdentityStamp Identity, string GameRoot, string OutputRoot, string PersistentDataRoot,
    string IsolationRecordPath, string DeviceProfilePath, string DriverManifestPath, int TimeoutSeconds)
{
    internal static BrokerRequest Read(string path)
    {
        var root = BoundedJson.Read(path);
        BoundedJson.Keys(root, "schemaVersion", "kind", "runId", "challenge", "managerSessionId", "process", "identity", "gameRoot", "outputRoot", "persistentDataRoot", "isolationRecordPath", "isolationRecordSha256", "acknowledgementSha256", "deviceProfilePath", "deviceProfileSha256", "driverManifestPath", "driverManifestSha256", "timeoutSeconds");
        if (BoundedJson.Integer(root, "schemaVersion", 1, 1) != 1 || BoundedJson.Text(root, "kind") != "sandbox-broker-request-v1") throw new InvalidDataException("Unsupported broker request.");
        var runId = BoundedJson.Text(root, "runId", 80);
        if (!Regex.IsMatch(runId, "^[a-z][a-z0-9-]{0,79}$")) throw new InvalidDataException("Invalid run ID.");
        var process = root.GetProperty("process");
        BoundedJson.Keys(process, "pid", "nativeStartToken", "executablePath");
        var token = BoundedJson.Text(process, "nativeStartToken", 64);
        if (!Regex.IsMatch(token, "^windows:[0-9]{1,20}$")) throw new InvalidDataException("A Windows native creation token is required.");
        var identity = root.GetProperty("identity");
        BoundedJson.Keys(identity, "userSid", "logonId", "sessionId", "userProfile", "localAppDataLow");
        var request = new BrokerRequest(root, runId, BoundedJson.Digest(root, "challenge"), BoundedJson.Text(root, "managerSessionId", 256),
            new(BoundedJson.Integer(process, "pid", 1, int.MaxValue), token, BoundedJson.PhysicalPath(BoundedJson.Text(process, "executablePath"))),
            new(BoundedJson.Text(identity, "userSid", 256), BoundedJson.Text(identity, "logonId", 16), BoundedJson.Integer(identity, "sessionId", 1, int.MaxValue), BoundedJson.PhysicalPath(BoundedJson.Text(identity, "userProfile")), BoundedJson.PhysicalPath(BoundedJson.Text(identity, "localAppDataLow"))),
            BoundedJson.PhysicalPath(BoundedJson.Text(root, "gameRoot")), BoundedJson.PhysicalPath(BoundedJson.Text(root, "outputRoot")), BoundedJson.PhysicalPath(BoundedJson.Text(root, "persistentDataRoot")),
            BoundedJson.PhysicalPath(BoundedJson.Text(root, "isolationRecordPath")), BoundedJson.PhysicalPath(BoundedJson.Text(root, "deviceProfilePath")), BoundedJson.PhysicalPath(BoundedJson.Text(root, "driverManifestPath")), BoundedJson.Integer(root, "timeoutSeconds", 30, 14400));
        request.VerifyFiles();
        return request;
    }
    internal void VerifyFiles()
    {
        foreach (var name in new[] { "isolationRecord", "deviceProfile", "driverManifest" })
            if (BoundedJson.Hash(BoundedJson.Text(Document, name + "Path")) != BoundedJson.Digest(Document, name + "Sha256")) throw new InvalidDataException("Bound " + name + " bytes changed.");
        _ = BoundedJson.Digest(Document, "acknowledgementSha256");
        var record = BoundedJson.Read(IsolationRecordPath);
        BoundedJson.Keys(record, "schemaVersion", "kind", "sourceGameRoot", "gameRoot", "launcherRoot", "outputRoot", "persistentDataRoot", "userSid", "userProfile", "localAppDataLow", "normalUserSid", "normalUserProfile", "reviewerEvidence");
        if (BoundedJson.Integer(record, "schemaVersion", 1, 1) != 1 || !new[] { "windows-user", "virtual-machine" }.Contains(BoundedJson.Text(record, "kind")) || BoundedJson.Text(record, "userSid") != Identity.UserSid || BoundedJson.Text(record, "normalUserSid") == Identity.UserSid)
            throw new InvalidDataException("QA identity is not isolated.");
        _ = BoundedJson.Text(record, "reviewerEvidence", 1024);
        if (!BoundedJson.SamePath(GameRoot, BoundedJson.Text(record, "gameRoot")) || !BoundedJson.SamePath(PersistentDataRoot, BoundedJson.Text(record, "persistentDataRoot")) || !BoundedJson.SamePath(Identity.UserProfile, BoundedJson.Text(record, "userProfile")) || !BoundedJson.SamePath(Identity.LocalAppDataLow, BoundedJson.Text(record, "localAppDataLow"))) throw new InvalidDataException("QA roots do not match the reviewed record.");
        var admittedOutput = BoundedJson.PhysicalPath(BoundedJson.Text(record, "outputRoot"));
        if (!BoundedJson.SamePath(OutputRoot, BoundedJson.Child(admittedOutput, "sandbox/" + RunId))) throw new InvalidDataException("Run output must be the immutable admitted Sandbox child.");
        foreach (var root in new[] { GameRoot, OutputRoot, PersistentDataRoot })
        {
            foreach (var excluded in new[] { BoundedJson.Text(record, "normalUserProfile"), BoundedJson.Text(record, "sourceGameRoot") })
                if (Overlaps(root, excluded)) throw new InvalidDataException("Writable QA root overlaps normal/source data.");
            BoundedJson.PhysicalPath(root);
        }
        if (!BoundedJson.SamePath(Process.ExecutablePath, Path.Combine(GameRoot, "Robotopia.exe"))) throw new InvalidDataException("Unexpected game executable.");
    }
    private static bool Overlaps(string left, string right) => BoundedJson.SamePath(left, right)
        || Path.GetFullPath(left).StartsWith(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(right).StartsWith(Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
