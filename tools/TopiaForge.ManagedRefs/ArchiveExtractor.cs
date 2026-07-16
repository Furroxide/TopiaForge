using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace TopiaForge.ManagedRefs;

internal interface IArchiveExtractor
{
    Task ExtractPublicAsync(string archivePath, string destinationManagedDirectory, CancellationToken cancellationToken);

    Task ExtractBundledAsync(string archivePath, string destinationManagedDirectory, CancellationToken cancellationToken);
}

internal sealed class ArchiveExtractor : IArchiveExtractor
{
    private const int MaxFiles = 100_000;
    private const long MaxBytes = 4L * 1024 * 1024 * 1024;
    private readonly IProcessRunner processRunner;
    private readonly Func<string> findSevenZip;

    internal ArchiveExtractor()
        : this(new ProcessRunner(), FindSevenZip)
    {
    }

    internal ArchiveExtractor(IProcessRunner processRunner, Func<string> findSevenZip)
    {
        this.processRunner = processRunner;
        this.findSevenZip = findSevenZip;
    }

    public async Task ExtractPublicAsync(
        string archivePath,
        string destinationManagedDirectory,
        CancellationToken cancellationToken)
    {
        var extractRoot = CreateTemporaryDirectory("robotopia-public-refs-extract");
        try
        {
            var arguments = new[]
            {
                "x",
                archivePath,
                $"-o{extractRoot}",
                "-y",
                "-bso0",
                "-bsp0",
                "*/Robotopia_Data/Managed/*",
                "Robotopia_Data/Managed/*",
                "*/Managed/*",
            };
            var result = await processRunner.RunAsync(
                findSevenZip(),
                arguments,
                TimeSpan.FromMinutes(30),
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0)
            {
                throw new InvalidDataException(
                    $"7-Zip extraction failed with exit code {result.ExitCode}. {result.StandardError}".Trim());
            }

            var managedDirectory = FindManagedDirectory(extractRoot);
            CopyManagedDirectory(managedDirectory, destinationManagedDirectory);
        }
        finally
        {
            PathSafety.DeleteDirectoryIfSafe(extractRoot);
        }
    }

    public Task ExtractBundledAsync(
        string archivePath,
        string destinationManagedDirectory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var extractRoot = CreateTemporaryDirectory("robotopia-bundled-refs-extract");
        try
        {
            ExtractZipSafely(archivePath, extractRoot, cancellationToken);
            var managedDirectory = FindManagedDirectory(extractRoot);
            CopyManagedDirectory(managedDirectory, destinationManagedDirectory);
            return Task.CompletedTask;
        }
        finally
        {
            PathSafety.DeleteDirectoryIfSafe(extractRoot);
        }
    }

    internal static string FindSevenZip()
    {
        var candidates = new List<string> { "7z", "7zz", "7za" };
        var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
        if (!string.IsNullOrWhiteSpace(programFiles))
        {
            candidates.Add(Path.Combine(programFiles, "7-Zip", "7z.exe"));
        }

        var programFilesX86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            candidates.Add(Path.Combine(programFilesX86, "7-Zip", "7z.exe"));
        }

        foreach (var candidate in candidates)
        {
            var resolved = ResolveExecutable(candidate);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        throw new FileNotFoundException(
            "7-Zip was not found. Install p7zip/7zip before restoring public Robotopia refs.");
    }

    internal static void ExtractZipSafely(string archivePath, string destination, CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxFiles)
        {
            throw new InvalidDataException($"Bundled refs archive exceeds the {MaxFiles}-entry limit.");
        }

        var destinationRoot = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long totalBytes = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalBytes = checked(totalBytes + entry.Length);
            if (totalBytes > MaxBytes)
            {
                throw new InvalidDataException("Bundled refs archive exceeds the 4 GiB extraction limit.");
            }

            if (IsSymbolicLink(entry))
            {
                throw new InvalidDataException($"Bundled refs archive contains a symbolic link: {entry.FullName}");
            }

