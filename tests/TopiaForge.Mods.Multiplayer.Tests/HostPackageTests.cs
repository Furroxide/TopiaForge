extern alias provider;

using System.Net;
using System.Security.Cryptography;
using System.Text;
using TopiaForge.Mods.Internal;
using provider::TopiaForge.Multiplayer.Network;
using static TopiaForge.Mods.Multiplayer.Tests.TransportAssert;

namespace TopiaForge.Mods.Multiplayer.Tests;

internal static class HostPackageTests
{
    internal static void Run()
    {
        HostingRequiresBothLaunchOptions();
        TheAllowlistAndArchiveBytesStayPinned();
        CacheRootsMatchTheLauncher();
        HostPackageEndpointTests.Run();
        Console.WriteLine("Host packages: 7 test groups passed (including all 4 launch-option combinations).");
    }

    private static void HostingRequiresBothLaunchOptions()
    {
        foreach (var developer in new[] { false, true })
            foreach (var serve in new[] { false, true })
            {
                using var files = new PackageFiles();
                var session = new InternalLaunchSession("host-packages", "Sam", false,
                    new InternalLaunchHost("Shared packages", 4, InternalSessionVisibility.Lan, false, false, serve))
                { DeveloperMode = developer };
                var plan = LaunchSessionPlan.From(session, false, out _)!;
                var settings = new HostSettings("Shared packages") { Port = 0 };
                plan.ApplyTo(settings);
                That(settings.DeveloperMode == developer && settings.ServePackages == (developer && serve), "the launch options reach host settings");
                using var hosted = new PackageHost(files, settings);
                var expected = developer && serve;
                That(hosted.Host.Descriptor!.Descriptor.HostServesPackages == expected, "the signed package flag requires developer mode and servePackages");
                That((hosted.Factory.Packages != null) == expected, "only an opted-in developer host installs a package delegate");
                var response = HostPackageEndpointTests.Raw(hosted.Host.Port, files.Sha);
                That(expected ? response.SequenceEqual(files.Answer) : response.SequenceEqual(Encoding.ASCII.GetBytes("TFD1 404\n")),
                    "only an opted-in developer host serves the archive");
                if (!expected) continue;
                settings.DeveloperMode = false;
                That(HostPackageEndpointTests.Raw(hosted.Host.Port, files.Sha).SequenceEqual(Encoding.ASCII.GetBytes("TFD1 404\n")),
                    "developer mode is rechecked for each request after hosting began");
                hosted.Host.UpdateWorld(null, null);
                That(!hosted.Host.Descriptor.Descriptor.HostServesPackages, "a republished descriptor no longer offers packages after developer mode is disabled");
                settings.DeveloperMode = true;
                settings.ServePackages = false;
                That(HostPackageEndpointTests.Raw(hosted.Host.Port, files.Sha).SequenceEqual(Encoding.ASCII.GetBytes("TFD1 404\n")),
                    "withdrawing package sharing also stops new requests");
            }

        // HostFlow must enforce the pair itself, even without the launch reader/planner's defence.
        using var rawFiles = new PackageFiles();
        using var rawHost = new PackageHost(rawFiles, new HostSettings("No developer mode") { Port = 0, ServePackages = true });
        That(!rawHost.Host.Descriptor!.Descriptor.HostServesPackages && rawHost.Factory.Packages == null,
            "HostFlow itself never serves outside developer mode");
    }

