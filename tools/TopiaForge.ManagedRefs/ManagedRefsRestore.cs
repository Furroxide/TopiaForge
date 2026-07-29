using System;
using System.Globalization;
using System.IO;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.ManagedRefs;

internal sealed partial class ManagedRefsRestore : IDisposable
{
    private readonly ManagedRefsOptions options;
    private readonly Func<string, string?> getEnvironmentVariable;
    private readonly string currentDirectory;
    private readonly string temporaryDirectory;
    private readonly IHttpTransport http;
    private readonly IArchiveExtractor extractor;
    private readonly IManagedDirectoryValidator validator;
    private readonly TextWriter output;
    private readonly TextWriter errors;
    private readonly IDisposable? ownedHttp;
    private PublicBuildConfiguration? publicConfiguration;
    private string? resolvedConfigPath;
    private bool publicLatestGateComplete;

    internal ManagedRefsRestore(ManagedRefsOptions options)
        : this(
            options,
            Environment.GetEnvironmentVariable,
            Environment.CurrentDirectory,
            Path.GetTempPath(),
            null,
            null,
            null,
            Console.Out,
            Console.Error)
    {
    }

    internal ManagedRefsRestore(
        ManagedRefsOptions options,
        Func<string, string?> getEnvironmentVariable,
        string currentDirectory,
        string temporaryDirectory,
        IHttpTransport? http,
        IArchiveExtractor? extractor,
        IManagedDirectoryValidator? validator,
        TextWriter output,
        TextWriter errors)
    {
        this.options = options;
        this.getEnvironmentVariable = getEnvironmentVariable;
        this.currentDirectory = Path.GetFullPath(currentDirectory);
        this.temporaryDirectory = Path.GetFullPath(temporaryDirectory);
        if (http is null)
        {
            var transport = new HttpTransport();
            this.http = transport;
            ownedHttp = transport;
        }
        else
        {
            this.http = http;
        }

        this.extractor = extractor ?? new ArchiveExtractor();
        this.validator = validator ?? new ManagedDirectoryValidator();
        this.output = output;
        this.errors = errors;
    }

