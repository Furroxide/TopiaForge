using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using TopiaForge.ManagedRefs;

namespace TopiaForge.ManagedRefs.Tests;

internal sealed class TemporaryWorkspace : IDisposable
{
    internal TemporaryWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), $"topiaforge-managed-refs-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(Root, ".git"));
        TemporaryDirectory = Path.Combine(Root, "tmp");
        Directory.CreateDirectory(TemporaryDirectory);
    }

    internal string Root { get; }

    internal string TemporaryDirectory { get; }

    internal string WriteConfiguration(string windowsSha, string macSha)
    {
        var path = Path.Combine(Root, "config.json");
        File.WriteAllText(
            path,
            $$"""
            {
              "buildId": 2309,
              "baseUrl": "https://public.example.invalid",
              "manifestUrl": "https://public.example.invalid/latest.json",
              "sourcePlatform": "windows",
              "archives": {
                "windows": {
                  "path": "Robotopia-v02309-Win64.7z",
                  "sha256": "{{windowsSha}}"
                },
                "mac": {
                  "path": "Robotopia-v02309-Mac.7z",
                  "sha256": "{{macSha}}"
                }
              }
            }
            """);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
        {
            Directory.Delete(Root, recursive: true);
        }
    }
}

internal sealed record FakeHttpCall(string Method, Uri Uri, string? BearerToken, bool HideUri);

internal sealed class FakeHttpTransport : IHttpTransport
{
    internal List<FakeHttpCall> Calls { get; } = new();

    internal string ManifestJson { get; set; } = string.Empty;

    internal byte[] DownloadBytes { get; set; } = Array.Empty<byte>();

    internal bool FailPublic { get; set; }

    public Task<HttpProbeResult> HeadAsync(
        Uri uri,
        string label,
        string? bearerToken,
        bool hideUri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new FakeHttpCall("HEAD", uri, bearerToken, hideUri));
        ThrowIfPublicFailure(uri);
        return Task.FromResult(new HttpProbeResult(1234));
    }

    public Task<string> GetStringAsync(
        Uri uri,
        string label,
        string? bearerToken,
        bool hideUri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new FakeHttpCall("GET-STRING", uri, bearerToken, hideUri));
        ThrowIfPublicFailure(uri);
        return Task.FromResult(ManifestJson);
    }

    public Task DownloadAsync(
        Uri uri,
        string destinationPath,
        string label,
        string? bearerToken,
        bool hideUri,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add(new FakeHttpCall("DOWNLOAD", uri, bearerToken, hideUri));
        ThrowIfPublicFailure(uri);
        File.WriteAllBytes(destinationPath, DownloadBytes);
        return Task.CompletedTask;
    }

    private void ThrowIfPublicFailure(Uri uri)
    {
        if (FailPublic && uri.Host == "public.example.invalid")
        {
            throw new InvalidDataException("synthetic public failure");
        }
    }
}

internal sealed class MarkerValidator : IManagedDirectoryValidator
{
    internal static readonly string[] Names = ManagedDirectoryValidator.RequiredAssemblies.Keys
        .OrderBy(value => value, StringComparer.Ordinal)
        .ToArray();

    public void Validate(string path)
    {
        foreach (var name in Names)
        {
            var file = Path.Combine(path, name);
            if (!File.Exists(file) || File.ReadAllText(file) != "valid")
            {
                throw new InvalidDataException($"invalid marker assembly: {file}");
            }
        }
    }

    public bool IsValid(string path, out string error)
    {
        try
        {
            Validate(path);
            error = string.Empty;
            return true;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }
}

internal sealed class MarkerExtractor : IArchiveExtractor
{
    internal int PublicCalls { get; private set; }

    internal int BundledCalls { get; private set; }

    internal bool FailAfterPartialWrite { get; set; }

    public Task ExtractPublicAsync(
        string archivePath,
        string destinationManagedDirectory,
        CancellationToken cancellationToken)
    {
        PublicCalls++;
        return ExtractAsync(destinationManagedDirectory, cancellationToken);
    }

    public Task ExtractBundledAsync(
        string archivePath,
        string destinationManagedDirectory,
        CancellationToken cancellationToken)
    {
        BundledCalls++;
        return ExtractAsync(destinationManagedDirectory, cancellationToken);
    }

    private Task ExtractAsync(string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, MarkerValidator.Names[0]), "valid");
        if (FailAfterPartialWrite)
        {
            throw new InvalidDataException("synthetic partial extraction failure");
        }

        foreach (var name in MarkerValidator.Names.Skip(1))
        {
            File.WriteAllText(Path.Combine(destination, name), "valid");
        }

        return Task.CompletedTask;
    }
}

internal sealed class FakeProcessRunner : IProcessRunner
{
    internal IReadOnlyList<string> Arguments { get; private set; } = Array.Empty<string>();

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Arguments = arguments.ToArray();
        var outputArgument = arguments.Single(value => value.StartsWith("-o", StringComparison.Ordinal));
        var managed = Path.Combine(outputArgument[2..], "Robotopia", "Robotopia_Data", "Managed");
        Directory.CreateDirectory(managed);
        foreach (var name in MarkerValidator.Names)
        {
            File.WriteAllText(Path.Combine(managed, name), "archive marker");
        }

        return Task.FromResult(new ProcessResult(0, string.Empty));
    }
}

internal sealed class StaticResponseHandler : HttpMessageHandler
{
    private readonly HttpStatusCode statusCode;
    private readonly string payload;

    internal StaticResponseHandler(HttpStatusCode statusCode, string payload = "payload")
    {
        this.statusCode = statusCode;
        this.payload = payload;
    }

    internal List<string?> AuthorizationParameters { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        AuthorizationParameters.Add(request.Headers.Authorization?.Parameter);
        return Task.FromResult(new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(payload),
            RequestMessage = request,
        });
    }
}
