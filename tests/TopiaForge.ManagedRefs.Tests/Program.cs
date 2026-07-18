using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using TopiaForge.ManagedRefs;

namespace TopiaForge.ManagedRefs.Tests;

internal static class Program
{
    private const string WindowsSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MacSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private static async Task<int> Main()
    {
        var tests = new (string Name, Func<Task> Action)[]
        {
            ("options preserve legacy switches and environment override", TestOptionsAsync),
            ("configuration validation is strict", TestConfigurationValidationAsync),
            ("manifest and source-platform config remain optional outside latest gate", TestOptionalManifestAsync),
            ("manifest validation covers both platforms", TestManifestValidationAsync),
            ("download URLs reject credentials and unsafe paths", TestSafeUrlsAsync),
            ("HTTP transport disables redirects and sends explicit bearer auth", TestHttpPolicyAsync),
            ("managed directory validation enforces PE identity", TestManagedDirectoryIdentityAsync),
            ("managed assembly inventory matches repository compile references", TestManagedAssemblyInventoryAsync),
            ("bundled ZIP extraction is bounded and traversal-safe", TestZipExtractionAsync),
            ("public extraction preserves the 7-Zip selection contract", TestSevenZipExtractionAsync),
            ("cache key output preserves CI contract", TestCacheKeyAsync),
            ("environment cache override wins over runner cache", TestCacheEnvironmentOverrideAsync),
            ("RequireLatest gates bundled probes through both public archives", TestRequireLatestBundledAsync),
            ("auto source falls back with bundled-only authorization", TestAutoFallbackAsync),
            ("valid caches are reused and invalid caches are atomically replaced", TestCacheReuseAndRepairAsync),
            ("SHA mismatch fails before extraction", TestShaMismatchAsync),
            ("partial extraction never publishes a cache entry", TestAtomicFailureAsync),
        };

        var failures = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Action().ConfigureAwait(false);
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception exception)
            {
                failures++;
                Console.Error.WriteLine($"FAIL {test.Name}: {exception}");
            }
        }

        Console.WriteLine($"Managed-reference tool tests: {tests.Length - failures}/{tests.Length} passed.");
        return failures == 0 ? 0 : 1;
    }

    private static Task TestOptionsAsync()
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ROBOTOPIA_REFS_SOURCE"] = "bundled",
        };
        var fromEnvironment = ManagedRefsOptions.Parse(
            Array.Empty<string>(),
            name => environment.GetValueOrDefault(name));
        Assert(fromEnvironment.Source == ManagedRefsSource.Bundled, "Environment source was ignored.");

        var explicitOptions = ManagedRefsOptions.Parse(
            new[]
            {
                "-Source",
                "public",
                "--source-platform=mac",
                "-Probe",
                "--cache-key-only",
                "-WriteLocalProps",
                "--require-latest",
            },
            name => environment.GetValueOrDefault(name));
        Assert(explicitOptions.Source == ManagedRefsSource.Public, "Explicit source did not win.");
        Assert(explicitOptions.SourcePlatform == "mac", "Source platform was not parsed.");
        Assert(
            explicitOptions.Probe && explicitOptions.CacheKeyOnly && explicitOptions.WriteLocalProps &&
            explicitOptions.RequireLatest,
            "Boolean switches were not parsed.");
        AssertThrows<ArgumentException>(
            () => ManagedRefsOptions.Parse(new[] { "--source", "other" }, _ => null),
            "Expected auto, public, or bundled");
        return Task.CompletedTask;
    }

    private static Task TestConfigurationValidationAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.WriteConfiguration(WindowsSha, MacSha);
        var configuration = PublicBuildConfiguration.Load(path);
        Assert(configuration.BuildId == 2227, "Build ID was not loaded.");
        Assert(configuration.Archives.Count == 2, "Archive count was not enforced.");

        var json = File.ReadAllText(path).Replace(
            "\"mac\": {",
            "\"linux\": {}, \"mac\": {",
            StringComparison.Ordinal);
        AssertThrows<InvalidDataException>(
            () => ParseConfiguration(json),
            "exactly windows and mac");
        AssertThrows<InvalidDataException>(
            () => ParseConfiguration(File.ReadAllText(path).Replace(WindowsSha, "abc", StringComparison.Ordinal)),
            "invalid SHA-256");
        AssertThrows<InvalidDataException>(
            () => ParseConfiguration(File.ReadAllText(path).Replace(
                "\"archives\": {",
                "\"unknown\": true, \"archives\": {",
                StringComparison.Ordinal)),
            "unknown property");
        var oversized = Path.Combine(workspace.Root, "oversized.json");
        File.WriteAllText(oversized, new string(' ', 64 * 1024 + 1));
        AssertThrows<InvalidDataException>(() => PublicBuildConfiguration.Load(oversized), "byte limit");
        return Task.CompletedTask;
    }

    private static async Task TestOptionalManifestAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var path = workspace.WriteConfiguration(WindowsSha, MacSha);
        var filtered = File.ReadLines(path)
            .Where(line => !line.Contains("\"manifestUrl\"", StringComparison.Ordinal) &&
                !line.Contains("\"sourcePlatform\"", StringComparison.Ordinal))
            .ToArray();
        File.WriteAllLines(path, filtered);

        var configuration = PublicBuildConfiguration.Load(path);
        Assert(configuration.ManifestUrl.Length == 0, "Missing manifest URL was not preserved.");
        Assert(
            configuration.SelectArchive(string.Empty, string.Empty, path).Platform == "windows",
            "Missing source platform did not default to windows.");

        var http = new FakeHttpTransport();
        using (var restore = CreateRestore(
            workspace,
            CreateOptions(ManagedRefsSource.Public, path, probe: true),
            new Dictionary<string, string>(),
            http,
            new MarkerExtractor()))
        {
            await restore.RunAsync().ConfigureAwait(false);
        }

        Assert(http.Calls.Count == 1 && http.Calls[0].Method == "HEAD", "Manifest-free probe contacted extra endpoints.");

        using var latestRestore = CreateRestore(
            workspace,
            CreateOptions(ManagedRefsSource.Public, path, probe: true, requireLatest: true),
            new Dictionary<string, string>(),
            new FakeHttpTransport(),
            new MarkerExtractor());
        await AssertThrowsAsync<InvalidDataException>(
            () => latestRestore.RunAsync(),
            "must define manifestUrl").ConfigureAwait(false);
    }

    private static Task TestManifestValidationAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var configuration = PublicBuildConfiguration.Load(workspace.WriteConfiguration(WindowsSha, MacSha));
        PublicBuildManifest.Parse(CreateManifestJson()).AssertMatches(configuration);

        AssertThrows<InvalidDataException>(
            () => PublicBuildManifest.Parse(CreateManifestJson(buildId: 2228)).AssertMatches(configuration),
            "reports build 2228");
        AssertThrows<InvalidDataException>(
            () => PublicBuildManifest.Parse(CreateManifestJson(includeMac: false)).AssertMatches(configuration),
            "missing the mac archive");
        AssertThrows<InvalidDataException>(
            () => PublicBuildManifest.Parse(CreateManifestJson(windowsSha: MacSha)).AssertMatches(configuration),
            "windows SHA");
        AssertThrows<InvalidDataException>(
            () => PublicBuildManifest.Parse(CreateManifestJson(macPath: "other.7z")).AssertMatches(configuration),
            "mac path");
        return Task.CompletedTask;
    }

    private static Task TestSafeUrlsAsync()
    {
        var expected = new Uri("https://example.invalid/root/archive.7z");
        Assert(
            SafeHttpsUri.Join("https://example.invalid/root", "archive.7z") == expected,
            "Safe relative URL did not join correctly.");
        AssertThrows<InvalidDataException>(
            () => SafeHttpsUri.Join("https://example.invalid", "../secret"),
            "safe relative");
        AssertThrows<InvalidDataException>(
            () => SafeHttpsUri.ParseAbsolute("https://token@example.invalid/archive", "URL"),
            "credential-free HTTPS");
        AssertThrows<InvalidDataException>(
            () => SafeHttpsUri.ParseAbsolute("https://example.invalid/archive?token=secret", "URL"),
            "credential-free HTTPS");
        AssertThrows<InvalidDataException>(
            () => SafeHttpsUri.ParseAbsolute("http://example.invalid/archive", "URL"),
            "credential-free HTTPS");
        return Task.CompletedTask;
    }

    private static async Task TestHttpPolicyAsync()
    {
        using var handler = HttpTransport.CreateHandler();
        Assert(!handler.AllowAutoRedirect, "HTTP redirects are enabled.");
        Assert(!handler.UseCookies, "Cookies are enabled.");

        var redirectHandler = new StaticResponseHandler(HttpStatusCode.Redirect);
        using var redirectTransport = new HttpTransport(redirectHandler);
        await AssertThrowsAsync<InvalidDataException>(
            () => redirectTransport.HeadAsync(
                new Uri("https://example.invalid/archive"),
                "archive",
                null,
                false,
                TimeSpan.FromSeconds(1),
                CancellationToken.None),
            "HTTP 302").ConfigureAwait(false);

        var authHandler = new StaticResponseHandler(HttpStatusCode.OK);
        using var authTransport = new HttpTransport(authHandler);
        await authTransport.HeadAsync(
            new Uri("https://example.invalid/archive"),
            "archive",
            "secret-token",
            true,
            TimeSpan.FromSeconds(1),
            CancellationToken.None).ConfigureAwait(false);
        Assert(authHandler.AuthorizationParameters.SequenceEqual(new[] { "secret-token" }), "Bearer token was not sent.");

        var oversizedHandler = new StaticResponseHandler(HttpStatusCode.OK, new string('x', 1024 * 1024 + 1));
        using var oversizedTransport = new HttpTransport(oversizedHandler);
        await AssertThrowsAsync<InvalidDataException>(
            () => oversizedTransport.GetStringAsync(
                new Uri("https://example.invalid/manifest"),
                "manifest",
                null,
                false,
                TimeSpan.FromSeconds(1),
                CancellationToken.None),
            "byte limit").ConfigureAwait(false);
    }

    private static Task TestManagedDirectoryIdentityAsync()
    {
        Assert(ManagedDirectoryValidator.RequiredAssemblies.Count == 20, "Production managed-ref inventory is incomplete.");
        using var workspace = new TemporaryWorkspace();
        var destination = Path.Combine(workspace.Root, "managed");
        Directory.CreateDirectory(destination);
        var fixtureName = "Fixture.dll";
        File.Copy(Assembly.GetExecutingAssembly().Location, Path.Combine(destination, fixtureName));
        ManagedDirectoryValidator.Validate(
            destination,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                [fixtureName] = Assembly.GetExecutingAssembly().GetName().Name!,
            });
        AssertThrows<BadImageFormatException>(
            () => ManagedDirectoryValidator.Validate(
                destination,
                new Dictionary<string, string>(StringComparer.Ordinal) { [fixtureName] = "Wrong.Identity" }),
            "expected 'Wrong.Identity'");
        File.WriteAllText(Path.Combine(destination, fixtureName), "not a PE");
        AssertThrows<BadImageFormatException>(
            () => ManagedDirectoryValidator.Validate(
                destination,
                new Dictionary<string, string>(StringComparer.Ordinal) { [fixtureName] = "Fixture" }),
            "valid managed PE");
        return Task.CompletedTask;
    }

    private static Task TestManagedAssemblyInventoryAsync()
    {
        var repositoryRoot = ManagedRefsRestore.ResolveRepositoryRoot(Environment.CurrentDirectory);
        var referencedAssemblies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceRoot in new[] { "src", "mods", "tests" })
        {
            foreach (var project in Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, sourceRoot),
                "*.csproj",
                SearchOption.AllDirectories))
            {
                var document = XDocument.Load(project, LoadOptions.None);
                foreach (var hintPath in document.Descendants()
                    .Where(element => element.Name.LocalName == "Reference")
                    .Select(element => element.Attribute("HintPath")?.Value ??
                        element.Elements().SingleOrDefault(child => child.Name.LocalName == "HintPath")?.Value)
                    .OfType<string>()
                    .Where(value => value.Contains("$(RobotopiaManagedDir)", StringComparison.Ordinal)))
                {
                    referencedAssemblies.Add(Path.GetFileName(hintPath.Replace('\\', '/')));
                }
            }
        }

        Assert(
            referencedAssemblies.SetEquals(ManagedDirectoryValidator.RequiredAssemblies.Keys),
            $"Managed-ref validator inventory differs from compile references. Referenced: {string.Join(", ", referencedAssemblies.OrderBy(value => value, StringComparer.Ordinal))}");
        return Task.CompletedTask;
    }

    private static async Task TestZipExtractionAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var archivePath = Path.Combine(workspace.Root, "refs.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            foreach (var name in MarkerValidator.Names)
            {
                var entry = archive.CreateEntry($"payload/Robotopia_Data/Managed/{name}");
                await using var writer = new StreamWriter(entry.Open());
                await writer.WriteAsync("archive marker").ConfigureAwait(false);
            }
        }

        var destination = Path.Combine(workspace.Root, "managed");
        await new ArchiveExtractor().ExtractBundledAsync(
            archivePath,
            destination,
            CancellationToken.None).ConfigureAwait(false);
        Assert(MarkerValidator.Names.All(name => File.Exists(Path.Combine(destination, name))), "ZIP refs were not copied.");

        var unsafeArchivePath = Path.Combine(workspace.Root, "unsafe.zip");
        using (var archive = ZipFile.Open(unsafeArchivePath, ZipArchiveMode.Create))
        {
            archive.CreateEntry("../escape.txt");
        }

        AssertThrows<InvalidDataException>(
            () => ArchiveExtractor.ExtractZipSafely(
                unsafeArchivePath,
                Path.Combine(workspace.Root, "unsafe-output"),
                CancellationToken.None),
            "unsafe path");
    }

    private static async Task TestSevenZipExtractionAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var process = new FakeProcessRunner();
        var destination = Path.Combine(workspace.Root, "managed");
        await new ArchiveExtractor(process, () => "/synthetic/7z").ExtractPublicAsync(
            Path.Combine(workspace.Root, "source.7z"),
            destination,
            CancellationToken.None).ConfigureAwait(false);
        Assert(process.Arguments.Contains("*/Robotopia_Data/Managed/*"), "Robotopia archive selection pattern is missing.");
        Assert(process.Arguments.Contains("*/Managed/*"), "Fallback Managed selection pattern is missing.");
        Assert(MarkerValidator.Names.All(name => File.Exists(Path.Combine(destination, name))), "7-Zip refs were not copied.");
    }

    private static async Task TestCacheKeyAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var configPath = workspace.WriteConfiguration(WindowsSha, MacSha);
        var githubOutput = Path.Combine(workspace.Root, "github-output.txt");
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["RUNNER_OS"] = "Linux",
            ["GITHUB_OUTPUT"] = githubOutput,
            ["ROBOTOPIA_REFS_SOURCE_PLATFORM"] = "mac",
        };
        using var output = new StringWriter();
        using var restore = CreateRestore(
            workspace,
            CreateOptions(ManagedRefsSource.Public, configPath, cacheKeyOnly: true),
            environment,
            new FakeHttpTransport(),
            new MarkerExtractor(),
            output: output);
        await restore.RunAsync().ConfigureAwait(false);
        var expected = $"robotopia-managed-refs-Linux-public-2227-mac-{MacSha}";
        Assert(output.ToString().Trim() == expected, "Cache key stdout changed.");
        Assert(File.ReadAllText(githubOutput).Trim() == $"key={expected}", "GITHUB_OUTPUT cache key changed.");

        environment["ROBOTOPIA_REFS_URL"] = "https://bundled.example.invalid/refs.zip";
        environment["ROBOTOPIA_REFS_SHA256"] = WindowsSha;
        using var bundledOutput = new StringWriter();
        using (var bundledRestore = CreateRestore(
            workspace,
            CreateOptions(ManagedRefsSource.Bundled, configPath, cacheKeyOnly: true),
            environment,
            new FakeHttpTransport(),
            new MarkerExtractor(),
            output: bundledOutput))
        {
            await bundledRestore.RunAsync().ConfigureAwait(false);
        }

        Assert(
            bundledOutput.ToString().Trim() == $"robotopia-managed-refs-Linux-bundled-{WindowsSha}",
            "Bundled cache key changed.");

        using var autoOutput = new StringWriter();
        using (var autoRestore = CreateRestore(
            workspace,
            CreateOptions(ManagedRefsSource.Auto, configPath, cacheKeyOnly: true),
            environment,
            new FakeHttpTransport(),
            new MarkerExtractor(),
            output: autoOutput))
        {
            await autoRestore.RunAsync().ConfigureAwait(false);
        }

        Assert(
            autoOutput.ToString().Trim() == $"{expected}-fallback-{WindowsSha}",
            "Auto cache key omitted the bundled fallback identity.");
    }

    private static async Task TestCacheEnvironmentOverrideAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var payload = Encoding.UTF8.GetBytes("payload");
        var environment = BundledEnvironment(payload, token: null);
        var environmentCache = Path.Combine(workspace.Root, "environment-cache");
        environment["ROBOTOPIA_REFS_CACHE"] = environmentCache;
        environment["RUNNER_TOOL_CACHE"] = Path.Combine(workspace.Root, "runner-cache");
        using var restore = CreateRestore(
            workspace,
            CreateOptions(
                ManagedRefsSource.Bundled,
                workspace.WriteConfiguration(WindowsSha, MacSha)),
            environment,
            new FakeHttpTransport { DownloadBytes = payload },
            new MarkerExtractor());
        await restore.RunAsync().ConfigureAwait(false);

        var sha = environment["ROBOTOPIA_REFS_SHA256"];
        Assert(
            Directory.Exists(Path.Combine(environmentCache, $"bundled-{sha}", "Managed")),
            "ROBOTOPIA_REFS_CACHE was not used.");
        Assert(!Directory.Exists(environment["RUNNER_TOOL_CACHE"]), "RUNNER_TOOL_CACHE incorrectly won precedence.");
    }

    private static async Task TestRequireLatestBundledAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var configPath = workspace.WriteConfiguration(WindowsSha, MacSha);
        var environment = BundledEnvironment(Array.Empty<byte>(), "secret-token");
        var http = new FakeHttpTransport { ManifestJson = CreateManifestJson() };
        using var restore = CreateRestore(
            workspace,
            CreateOptions(ManagedRefsSource.Bundled, configPath, probe: true, requireLatest: true),
            environment,
            http,
            new MarkerExtractor());
        await restore.RunAsync().ConfigureAwait(false);

        var publicCalls = http.Calls.Where(call => call.Uri.Host == "public.example.invalid").ToArray();
        Assert(publicCalls.Length == 4, "Latest gate did not request the manifest and both archives exactly once.");
        Assert(publicCalls.All(call => call.BearerToken is null), "Bundled token leaked to a public endpoint.");
        var bundledCall = http.Calls.Single(call => call.Uri.Host == "bundled.example.invalid");
        Assert(bundledCall.Method == "HEAD", "Bundled probe did not use HEAD.");
        Assert(bundledCall.BearerToken == "secret-token" && bundledCall.HideUri, "Bundled auth/privacy policy changed.");
    }

    private static async Task TestAutoFallbackAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var payload = Encoding.UTF8.GetBytes("bundled payload");
        var configPath = workspace.WriteConfiguration(WindowsSha, MacSha);
        var environment = BundledEnvironment(payload, "secret-token");
        var githubEnvironment = Path.Combine(workspace.Root, "github-env.txt");
        environment["GITHUB_ENV"] = githubEnvironment;
        var http = new FakeHttpTransport
        {
            DownloadBytes = payload,
            FailPublic = true,
        };
        var extractor = new MarkerExtractor();
        using var restore = CreateRestore(
            workspace,
            CreateOptions(
                ManagedRefsSource.Auto,
                configPath,
                cacheRoot: Path.Combine(workspace.Root, "cache"),
                writeLocalProps: true),
            environment,
            http,
            extractor);
        await restore.RunAsync().ConfigureAwait(false);

        Assert(extractor.PublicCalls == 0 && extractor.BundledCalls == 1, "Auto fallback extracted the wrong source.");
        Assert(http.Calls.Where(call => call.Uri.Host == "public.example.invalid").All(call => call.BearerToken is null),
            "Bundled token leaked during public fallback.");
        Assert(http.Calls.Single(call => call.Method == "DOWNLOAD").BearerToken == "secret-token",
            "Bundled download omitted authorization.");
        Assert(File.ReadAllText(githubEnvironment).Contains("RobotopiaManagedDir=", StringComparison.Ordinal),
            "GITHUB_ENV was not written.");
        var props = File.ReadAllText(Path.Combine(workspace.Root, "Directory.Build.local.props"));
        Assert(props.Contains("<RobotopiaManagedDir>", StringComparison.Ordinal), "Local props were not written.");
        Assert(!Directory.EnumerateDirectories(Path.Combine(workspace.Root, "cache")).Any(
            path => Path.GetFileName(path).Contains("staging", StringComparison.Ordinal)),
            "A staging directory remained after success.");
    }

    private static async Task TestShaMismatchAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var payload = Encoding.UTF8.GetBytes("payload");
        var environment = BundledEnvironment(payload, token: null);
        environment["ROBOTOPIA_REFS_SHA256"] = WindowsSha;
        var extractor = new MarkerExtractor();
        using var restore = CreateRestore(
            workspace,
            CreateOptions(
                ManagedRefsSource.Bundled,
                workspace.WriteConfiguration(WindowsSha, MacSha),
                cacheRoot: Path.Combine(workspace.Root, "cache")),
            environment,
            new FakeHttpTransport { DownloadBytes = payload },
            extractor);
        await AssertThrowsAsync<InvalidDataException>(() => restore.RunAsync(), "SHA-256 mismatch").ConfigureAwait(false);
        Assert(extractor.BundledCalls == 0, "Extraction ran before SHA validation.");
    }

    private static async Task TestCacheReuseAndRepairAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var payload = Encoding.UTF8.GetBytes("payload");
        var environment = BundledEnvironment(payload, token: null);
        var sha = environment["ROBOTOPIA_REFS_SHA256"];
        var cache = Path.Combine(workspace.Root, "cache");
        var options = CreateOptions(
            ManagedRefsSource.Bundled,
            workspace.WriteConfiguration(WindowsSha, MacSha),
            cacheRoot: cache);

        var firstExtractor = new MarkerExtractor();
        using (var firstRestore = CreateRestore(
            workspace,
            options,
            environment,
            new FakeHttpTransport { DownloadBytes = payload },
            firstExtractor))
        {
            await firstRestore.RunAsync().ConfigureAwait(false);
        }

        Assert(firstExtractor.BundledCalls == 1, "Initial cache install did not extract.");
        var secondExtractor = new MarkerExtractor();
        var secondHttp = new FakeHttpTransport { DownloadBytes = payload };
        using (var secondRestore = CreateRestore(
            workspace,
            options,
            environment,
            secondHttp,
            secondExtractor))
        {
            await secondRestore.RunAsync().ConfigureAwait(false);
        }

        Assert(secondExtractor.BundledCalls == 0 && secondHttp.Calls.Count == 0, "Valid cache was not reused offline.");

        var managed = Path.Combine(cache, $"bundled-{sha}", "Managed");
        File.WriteAllText(Path.Combine(managed, MarkerValidator.Names[0]), "corrupt");
        var repairExtractor = new MarkerExtractor();
        using (var repairRestore = CreateRestore(
            workspace,
            options,
            environment,
            new FakeHttpTransport { DownloadBytes = payload },
            repairExtractor))
        {
            await repairRestore.RunAsync().ConfigureAwait(false);
        }

        Assert(repairExtractor.BundledCalls == 1, "Invalid cache was not repaired.");
        new MarkerValidator().Validate(managed);
        Assert(!Directory.EnumerateDirectories(cache).Any(
            path => Path.GetFileName(path).Contains("staging", StringComparison.Ordinal)),
            "Cache repair retained staging state.");
    }

    private static async Task TestAtomicFailureAsync()
    {
        using var workspace = new TemporaryWorkspace();
        var payload = Encoding.UTF8.GetBytes("payload");
        var environment = BundledEnvironment(payload, token: null);
        var sha = environment["ROBOTOPIA_REFS_SHA256"];
        var cache = Path.Combine(workspace.Root, "cache");
        var extractor = new MarkerExtractor { FailAfterPartialWrite = true };
        using var restore = CreateRestore(
            workspace,
            CreateOptions(
                ManagedRefsSource.Bundled,
                workspace.WriteConfiguration(WindowsSha, MacSha),
                cacheRoot: cache),
            environment,
            new FakeHttpTransport { DownloadBytes = payload },
            extractor);
        await AssertThrowsAsync<InvalidDataException>(() => restore.RunAsync(), "partial extraction").ConfigureAwait(false);
        Assert(!Directory.Exists(Path.Combine(cache, $"bundled-{sha}")), "Partial cache entry became visible.");
        Assert(!Directory.EnumerateDirectories(cache).Any(), "Partial staging directory was retained.");
    }

    private static ManagedRefsRestore CreateRestore(
        TemporaryWorkspace workspace,
        ManagedRefsOptions options,
        IReadOnlyDictionary<string, string> environment,
        IHttpTransport http,
        IArchiveExtractor extractor,
        TextWriter? output = null) =>
        new(
            options,
            name => environment.GetValueOrDefault(name),
            workspace.Root,
            workspace.TemporaryDirectory,
            http,
            extractor,
            new MarkerValidator(),
            output ?? TextWriter.Null,
            TextWriter.Null);

    private static ManagedRefsOptions CreateOptions(
        ManagedRefsSource source,
        string configPath,
        string cacheRoot = "",
        bool probe = false,
        bool cacheKeyOnly = false,
        bool writeLocalProps = false,
        bool requireLatest = false) =>
        new(
            source,
            string.Empty,
            configPath,
            cacheRoot,
            probe,
            cacheKeyOnly,
            writeLocalProps,
            requireLatest,
            ShowHelp: false);

    private static Dictionary<string, string> BundledEnvironment(byte[] payload, string? token)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ROBOTOPIA_REFS_URL"] = "https://bundled.example.invalid/refs.zip",
            ["ROBOTOPIA_REFS_SHA256"] = Convert.ToHexStringLower(SHA256.HashData(payload)),
        };
        if (token is not null)
        {
            environment["ROBOTOPIA_REFS_TOKEN"] = token;
        }

        return environment;
    }

    private static string CreateManifestJson(
        int buildId = 2227,
        bool includeMac = true,
        string windowsSha = WindowsSha,
        string macPath = "Robotopia-v02227-Mac.7z")
    {
        var macEntry = includeMac
            ? $$"""
              ,
              "mac": {
                "path": "{{macPath}}",
                "sha256": "{{MacSha}}"
              }
              """
            : string.Empty;
        return $$"""
        {
          "id": {{buildId}},
          "windows": {
            "path": "Robotopia-v02227-Win64.7z",
            "sha256": "{{windowsSha}}"
          }{{macEntry}}
        }
        """;
    }

    private static void ParseConfiguration(string json)
    {
        using var document = JsonDocument.Parse(json);
        _ = PublicBuildConfiguration.Parse(document.RootElement, "test config");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static void AssertThrows<T>(Action action, string expectedMessage)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T exception) when (exception.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name} containing '{expectedMessage}'.");
    }

    private static async Task AssertThrowsAsync<T>(Func<Task> action, string expectedMessage)
        where T : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (T exception) when (exception.Message.Contains(expectedMessage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException($"Expected {typeof(T).Name} containing '{expectedMessage}'.");
    }
}
