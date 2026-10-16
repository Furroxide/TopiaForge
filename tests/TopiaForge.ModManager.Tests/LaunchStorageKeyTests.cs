using System;
using System.IO;
using System.Text.Json;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModManager.Tests
{
    internal static class LaunchStorageKeyTests
    {
        internal static void Run(string repoRoot)
        {
            using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(repoRoot, "tests", "fixtures", "launch-storage-keys.json")));
            if (json.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw new InvalidDataException("Unknown storage fixture schema.");
            var count = 0;
            foreach (var item in json.RootElement.GetProperty("cases").EnumerateArray())
            {
                string key;
                string prefix;
                switch (item.GetProperty("kind").GetString())
                {
                    case "request":
                        key = LaunchStorageKeys.Request(item.GetProperty("requestId").GetString()!);
                        prefix = "launch-profile-";
                        break;
                    case "observation":
                        var producer = item.GetProperty("producer");
                        key = LaunchStorageKeys.Observation(item.GetProperty("profileId").GetString()!, item.GetProperty("profileRevision").GetInt32(),
                            new PackageIdentity(producer.GetProperty("id").GetString()!, producer.GetProperty("version").GetString()!),
                            item.GetProperty("packageSetDigest").GetString()!);
                        prefix = "runtime-observation-";
                        break;
                    default: throw new InvalidDataException("Unknown storage fixture channel.");
                }
                if (key != item.GetProperty("key").GetString() || prefix + key + ".json" != item.GetProperty("filename").GetString())
                    throw new InvalidDataException("C# storage key disagrees with the shared fixture at index " + count + ".");
                count++;
            }
            if (count == 0) throw new InvalidDataException("The storage fixture corpus must not be empty.");
            Console.WriteLine("Launch storage keys: " + count + " shared vectors passed.");
        }
    }
}