    private static void TheAllowlistAndArchiveBytesStayPinned()
    {
        using var files = new PackageFiles();
        using var hosted = new PackageHost(files, new HostSettings("Pinned archives") { Port = 0, DeveloperMode = true, ServePackages = true });
        var bytes = HostPackageEndpointTests.Raw(hosted.Host.Port, files.Sha);
        That(bytes.SequenceEqual(files.Answer), "the C# writer matches the shared package-answer fixture byte for byte");
        var header = Array.IndexOf(bytes, (byte)'\n') + 1;
        That(header <= 64 && bytes.AsSpan(header).SequenceEqual(files.Archive), "the status line fits 64 bytes and the archive is exact");

        var extra = Encoding.ASCII.GetBytes("another archive in the host cache");
        var extraSha = Convert.ToHexString(SHA256.HashData(extra)).ToLowerInvariant();
        File.WriteAllBytes(Path.Combine(files.Root.FullName, extraSha + ".topiaforgemod"), extra);
        var unknown = HostPackageEndpointTests.Raw(hosted.Host.Port, new string('f', 64));
        That(unknown.SequenceEqual(Encoding.ASCII.GetBytes("TFD1 404\n")) &&
             HostPackageEndpointTests.Raw(hosted.Host.Port, extraSha).SequenceEqual(unknown), "an unlisted cached archive and an unknown digest are refused identically");

        // Changing the runtime's content list cannot expand this hosting run's allowlist or signed package list.
        ((List<DescriptorPackage>)hosted.Content.Packages)[0] = new DescriptorPackage("io.example.extra", "1.0.0", extraSha, DescriptorPackageRole.Implicit);
        hosted.Host.UpdateWorld(null, null);
        That(hosted.Host.Descriptor!.Descriptor.Packages.Single().Sha256 == files.Sha &&
             HostPackageEndpointTests.Raw(hosted.Host.Port, extraSha).SequenceEqual(unknown), "the allowlist stays fixed when the world is republished");

        // The size stays the same: a length/mtime check alone cannot prove these bytes.
        var changed = files.Archive.ToArray();
        changed[^1] ^= 1;
        File.WriteAllBytes(files.Path, changed);
        That(HostPackageEndpointTests.Raw(hosted.Host.Port, files.Sha).SequenceEqual(unknown), "an archive changed on disk is not offered");
        hosted.Host.Pump();
        That(hosted.Content.PackageWarnings.Count == 1 && hosted.Content.PackageWarnings[0].Contains("SHA-256", StringComparison.Ordinal),
            "the host explains the changed archive");
        That(!hosted.Content.PackageWarnings[0].Contains(files.Root.FullName, StringComparison.Ordinal) &&
             !hosted.Content.PackageWarnings[0].Contains(IPAddress.Loopback.ToString(), StringComparison.Ordinal), "the warning includes no local path or address");

        // A direct call proves hostile keys never become filesystem paths, even without the wire parser.
        That(hosted.Factory.Packages!("../" + files.Sha, CancellationToken.None) == null, "a request cannot supply a path");
        File.Delete(files.Path);
        using var missingHost = new PackageHost(files, new HostSettings("Missing archive") { Port = 0, DeveloperMode = true, ServePackages = true });
        That(missingHost.Content.PackageWarnings.Single().Contains("missing", StringComparison.Ordinal) &&
             HostPackageEndpointTests.Raw(missingHost.Host.Port, files.Sha).SequenceEqual(unknown), "a missing archive warns at startup and is refused");

        File.WriteAllBytes(files.Path, Array.Empty<byte>());
        var archives = new HostPackageArchives(files.Packages, files.Root.FullName, () => true);
        That(archives.Open(files.Sha, CancellationToken.None) == null, "an empty archive is refused");
        using (var oversized = new FileStream(files.Path, FileMode.Open, FileAccess.Write)) oversized.SetLength(HostPackageArchives.MaximumBytes + 1);
        That(archives.Open(files.Sha, CancellationToken.None) == null, "an oversized archive is refused before hashing");
        File.WriteAllBytes(files.Path, files.Archive);
        using (var held = new FileStream(files.Path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            That(archives.Open(files.Sha, CancellationToken.None) == null, "a locked archive is refused");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Throws<OperationCanceledException>(() => archives.Open(files.Sha, cancelled.Token), "cancelled verification opens nothing");
        using var restored = archives.Open(files.Sha, CancellationToken.None);
        That(restored != null && restored.Position == 0, "a verified archive starts at byte zero");
    }

    private static void CacheRootsMatchTheLauncher()
    {
        using var files = new PackageFiles();
        var values = new Dictionary<string, string>();
        string? Read(string key) => values.GetValueOrDefault(key);
        values["APPDATA"] = files.Root.FullName;
        That(HostPackageArchives.CacheFor(Read, true, files.Root.FullName) == Path.Combine(files.Root.FullName, "TopiaForgeLauncher", "package-cache"), "Windows uses the launcher's roaming root");
        values["HOME"] = files.Root.FullName;
        That(HostPackageArchives.CacheFor(Read, false, files.Root.FullName) == Path.Combine(files.Root.FullName, ".topiaforge_launcher", "package-cache"), "other platforms use the launcher's home root");
        values["TOPIAFORGE_DATA_ROOT"] = "  " + files.Root.FullName + "  ";
        That(HostPackageArchives.CacheFor(Read, true, files.Root.FullName) == Path.Combine(files.Root.FullName, "package-cache"), "a configured root wins and is trimmed");
    }

    internal sealed class PackageFiles : IDisposable
    {
        internal PackageFiles()
        {
            var fixture = NetworkFixtures.Load("package-answer/case.json");
            Archive = Convert.FromBase64String(fixture.Text("archiveBase64"));
            Answer = Convert.FromBase64String(fixture.Text("answerBase64"));
            Sha = fixture.Text("sha256");
            That(Convert.ToHexString(SHA256.HashData(Archive)).ToLowerInvariant() == Sha, "the fixture pins its archive hash");
            Root = Directory.CreateTempSubdirectory("TopiaForgeHostPackages-");
            Path = System.IO.Path.Combine(Root.FullName, Sha + ".topiaforgemod");
            File.WriteAllBytes(Path, Archive);
            Packages = new[] { new DescriptorPackage("io.example.shared", "1.0.0", Sha, DescriptorPackageRole.Implicit) };
        }
        internal DirectoryInfo Root { get; }
        internal string Path { get; }
        internal string Sha { get; }
        internal byte[] Archive { get; }
        internal byte[] Answer { get; }
        internal IReadOnlyList<DescriptorPackage> Packages { get; }
        public void Dispose() => Root.Delete(true);
    }

    private sealed class PackageHost : IDisposable
    {
        private readonly LiveRig rig = new(9121);
        private readonly LocalIdentity identity = LocalIdentity.Create(1_760_000_000);
        internal PackageHost(PackageFiles files, HostSettings settings)
        {
            Content = new TestContent { Packages = new List<DescriptorPackage>(files.Packages) };
            Factory = new PackageNetworkFactory();
            Host = new LiveHost(rig.Host.Engine, identity, new BanList(), settings, Content,
                PackageNetworkFactory.Clock, () => 1_760_000_000, null, packageCache: () => files.Root.FullName);
            That(Host.Start(Factory) == LiveFailure.None, "hosting starts on loopback");
        }
        internal LiveHost Host { get; }
        internal TestContent Content { get; }
        internal PackageNetworkFactory Factory { get; }
        public void Dispose() { Host.Dispose(); identity.Dispose(); rig.Dispose(); }
    }
}
