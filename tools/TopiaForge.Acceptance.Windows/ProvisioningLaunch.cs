using System.Text.Json;
using System.Text.RegularExpressions;

namespace TopiaForge.Acceptance.Windows;

internal sealed record ProvisioningLaunch(string InputPath, string InputSha256, string QaRoot, string GameRoot,
    string SourceGameRoot, string OutputRoot, string NormalUserSid, string NormalUserProfile,
    IdentityStamp Identity, string OutboundBlockRuleName, IReadOnlyList<ProvisioningFile> Files)
{
    internal static readonly string[] LoaderNames =
    {
        "System.Collections.Immutable.dll", "System.Reflection.Metadata.dll", "TopiaForge.ModManager.dll",
        "TopiaForge.ModManager.Core.dll", "TopiaForge.Mods.Abstractions.dll", "TopiaForge.Mods.Chronos.dll",
        "TopiaForge.Mods.CreatorContent.dll", "TopiaForge.Mods.Interop.Unity.dll", "TopiaForge.Mods.Multiplayer.dll",
        "TopiaForge.Mods.Prompts.dll", "TopiaForge.Mods.RobotKit.dll", "TopiaForge.Mods.UnityUi.dll", "TopiaForge.Mods.Worlds.dll"
    };
    internal string Executable => Path.Combine(GameRoot, "Robotopia.exe");
    internal string ManagerRoot => Path.Combine(GameRoot, "BepInEx", "TopiaForge");
    internal static ProvisioningLaunch Read(string path)
    {
        var bytes = BoundedJson.ReadBytes(path, 4 * 1024 * 1024);
        var root = BoundedJson.Parse(bytes, 4 * 1024 * 1024);
        BoundedJson.Keys(root, "schemaVersion", "kind", "qaRoot", "gameRoot", "sourceGameRoot", "outputRoot",
            "normalUserSid", "normalUserProfile", "expectedIdentity", "outboundBlockRuleName", "files");
        if (BoundedJson.Integer(root, "schemaVersion", 1, 1) != 1 || BoundedJson.Text(root, "kind") != "sandbox-provisioning-launch-v1")
            throw new InvalidDataException("Not a provisioning-only launch input.");
        var identity = ReadIdentity(root.GetProperty("expectedIdentity"));
        var inventory = root.GetProperty("files");
        if (inventory.ValueKind != JsonValueKind.Array || inventory.GetArrayLength() is < 1 or > 10000)
            throw new InvalidDataException("Provisioning input inventory exceeds its bound.");
        var files = new List<ProvisioningFile>(); var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in inventory.EnumerateArray())
        {
            BoundedJson.Keys(row, "path", "sha256");
            var relative = BoundedJson.Text(row, "path", 255);
            if (!names.Add(relative) || relative.Contains('\\')) throw new InvalidDataException("Inventory names are not unique forward-slash paths.");
            ProvisioningGameInputs.ValidateRelativePath(relative);
            files.Add(new(relative, BoundedJson.Digest(row, "sha256")));
        }
        var request = new ProvisioningLaunch(BoundedJson.PhysicalPath(path), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
            BoundedJson.PhysicalPath(BoundedJson.Text(root, "qaRoot")), BoundedJson.PhysicalPath(BoundedJson.Text(root, "gameRoot")),
            BoundedJson.PhysicalPath(BoundedJson.Text(root, "sourceGameRoot")), BoundedJson.PhysicalPath(BoundedJson.Text(root, "outputRoot")),
            BoundedJson.Text(root, "normalUserSid", 256), BoundedJson.PhysicalPath(BoundedJson.Text(root, "normalUserProfile")),
            identity, BoundedJson.Text(root, "outboundBlockRuleName", 128), files.AsReadOnly());
        request.VerifyLayout();
        return request;
    }
    internal static IdentityStamp ReadIdentity(JsonElement value)
    {
        BoundedJson.Keys(value, "userSid", "logonId", "sessionId", "userProfile", "localAppDataLow");
        var sid = BoundedJson.Text(value, "userSid", 256); var logon = BoundedJson.Text(value, "logonId", 16);
        if (!Regex.IsMatch(sid, @"\AS-1-5-21-(?:[0-9]+-){2}[0-9]+-[0-9]+\z") || !Regex.IsMatch(logon, @"\A[0-9a-f]{16}\z"))
            throw new InvalidDataException("Invalid native primary identity.");
        return new(sid, logon, BoundedJson.Integer(value, "sessionId", 1, int.MaxValue),
            BoundedJson.PhysicalPath(BoundedJson.Text(value, "userProfile")), BoundedJson.PhysicalPath(BoundedJson.Text(value, "localAppDataLow")));
    }
    internal static bool Within(string path, string root) =>
        path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    internal void VerifyLayout()
    {
        foreach (var root in new[] { QaRoot, GameRoot, SourceGameRoot, OutputRoot, Identity.UserProfile, Identity.LocalAppDataLow })
            if (!Directory.Exists(root)) throw new InvalidDataException("Every provisioned root must already exist.");
        if (Identity.UserSid == NormalUserSid || BoundedJson.SamePath(Identity.UserProfile, NormalUserProfile)
            || !Regex.IsMatch(NormalUserSid, @"\AS-1-5-21-(?:[0-9]+-){2}[0-9]+-[0-9]+\z")
            || !Within(Identity.LocalAppDataLow, Identity.UserProfile))
            throw new InvalidDataException("Normal and QA native identities must be separate.");
        foreach (var path in new[] { GameRoot, SourceGameRoot, OutputRoot })
            if (!Within(path, QaRoot) || Within(path, NormalUserProfile) || Within(NormalUserProfile, path))
                throw new InvalidDataException("Provisioning must remain within the separate QA root.");
        var roots = new[] { GameRoot, SourceGameRoot, OutputRoot };
        for (var a = 0; a < roots.Length; a++) for (var b = a + 1; b < roots.Length; b++)
            if (BoundedJson.SamePath(roots[a], roots[b]) || Within(roots[a], roots[b]) || Within(roots[b], roots[a]))
                throw new InvalidDataException("Source, game and output roots must be disjoint.");
        foreach (var file in Files)
        {
            var target = ProvisioningGameInputs.Child(GameRoot, file.Path);
            if (Within(target, ManagerRoot)) throw new InvalidDataException("Provisioning inventory must contain no manager state or packages.");
        }
        foreach (var required in new[] { "Robotopia.exe", "UnityPlayer.dll", "BepInEx/plugins/TopiaForge.ModManager/TopiaForge.ModManager.dll", "BepInEx/plugins/TopiaForge.ModManager/TopiaForge.ModManager.Core.dll" })
            if (!Files.Any(file => file.Path == required)) throw new InvalidDataException("Required game/runtime input is absent: " + required);
    }
    internal void VerifyFiles()
    {
        if (BoundedJson.Hash(InputPath) != InputSha256) throw new InvalidDataException("Provisioning launch input changed.");
        var expected = Files.ToDictionary(row => row.Path, StringComparer.OrdinalIgnoreCase);
        long totalBytes = 0;
        foreach (var file in Files)
        {
            var path = ProvisioningGameInputs.Child(GameRoot, file.Path);
            if (!File.Exists(path)) throw new InvalidDataException("Provisioning game/runtime file is absent.");
            totalBytes = ProvisioningGameInputs.AddLength(totalBytes, new FileInfo(path).Length);
            if (BoundedJson.Hash(path) != file.Sha256)
                throw new InvalidDataException("Provisioning game/runtime bytes changed.");
        }
        var count = 0;
        foreach (var path in Walk(GameRoot))
        {
            if (++count > 20000) throw new InvalidDataException("Provisioning file tree exceeds its bound.");
            var relative = Path.GetRelativePath(GameRoot, path).Replace('\\', '/');
            if (!expected.ContainsKey(relative)) throw new InvalidDataException("Uninventoried game/runtime input.");
        }
        foreach (var name in new[] { "Robotopia.exe", "UnityPlayer.dll" })
            if (BoundedJson.Hash(Path.Combine(SourceGameRoot, name)) != expected[name].Sha256)
                throw new InvalidDataException("QA game differs from the verified binary source.");
        // Only the known loader plugin family may load; unrelated BepInEx plugins are refused.
        if (Files.Any(row => row.Path.StartsWith("BepInEx/patchers/", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Provisioning may not load extra preloader patchers.");
        foreach (var name in LoaderNames)
            if (!Files.Any(row => row.Path == "BepInEx/plugins/TopiaForge.ModManager/" + name))
                throw new InvalidDataException("Provisioning requires the complete known loader family.");
        foreach (var row in Files.Where(row => row.Path.StartsWith("BepInEx/plugins/", StringComparison.OrdinalIgnoreCase)))
            if (!row.Path.StartsWith("BepInEx/plugins/TopiaForge.ModManager/", StringComparison.Ordinal)
                || !row.Path.EndsWith(".dll", StringComparison.Ordinal)
                || !LoaderNames.Any(name => row.Path == "BepInEx/plugins/TopiaForge.ModManager/" + name))
                throw new InvalidDataException("Provisioning may not load unrelated plugins.");
    }
    private static IEnumerable<string> Walk(string root)
    {
        var stack = new Stack<string>(); stack.Push(root); var count = 0;
        while (stack.Count != 0)
            foreach (var item in Directory.EnumerateFileSystemEntries(stack.Pop()))
            {
                if (++count > 30000) throw new InvalidDataException("Provisioning tree exceeds its bound.");
                BoundedJson.PhysicalPath(item);
                if (Directory.Exists(item)) stack.Push(item); else yield return item;
            }
    }
}
internal sealed record ProvisioningFile(string Path, string Sha256);
