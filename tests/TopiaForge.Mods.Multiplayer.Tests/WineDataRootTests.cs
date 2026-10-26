extern alias provider;

using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class WineDataRootTests
{
    internal static void Run()
    {
        using var files = new HostPackageTests.PackageFiles();
        var unixRoot = "/" + string.Join("/", new[] { "data", "Sam", "shared maps é" });
        var mappedRoot = OperatingSystem.IsWindows() ? files.Root.FullName : "Q:" + "\\cache";
        var values = new Dictionary<string, string>
        {
            ["TOPIAFORGE_DATA_ROOT"] = unixRoot,
            ["APPDATA"] = files.Root.FullName,
        };
        string? Read(string key) => values.GetValueOrDefault(key);
        var calls = 0;
        string? Translate(string value)
        {
            calls++;
            That(value == unixRoot, "Wine receives the exact Unicode Unix root");
            return mappedRoot;
        }
        var cache = HostPackageArchives.CacheFor(Read, true, files.Root.FullName, Translate);
        That(cache == Path.GetFullPath(Path.Combine(files.Root.FullName, mappedRoot, "package-cache")) && calls == 1,
            "a Unix launch root uses the active prefix's mapping exactly once, before composing the cache");
        foreach (var failed in new string?[] { null, "", unixRoot, "relative", "Q:relative", "Q:\\bad\0root" })
            That(HostPackageArchives.CacheFor(Read, true, files.Root.FullName, _ => failed) == null,
                "failed or nonabsolute conversion never falls back to APPDATA or the Windows current drive");
        var native = HostPackageArchives.CacheFor(Read, false, files.Root.FullName, _ => throw new Exception("must not convert"));
        That(native == Path.GetFullPath(Path.Combine(files.Root.FullName, unixRoot, "package-cache")), "a native Unix process keeps its root");
        values["TOPIAFORGE_DATA_ROOT"] = mappedRoot;
        That(HostPackageArchives.CacheFor(Read, true, files.Root.FullName, _ => throw new Exception("must not convert")) == cache,
            "a Windows path is never sent to Wine");
        values.Remove("TOPIAFORGE_DATA_ROOT");
        That(HostPackageArchives.CacheFor(Read, true, files.Root.FullName, _ => throw new Exception("must not convert")) ==
            Path.Combine(files.Root.FullName, "TopiaForgeLauncher", "package-cache"), "a game launched without a launcher keeps the roaming fallback");
        That(WineDataRoot.Translate(unixRoot + '\0') == null, "an embedded terminator never reaches native conversion");
        if (OperatingSystem.IsWindows() && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WINEPREFIX")))
            That(WineDataRoot.Translate(unixRoot) == null, "native Windows has no Wine conversion export and fails closed");

        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(cache!);
            File.Copy(files.Path, Path.Combine(cache!, files.Sha + ".topiaforgemod"));
            var archives = new HostPackageArchives(files.Packages, cache, () => true);
            using var archive = archives.Open(files.Sha, CancellationToken.None);
            That(archive != null, "the resolved launcher cache serves its pinned archive, not the prefix's roaming cache");
        }
        Console.WriteLine("Wine data root: 8 boundary checks passed (native conversion and mapped archive also checked on Windows).");
    }
}
