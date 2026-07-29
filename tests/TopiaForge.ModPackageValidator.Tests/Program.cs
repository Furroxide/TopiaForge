using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using TopiaForge.ModPackageValidator;

namespace TopiaForge.ModPackageValidator.Tests;

internal static class Program
{
    private const string SdkVersion = "1.0.0-rc.1";

    private static int Main()
    {
        Run("exact portable scaffold", TestExactPortableScaffold);
        Run("portable project-reference separators", TestPortableProjectReferenceSeparators);
        Run("escaping project reference and ranged package", TestProjectReferenceAndVersionRange);
        Run("project reference through linked directory", TestLinkedProjectReferenceEscape);
        Run("extraction path leak", TestExtractionPathLeak);
        Run("XML-escaped extraction path leak", TestXmlEscapedExtractionPathLeak);
        Run("unsafe source manifest identity", TestUnsafeSourceManifestIdentity);
        Run("installed package and receipt", TestInstalledPackageAndReceipt);
        Run("tampered installed payload", TestTamperedInstalledPayload);
        Run("unsorted receipt inventory", TestUnsortedReceiptInventory);
        Run("bounded installed versions", TestBoundedInstalledVersions);
        Run("bounded receipt metadata", TestBoundedReceiptMetadata);
        Run("duplicate nested lock metadata", TestDuplicateNestedLockMetadata);
        Run("session package requires canonical multiplayer lock", TestSessionPackageRequiresCanonicalLock);
        Console.WriteLine("Release scaffold validator tests passed.");
        return 0;
    }

    private static void TestExactPortableScaffold(string root)
    {
        var project = WriteProjectFixture(root);
        AssertNoErrors(ReleaseScaffoldValidator.Validate(
            project,
            new[] { Path.Combine(root, "extracted-release") }));
    }

    private static void TestProjectReferenceAndVersionRange(string root)
    {
        var project = WriteProjectFixture(root);
        File.WriteAllText(
            Path.Combine(project, "Example.csproj"),
            "<Project><ItemGroup><ProjectReference Include=\"../../sdk.csproj\" />" +
            "<PackageReference Include=\"TopiaForge.Mods.Abstractions\" " +
            "Version=\"[1.0.0,2.0.0)\" /></ItemGroup></Project>");
        var errors = ReleaseScaffoldValidator.Validate(
            project,
            new[] { Path.Combine(root, "extracted-release") });
        AssertContains(errors, "ProjectReference escapes scaffold");
        AssertContains(errors, "must use exact version");
    }

    private static void TestPortableProjectReferenceSeparators(string root)
    {
        var project = WriteProjectFixture(root);
        var contract = Path.Combine(project, "contracts", "Example.Api", "Example.Api.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(contract)!);
        WriteCSharpProject(contract, "TopiaForge.Mods.Abstractions");
        File.WriteAllText(
            Path.Combine(project, "Example.csproj"),
            "<Project><ItemGroup><ProjectReference Include=\"contracts\\Example.Api\\Example.Api.csproj\" />" +
            "<PackageReference Include=\"TopiaForge.Mods.Abstractions\" Version=\"" + SdkVersion + "\" />" +
            "</ItemGroup></Project>");
        AssertNoErrors(ReleaseScaffoldValidator.Validate(project, Array.Empty<string>()));
    }

    private static void TestExtractionPathLeak(string root)
    {
        var project = WriteProjectFixture(root);
        var extracted = Path.Combine(root, "extracted-release");
        File.WriteAllText(
            Path.Combine(project, "topiaforge.dev.props"),
            "<Project><PropertyGroup><Sdk>" + extracted + "</Sdk></PropertyGroup></Project>");
        AssertContains(
            ReleaseScaffoldValidator.Validate(project, new[] { extracted }),
            "contains forbidden path");
    }

    private static void TestLinkedProjectReferenceEscape(string root)
    {
        var project = WriteProjectFixture(root);
        var outside = Path.Combine(root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "Outside.csproj"), "<Project />");
        var bin = Path.Combine(project, "bin");
        Directory.CreateDirectory(bin);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(bin, "linked"), outside);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        {
            return;
        }