    internal async Task RunAsync(CancellationToken cancellationToken = default)
    {
        // This is deliberately an online public gate for every source, including bundled.
        if (options.RequireLatest && !publicLatestGateComplete)
        {
            await ProbePublicAsync(cancellationToken).ConfigureAwait(false);
        }

        if (options.CacheKeyOnly)
        {
            WriteCacheKey();
            return;
        }

        switch (options.Source)
        {
            case ManagedRefsSource.Public:
                await RestorePublicAsync(cancellationToken).ConfigureAwait(false);
                break;
            case ManagedRefsSource.Bundled:
                await RestoreBundledAsync(cancellationToken).ConfigureAwait(false);
                break;
            case ManagedRefsSource.Auto:
                try
                {
                    await RestorePublicAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    WriteWarning($"Public Robotopia refs failed: {exception.Message}");
                    if (!BundledRefsConfigured())
                    {
                        throw new InvalidOperationException(
                            $"Public Robotopia refs failed and bundled refs are not configured. {exception.Message}",
                            exception);
                    }

                    WriteWarning("Falling back to bundled Robotopia refs.");
                    await RestoreBundledAsync(cancellationToken).ConfigureAwait(false);
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported managed-reference source: {options.Source}");
        }
    }

    public void Dispose() => ownedHttp?.Dispose();

    internal static string ResolveRepositoryRoot(string startDirectory)
    {
        var current = new DirectoryInfo(Path.GetFullPath(startDirectory));
        while (current is not null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")) ||
                File.Exists(Path.Combine(current.FullName, ".git")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root from {Path.GetFullPath(startDirectory)}.");
    }

    private async Task RestorePublicAsync(CancellationToken cancellationToken)
    {
        if (options.Probe)
        {
            if (!options.RequireLatest || !publicLatestGateComplete)
            {
                await ProbePublicAsync(cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        var configuration = GetPublicConfiguration();
        var archive = GetSelectedArchive(configuration);
        if (options.RequireLatest && !publicLatestGateComplete)
        {
            await ProbePublicAsync(cancellationToken).ConfigureAwait(false);
        }

        var cacheEntryName = $"public-{configuration.BuildId}-{archive.Platform}-{archive.Sha256}";
        var cacheEntry = Path.Combine(GetCacheRoot(), cacheEntryName);
        var managedDestination = Path.Combine(cacheEntry, "Managed");
        if (validator.IsValid(managedDestination, out _))
        {
            output.WriteLine($"Using cached Robotopia public managed refs for build {configuration.BuildId}.");
            WriteManagedEnvironment(managedDestination);
            return;
        }

        if (!options.RequireLatest)
        {
            await ProbePublicAsync(cancellationToken).ConfigureAwait(false);
        }

        Directory.CreateDirectory(temporaryDirectory);
        var downloadPath = Path.Combine(
            temporaryDirectory,
            $"robotopia-public-refs-{Guid.NewGuid():N}.7z");
        var archiveUri = SafeHttpsUri.Join(configuration.BaseUrl, archive.Path);
        try
        {
            output.WriteLine(
                $"Downloading Robotopia build {configuration.BuildId} refs source from {archiveUri}");
            await http.DownloadAsync(
                archiveUri,
                downloadPath,
                $"Robotopia {archive.Platform} archive download",
                bearerToken: null,
                hideUri: false,
                TimeSpan.FromMinutes(30),
                cancellationToken).ConfigureAwait(false);
            await AssertSha256Async(downloadPath, archive.Sha256, cancellationToken).ConfigureAwait(false);
            await InstallCacheEntryAsync(
                cacheEntryName,
                async (destination, token) =>
                {
                    await extractor.ExtractPublicAsync(downloadPath, destination, token).ConfigureAwait(false);
                    await AssertSha256Async(downloadPath, archive.Sha256, token).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(downloadPath);
        }

        WriteManagedEnvironment(managedDestination);
    }

    private async Task RestoreBundledAsync(CancellationToken cancellationToken)
    {
        var configuration = GetBundledConfiguration();
        var token = EmptyToNull(getEnvironmentVariable("ROBOTOPIA_REFS_TOKEN"));

        if (options.Probe)
        {
            await http.HeadAsync(
                configuration.Uri,
                "bundled Robotopia refs HEAD",
                token,
                hideUri: true,
                TimeSpan.FromMinutes(1),
                cancellationToken).ConfigureAwait(false);
            output.WriteLine("Bundled Robotopia refs probe succeeded.");
            return;
        }

        var cacheEntryName = $"bundled-{configuration.Sha256}";
        var cacheEntry = Path.Combine(GetCacheRoot(), cacheEntryName);
        var managedDestination = Path.Combine(cacheEntry, "Managed");
        if (validator.IsValid(managedDestination, out _))
        {
            output.WriteLine("Using cached bundled Robotopia managed refs.");
            WriteManagedEnvironment(managedDestination);
            return;
        }

        Directory.CreateDirectory(temporaryDirectory);
        var downloadPath = Path.Combine(
            temporaryDirectory,
            $"robotopia-bundled-refs-{Guid.NewGuid():N}.zip");
        try
        {
            try
            {
                await http.DownloadAsync(
                    configuration.Uri,
                    downloadPath,
                    "Bundled Robotopia refs download",
                    token,
                    hideUri: true,
                    TimeSpan.FromMinutes(10),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new InvalidDataException("Bundled Robotopia refs download failed.", exception);
            }

            await AssertSha256Async(downloadPath, configuration.Sha256, cancellationToken).ConfigureAwait(false);
            await InstallCacheEntryAsync(
                cacheEntryName,
                async (destination, cancellation) =>
                {
                    await extractor.ExtractBundledAsync(downloadPath, destination, cancellation).ConfigureAwait(false);
                    await AssertSha256Async(downloadPath, configuration.Sha256, cancellation).ConfigureAwait(false);
                },
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteFile(downloadPath);
        }

        WriteManagedEnvironment(managedDestination);
    }

    private async Task ProbePublicAsync(CancellationToken cancellationToken)
    {
        var configuration = GetPublicConfiguration();
        var selectedArchive = GetSelectedArchive(configuration);
        PublicArchive[] archivesToProbe;
        if (!string.IsNullOrWhiteSpace(configuration.ManifestUrl))
        {
            var manifestUri = SafeHttpsUri.ParseAbsolute(
                configuration.ManifestUrl,
                "Robotopia build manifest URL");
            await http.HeadAsync(
                manifestUri,
                "Robotopia build manifest HEAD",
                bearerToken: null,
                hideUri: false,
                TimeSpan.FromMinutes(1),
                cancellationToken).ConfigureAwait(false);
            var manifestJson = await http.GetStringAsync(
                manifestUri,
                "Robotopia build manifest GET",
                bearerToken: null,
                hideUri: false,
                TimeSpan.FromMinutes(1),
                cancellationToken).ConfigureAwait(false);
            var manifest = PublicBuildManifest.Parse(manifestJson);

            if (options.RequireLatest)
            {
                manifest.AssertMatches(configuration);
                archivesToProbe = new[]
                {
                    configuration.Archives["windows"],
                    configuration.Archives["mac"],
                };
            }
            else if (manifest.BuildId == configuration.BuildId)
            {
                manifest.AssertSelectedArchiveMatches(selectedArchive);
                archivesToProbe = new[] { selectedArchive };
            }
            else
            {
                WriteWarning(
                    $"Latest manifest reports build {manifest.BuildId}, while this checkout is pinned to build {configuration.BuildId}.");
                archivesToProbe = new[] { selectedArchive };
            }
        }
        else
        {
            if (options.RequireLatest)
            {
                throw new InvalidDataException(
                    "Robotopia game build config must define manifestUrl when --require-latest is used.");
            }

            archivesToProbe = new[] { selectedArchive };
        }

        foreach (var archive in archivesToProbe)
        {
            var uri = SafeHttpsUri.Join(configuration.BaseUrl, archive.Path);
            var result = await http.HeadAsync(
                uri,
                $"Robotopia {archive.Platform} archive HEAD",
                bearerToken: null,
                hideUri: false,
                TimeSpan.FromMinutes(1),
                cancellationToken).ConfigureAwait(false);
            output.WriteLine(
                $"Robotopia public refs probe succeeded: build {configuration.BuildId}, {archive.Platform}, {uri}");
            if (result.ContentLength.HasValue)
            {
                output.WriteLine(
                    $"{archive.Platform} archive content length: {result.ContentLength.Value.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        if (options.RequireLatest)
        {
            publicLatestGateComplete = true;
        }
    }

    private async Task InstallCacheEntryAsync(
        string cacheEntryName,
        Func<string, CancellationToken, Task> extract,
        CancellationToken cancellationToken)
    {
        var cacheRoot = GetCacheRoot();
        Directory.CreateDirectory(cacheRoot);
        await using var cacheLock = await CacheEntryLock.AcquireAsync(
            Path.Combine(cacheRoot, $".{cacheEntryName}.lock"),
            cancellationToken).ConfigureAwait(false);

        var cacheEntry = Path.Combine(cacheRoot, cacheEntryName);
        var managedDestination = Path.Combine(cacheEntry, "Managed");
        if (validator.IsValid(managedDestination, out _))
        {
            return;
        }

        if (Directory.Exists(cacheEntry))
        {
            if (!validator.IsValid(managedDestination, out var error))
            {
                WriteWarning($"Replacing invalid managed-reference cache entry '{cacheEntry}': {error}");
            }

            PathSafety.DeleteDirectoryIfSafe(cacheEntry);
        }

        var stagingEntry = Path.Combine(cacheRoot, $".{cacheEntryName}.staging-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingEntry);
            var stagingManaged = Path.Combine(stagingEntry, "Managed");
            await extract(stagingManaged, cancellationToken).ConfigureAwait(false);
            validator.Validate(stagingManaged);
            Directory.Move(stagingEntry, cacheEntry);
        }
        finally
        {
            if (Directory.Exists(stagingEntry))
            {
                PathSafety.DeleteDirectoryIfSafe(stagingEntry);
            }
        }
    }

    private PublicBuildConfiguration GetPublicConfiguration()
    {
        if (publicConfiguration is not null)
        {
            return publicConfiguration;
        }

        resolvedConfigPath = string.IsNullOrWhiteSpace(options.ConfigPath)
            ? Path.Combine(ResolveRepositoryRoot(currentDirectory), ".github", "robotopia-game-build.json")
            : Path.GetFullPath(options.ConfigPath, currentDirectory);
        publicConfiguration = PublicBuildConfiguration.Load(resolvedConfigPath);
        return publicConfiguration;
    }

    private PublicArchive GetSelectedArchive(PublicBuildConfiguration configuration) =>
        configuration.SelectArchive(
            options.SourcePlatform,
            getEnvironmentVariable("ROBOTOPIA_REFS_SOURCE_PLATFORM") ?? string.Empty,
            resolvedConfigPath ?? options.ConfigPath);

    private BundledConfiguration GetBundledConfiguration()
    {
        var url = EmptyToNull(getEnvironmentVariable("ROBOTOPIA_REFS_URL"));
        var sha256 = EmptyToNull(getEnvironmentVariable("ROBOTOPIA_REFS_SHA256"))?.ToLowerInvariant();
        if (url is null || sha256 is null)
        {
            throw new InvalidDataException(
                "Bundled Robotopia refs require ROBOTOPIA_REFS_URL and ROBOTOPIA_REFS_SHA256.");
        }

        Sha256Value.Validate(
            sha256,
            "ROBOTOPIA_REFS_SHA256 must be exactly 64 hexadecimal characters.");
        return new BundledConfiguration(
            SafeHttpsUri.ParseAbsolute(url, "Bundled Robotopia refs URL"),
            sha256);
    }

    private bool BundledRefsConfigured() =>
        !string.IsNullOrWhiteSpace(getEnvironmentVariable("ROBOTOPIA_REFS_URL")) &&
        !string.IsNullOrWhiteSpace(getEnvironmentVariable("ROBOTOPIA_REFS_SHA256"));

    private string GetCacheRoot()
    {
        string root;
        if (!string.IsNullOrWhiteSpace(options.CacheRoot))
        {
            root = options.CacheRoot;
        }
        else if (!string.IsNullOrWhiteSpace(getEnvironmentVariable("ROBOTOPIA_REFS_CACHE")))
        {
            root = getEnvironmentVariable("ROBOTOPIA_REFS_CACHE")!;
        }
        else if (!string.IsNullOrWhiteSpace(getEnvironmentVariable("RUNNER_TOOL_CACHE")))
        {
            root = Path.Combine(getEnvironmentVariable("RUNNER_TOOL_CACHE")!, "robotopia-managed-refs");
        }
        else
        {
            root = Path.Combine(temporaryDirectory, "robotopia-managed-refs");
        }

        return Path.GetFullPath(root.Trim(), currentDirectory);
    }

    private void WriteCacheKey()
    {
        var runnerOs = (getEnvironmentVariable("RUNNER_OS") ?? string.Empty).Trim();
        if (!SafeCacheSegmentRegex().IsMatch(runnerOs))
        {
            throw new InvalidDataException("RUNNER_OS contains characters that are unsafe in a cache key.");
        }

        string key;
        switch (options.Source)
        {
            case ManagedRefsSource.Public:
                key = GetPublicCacheKey(runnerOs);
                break;
            case ManagedRefsSource.Bundled:
                var bundled = GetBundledConfiguration();
                key = $"robotopia-managed-refs-{runnerOs}-bundled-{bundled.Sha256}";
                break;
            case ManagedRefsSource.Auto:
                key = GetPublicCacheKey(runnerOs);
                var fallbackSha = (getEnvironmentVariable("ROBOTOPIA_REFS_SHA256") ?? string.Empty)
                    .Trim()
                    .ToLowerInvariant();
                if (Sha256Value.IsValid(fallbackSha))
                {
                    key = $"{key}-fallback-{fallbackSha}";
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported managed-reference source: {options.Source}");
        }

        AppendGitHubFile("GITHUB_OUTPUT", $"key={key}");
        output.WriteLine(key);
    }

    private string GetPublicCacheKey(string runnerOs)
    {
        var configuration = GetPublicConfiguration();
        var archive = GetSelectedArchive(configuration);
        return $"robotopia-managed-refs-{runnerOs}-public-{configuration.BuildId}-{archive.Platform}-{archive.Sha256}";
    }

    private void WriteManagedEnvironment(string managedDirectory)
    {
        validator.Validate(managedDirectory);
        var resolved = Path.GetFullPath(managedDirectory);
        if (resolved.Contains('\r') || resolved.Contains('\n'))
        {
            throw new InvalidDataException("Managed-reference path cannot contain a newline.");
        }

        AppendGitHubFile("GITHUB_ENV", $"RobotopiaManagedDir={resolved}");
        if (options.WriteLocalProps)
        {
            var repositoryRoot = ResolveRepositoryRoot(currentDirectory);
            var propsPath = Path.Combine(repositoryRoot, "Directory.Build.local.props");
            var escaped = SecurityElement.Escape(resolved) ??
                throw new InvalidOperationException("Could not XML-escape the managed-reference path.");
            var contents = $"""
                <Project>
                  <PropertyGroup>
                    <RobotopiaManagedDir>{escaped}</RobotopiaManagedDir>
                  </PropertyGroup>
                </Project>
                """ + Environment.NewLine;
            WriteFileAtomically(propsPath, contents);
            output.WriteLine($"Wrote local MSBuild references: {propsPath}");
        }

        output.WriteLine($"RobotopiaManagedDir={resolved}");
    }

    private void AppendGitHubFile(string environmentVariable, string line)
    {
        var path = EmptyToNull(getEnvironmentVariable(environmentVariable));
        if (path is null)
        {
            return;
        }

        if (line.Contains('\r') || line.Contains('\n'))
        {
            throw new InvalidDataException($"Unsafe newline in {environmentVariable} output.");
        }

        File.AppendAllText(Path.GetFullPath(path, currentDirectory), line + Environment.NewLine, new UTF8Encoding(false));
    }

    private static void WriteFileAtomically(string destinationPath, string contents)
    {
        var temporaryPath = $"{destinationPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            File.WriteAllText(temporaryPath, contents, new UTF8Encoding(false));
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static async Task AssertSha256Async(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var actualBytes = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        var actual = Convert.ToHexStringLower(actualBytes);
        if (!string.Equals(actual, expected.Trim().ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SHA-256 mismatch for {path}. Expected {expected} but got {actual}.");
        }
    }

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Preserve the primary operation error. Temporary files live outside the cache.
        }
        catch (UnauthorizedAccessException)
        {
            // Preserve the primary operation error. Temporary files live outside the cache.
        }
    }

    private void WriteWarning(string message)
    {
        if (!string.IsNullOrWhiteSpace(getEnvironmentVariable("GITHUB_ACTIONS")))
        {
            var escaped = message
                .Replace("%", "%25", StringComparison.Ordinal)
                .Replace("\r", "%0D", StringComparison.Ordinal)
                .Replace("\n", "%0A", StringComparison.Ordinal);
            errors.WriteLine($"::warning::{escaped}");
        }
        else
        {
            errors.WriteLine($"warning: {message}");
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeCacheSegmentRegex();

    private sealed record BundledConfiguration(Uri Uri, string Sha256);
}

internal sealed class CacheEntryLock : IAsyncDisposable
{
    private readonly FileStream stream;

    private CacheEntryLock(FileStream stream)
    {
        this.stream = stream;
    }

    internal static async Task<CacheEntryLock> AcquireAsync(string path, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(1);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new IOException($"Managed-reference cache lock cannot be a link: {path}");
                }

                var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                return new CacheEntryLock(stream);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        stream.Dispose();
        // Keep the empty lock file. Unlinking it would let another process lock a
        // new inode while an existing waiter still holds the old one on Unix.
        return ValueTask.CompletedTask;
    }
}