            var outputPath = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!outputPath.StartsWith(destinationRoot, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Bundled refs archive contains an unsafe path: {entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(outputPath);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using var source = entry.Open();
            using var target = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            source.CopyTo(target);
        }
    }

    private static bool IsSymbolicLink(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixSymbolicLink = 0xA000;
        var unixMode = (entry.ExternalAttributes >> 16) & UnixFileTypeMask;
        return unixMode == UnixSymbolicLink;
    }

    private static string FindManagedDirectory(string root)
    {
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        var visitedEntries = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            PathSafety.RequireRegularDirectory(current.Path, "extracted directory");
            if (ManagedDirectoryValidator.RequiredAssemblies.Keys.All(
                name => File.Exists(Path.Combine(current.Path, name))))
            {
                return current.Path;
            }

            if (current.Depth >= 32)
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(current.Path))
            {
                PathSafety.RequireRegularFile(file, "extracted file");
                if (++visitedEntries > MaxFiles)
                {
                    throw new InvalidDataException($"Extracted refs exceed the {MaxFiles}-file limit.");
                }
            }

            foreach (var directory in Directory.EnumerateDirectories(current.Path).OrderBy(value => value, StringComparer.Ordinal))
            {
                PathSafety.RequireRegularDirectory(directory, "extracted directory");
                if (++visitedEntries > MaxFiles)
                {
                    throw new InvalidDataException($"Extracted refs exceed the {MaxFiles}-entry limit.");
                }

                pending.Push((directory, current.Depth + 1));
            }
        }

        throw new InvalidDataException($"Could not find a complete Robotopia managed refs directory under {root}.");
    }

    private static void CopyManagedDirectory(string source, string destination)
    {
        if (Directory.Exists(destination))
        {
            throw new IOException($"Managed refs staging destination already exists: {destination}");
        }

        Directory.CreateDirectory(destination);
        var pending = new Stack<(string Source, string Destination, int Depth)>();
        pending.Push((source, destination, 0));
        var files = 0;
        long totalBytes = 0;
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current.Depth > 32)
            {
                throw new InvalidDataException("Managed refs exceed the 32-directory-depth limit.");
            }

            PathSafety.RequireRegularDirectory(current.Source, "managed refs source directory");
            foreach (var file in Directory.EnumerateFiles(current.Source))
            {
                PathSafety.RequireRegularFile(file, "managed refs source file");
                var fileInfo = new FileInfo(file);
                totalBytes = checked(totalBytes + fileInfo.Length);
                if (++files > MaxFiles || totalBytes > MaxBytes)
                {
                    throw new InvalidDataException("Managed refs exceed the copy safety limits.");
                }

                File.Copy(file, Path.Combine(current.Destination, Path.GetFileName(file)), overwrite: false);
            }

            foreach (var directory in Directory.EnumerateDirectories(current.Source))
            {
                PathSafety.RequireRegularDirectory(directory, "managed refs source directory");
                var childDestination = Path.Combine(current.Destination, Path.GetFileName(directory));
                Directory.CreateDirectory(childDestination);
                pending.Push((directory, childDestination, current.Depth + 1));
            }
        }
    }

    private static string CreateTemporaryDirectory(string prefix)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string? ResolveExecutable(string candidate)
    {
        if (Path.IsPathRooted(candidate))
        {
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')
            : new[] { string.Empty };
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var fullPath = Path.Combine(directory, candidate + extension);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }
            }
        }

        return null;
    }
}

internal sealed record ProcessResult(int ExitCode, string StandardError);

internal interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

internal sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ??
            throw new IOException($"Could not start archive extractor: {executable}");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // The process exited between cancellation and the kill attempt.
            }

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException($"Archive extraction exceeded {timeout}.");
        }

        _ = await outputTask.ConfigureAwait(false);
        return new ProcessResult(process.ExitCode, await errorTask.ConfigureAwait(false));
    }
}