        File.WriteAllText(
            Path.Combine(project, "Example.csproj"),
            "<Project><ItemGroup><ProjectReference Include=\"bin/linked/Outside.csproj\" />" +
            "<PackageReference Include=\"TopiaForge.Mods.Abstractions\" Version=\"" + SdkVersion + "\" />" +
            "</ItemGroup></Project>");
        AssertContains(
            ReleaseScaffoldValidator.Validate(project, Array.Empty<string>()),
            "ProjectReference escapes scaffold");
    }

    private static void TestXmlEscapedExtractionPathLeak(string root)
    {
        var project = WriteProjectFixture(root);
        var extracted = Path.Combine(root, "extracted&release");
        File.WriteAllText(
            Path.Combine(project, "topiaforge.dev.props"),
            "<Project><PropertyGroup><Sdk>" + extracted.Replace("&", "&amp;") +
            "</Sdk></PropertyGroup></Project>");
        AssertContains(
            ReleaseScaffoldValidator.Validate(project, new[] { extracted }),
            "contains forbidden path");
    }

    private static void TestUnsafeSourceManifestIdentity(string root)
    {
        var project = WriteProjectFixture(root);
        var manifestPath = Path.Combine(project, "topiaforge.mod.json");
        var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath)).RootElement;
        WriteJson(manifestPath, new
        {
            schemaVersion = 5,
            name = "../../outside",
            displayName = manifest.GetProperty("displayName").GetString(),
            version = "1.0.0",
            author = new { name = "TopiaForge" },
            entryAssembly = "Example.dll",
            entryType = "Example.Mod",
            supportedGameVersionRange = "*",
            supportedLoaderVersionRange = "*",
            supportedSdkVersionRange = "*",
            apiAssemblies = Array.Empty<string>(),
        });
        var package = Path.Combine(root, "example.topiaforgemod");
        File.WriteAllBytes(package, "fixture"u8.ToArray());
        AssertContains(
            ReleaseScaffoldValidator.Validate(
                project,
                Array.Empty<string>(),
                package,
                Path.Combine(root, "game", "packages")),
            "name must be 2-64 characters");
    }

    private static void TestInstalledPackageAndReceipt(string root)
    {
        var project = WriteProjectFixture(root);
        var install = WriteInstallFixture(project, root);
        AssertNoErrors(ReleaseScaffoldValidator.Validate(
            project,
            new[] { Path.Combine(root, "extracted-release") },
            install.Package,
            install.PackagesRoot));
    }

    private static void TestTamperedInstalledPayload(string root)
    {
        var project = WriteProjectFixture(root);
        var install = WriteInstallFixture(project, root);
        File.WriteAllBytes(Path.Combine(install.InstalledRoot, "Example.dll"), "tampered"u8.ToArray());
        AssertContains(
            ReleaseScaffoldValidator.Validate(
                project,
                new[] { Path.Combine(root, "extracted-release") },
                install.Package,
                install.PackagesRoot),
            "file size changed: Example.dll");
    }

    private static void TestUnsortedReceiptInventory(string root)
    {
        var project = WriteProjectFixture(root);
        var install = WriteInstallFixture(project, root, reverseInventory: true);
        AssertContains(
            ReleaseScaffoldValidator.Validate(
                project,
                Array.Empty<string>(),
                install.Package,
                install.PackagesRoot),
            "file inventory is not sorted");
    }

    private static void TestBoundedInstalledVersions(string root)
    {
        var project = WriteProjectFixture(root);
        var install = WriteInstallFixture(project, root);
        for (var index = 0; index < 256; index++)
        {
            Directory.CreateDirectory(Path.Combine(install.PackagesRoot, "example.scaffold", "extra-" + index));
        }

        AssertContains(
            ReleaseScaffoldValidator.Validate(
                project,
                Array.Empty<string>(),
                install.Package,
                install.PackagesRoot),
            "version-entry limit");
    }

    private static void TestBoundedReceiptMetadata(string root)
    {
        var project = WriteProjectFixture(root);
        var install = WriteInstallFixture(project, root);
        File.WriteAllBytes(
            Path.Combine(install.InstalledRoot, "topiaforge.install.json"),
            new byte[(4 * 1024 * 1024) + 1]);
        AssertContains(
            ReleaseScaffoldValidator.Validate(
                project,
                Array.Empty<string>(),
                install.Package,
                install.PackagesRoot),
            "exceeds the 4 MiB metadata limit");
    }

    private static void TestDuplicateNestedLockMetadata(string root)
    {
        var project = WriteProjectFixture(root);
        File.WriteAllText(
            Path.Combine(project, "packages.lock.json"),
            "{\"version\":1,\"dependencies\":{\"netstandard2.1\":{" +
            "\"TopiaForge.Mods.Abstractions\":{\"resolved\":\"0.9.0\",\"resolved\":\"1.0.0\"}}}}");
        AssertContains(
            ReleaseScaffoldValidator.Validate(project, Array.Empty<string>()),
            "duplicate JSON field 'resolved'");
    }

    private static void TestSessionPackageRequiresCanonicalLock(string root)
    {
        const string lockPath = "topiaforge.multiplayer.lock.json";
        var packageRoot = Path.Combine(root, "handcrafted-session-package");
        var contentRoot = Path.Combine(packageRoot, "Content");
        Directory.CreateDirectory(contentRoot);
        var synchronizedContent = "{\"difficulty\":8}"u8.ToArray();
        var contractLock =
            "{\"schemaVersion\":2,\"protocolVersion\":\"1.0.0\",\"contracts\":[]}"u8.ToArray();
        File.WriteAllBytes(Path.Combine(contentRoot, "gameplay-rules.json"), synchronizedContent);
        File.WriteAllBytes(Path.Combine(packageRoot, lockPath), contractLock);
        File.WriteAllBytes(Path.Combine(packageRoot, "Example.dll"), "managed fixture"u8.ToArray());
        WriteJson(Path.Combine(packageRoot, "topiaforge.mod.json"), new
        {
            schemaVersion = 5,
            name = "example.handcrafted-session",
            displayName = "Handcrafted session",
            version = "1.0.0",
            author = new { name = "TopiaForge Tests" },
            entryAssembly = "Example.dll",
            entryType = "Example.Mod",
            supportedGameVersionRange = "*",
            supportedLoaderVersionRange = "*",
            supportedSdkVersionRange = "*",
            hashes = new Dictionary<string, string>
            {
                ["Content/gameplay-rules.json"] = Sha256(synchronizedContent),
                [lockPath] = Sha256(contractLock),
            },
            multiplayer = new
            {
                mode = "session",
                presence = "required",
                protocol = new { version = "1.0.0" },
                synchronizedFiles = new[] { "Content/gameplay-rules.json" },
            },
        });

        var validatorProgram = typeof(ReleaseScaffoldValidator).Assembly.GetType(
            "TopiaForge.ModPackageValidator.Program",
            throwOnError: true)!;
        var validatorMain = validatorProgram.GetMethod(
            "Main",
            BindingFlags.Public | BindingFlags.Static) ??
            throw new InvalidOperationException("Could not locate the package validator entry point.");
        var previousError = Console.Error;
        using var error = new StringWriter();
        int exitCode;
        try
        {
            Console.SetError(error);
            exitCode = (int)(validatorMain.Invoke(null, new object[] { new[] { packageRoot } }) ?? -1);
        }
        finally
        {
            Console.SetError(previousError);
        }

        if (exitCode != 1)
        {
            throw new InvalidOperationException(
                "Package validator accepted a session package with an undeclared contract lock.");
        }
        AssertContains(
            error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries),
            "canonical generated contract lock '" + lockPath + "'");
    }

    private static string WriteProjectFixture(string root)
    {
        var project = Path.Combine(root, "project");
        var tests = Path.Combine(project, "tests", "Example.Tests");
        Directory.CreateDirectory(tests);
        WriteJson(Path.Combine(project, "global.json"), new
        {
            sdk = new { version = "10.0.301", rollForward = "disable" },
        });
        WriteJson(Path.Combine(project, "topiaforge.sdk.lock.json"), new
        {
            schemaVersion = 1,
            sdkVersion = SdkVersion,
            dotnetSdkVersion = "10.0.301",
            manifestSha256 = new string('a', 64),
        });
        File.WriteAllText(Path.Combine(project, "topiaforge.dev.props"), "<Project />");
        WriteJson(Path.Combine(project, "topiaforge.mod.json"), new
        {
            schemaVersion = 5,
            name = "example.scaffold",
            displayName = "Example scaffold",
            version = "1.0.0",
            author = new { name = "TopiaForge" },
            entryAssembly = "Example.dll",
            entryType = "Example.Mod",
            supportedGameVersionRange = "*",
            supportedLoaderVersionRange = "*",
            supportedSdkVersionRange = "*",
            apiAssemblies = Array.Empty<string>(),
        });
        WriteCSharpProject(
            Path.Combine(project, "Example.csproj"),
            "TopiaForge.Mods.Abstractions");
        WriteCSharpProject(
            Path.Combine(tests, "Example.Tests.csproj"),
            "TopiaForge.Mods.Testing");
        return project;
    }

    private static void WriteCSharpProject(string path, string package)
    {
        File.WriteAllText(
            path,
            "<Project><ItemGroup><PackageReference Include=\"" + package +
            "\" Version=\"" + SdkVersion + "\" /></ItemGroup></Project>");
        WriteJson(Path.Combine(Path.GetDirectoryName(path)!, "packages.lock.json"), new
        {
            version = 1,
            dependencies = new Dictionary<string, object>
            {
                ["netstandard2.1"] = new Dictionary<string, object>
                {
                    [package] = new { resolved = SdkVersion },
                },
            },
        });
    }

    private static InstallFixture WriteInstallFixture(
        string project,
        string root,
        bool reverseInventory = false)
    {
        var package = Path.Combine(root, "example.scaffold-1.0.0.topiaforgemod");
        File.WriteAllBytes(package, "deterministic package fixture"u8.ToArray());
        var packagesRoot = Path.Combine(root, "game", "BepInEx", "TopiaForge", "packages");
        var installedRoot = Path.Combine(packagesRoot, "example.scaffold", "1.0.0");
        Directory.CreateDirectory(installedRoot);
        var manifest = File.ReadAllBytes(Path.Combine(project, "topiaforge.mod.json"));
        var entry = "managed entry fixture"u8.ToArray();
        File.WriteAllBytes(Path.Combine(installedRoot, "topiaforge.mod.json"), manifest);
        File.WriteAllBytes(Path.Combine(installedRoot, "Example.dll"), entry);
        var inventory = new List<object>
        {
            new
            {
                path = "Example.dll",
                length = entry.Length,
                sha256 = Sha256(entry),
                critical = true,
            },
            new
            {
                path = "topiaforge.mod.json",
                length = manifest.Length,
                sha256 = Sha256(manifest),
                critical = true,
            },
        };
        if (reverseInventory)
        {
            inventory.Reverse();
        }

        WriteJson(Path.Combine(installedRoot, "topiaforge.install.json"), new
        {
            schemaVersion = 2,
            modId = "example.scaffold",
            version = "1.0.0",
            sourceFile = Path.GetFileName(package),
            source = "local",
            sourceSha256 = Sha256(File.ReadAllBytes(package)),
            installedAtUtc = "2026-07-15T12:00:00Z",
            validatorVersion = "1",
            trust = "local-unverified",
            files = inventory,
        });
        WriteJson(Path.Combine(packagesRoot, "..", "state.json"), new
        {
            mods = new[]
            {
                new
                {
                    id = "example.scaffold",
                    version = "1.0.0",
                    enabled = true,
                    restartRequired = true,
                    uninstallPending = false,
                },
            },
        });
        return new InstallFixture(package, packagesRoot, installedRoot);
    }

    private static void WriteJson(string path, object value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value));
    }

    private static string Sha256(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static void Run(string name, Action<string> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "TopiaForgeScaffoldValidatorTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            test(root);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("Release scaffold validator test failed (" + name + "): " + exception.Message, exception);
        }
        finally
        {
            try
            {
                Directory.Delete(root, recursive: true);
            }
            catch
            {
                // A failed best-effort test cleanup must not hide the assertion result.
            }
        }
    }

    private static void AssertNoErrors(IReadOnlyList<string> errors)
    {
        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Unexpected errors: " + string.Join(" | ", errors));
        }
    }

    private static void AssertContains(IEnumerable<string> errors, string expected)
    {
        if (!errors.Any(error => error.Contains(expected, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                "Expected an error containing '" + expected + "', got: " + string.Join(" | ", errors));
        }
    }

    private sealed record InstallFixture(string Package, string PackagesRoot, string InstalledRoot);
}
