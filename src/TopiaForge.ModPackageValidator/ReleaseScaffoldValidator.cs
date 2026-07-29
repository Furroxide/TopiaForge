using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using TopiaForge.ModManager.Core;

namespace TopiaForge.ModPackageValidator;

/// <summary>
/// Independently verifies that a scaffold produced by an extracted release is portable, exactly locked,
/// and—when requested—installed with a valid receipt and active manager state.
/// </summary>
public static class ReleaseScaffoldValidator
{
    private const long MaxMetadataBytes = 4L * 1024 * 1024;
    private const long MaxArchiveBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxProjectFiles = 128;
    private const int MaxTraversedEntries = 16_384;
    private const int MaxInstalledVersions = 256;
    private const int MaxForbiddenRoots = 256;
    private const int MaxProjectReferences = 256;
    private const int MaxPackageReferences = 256;
    private const int MaxLockTargets = 64;

    public static IReadOnlyList<string> Validate(
        string projectPath,
        IReadOnlyList<string> forbiddenRoots,
        string? packagePath = null,
        string? installedPackagesPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(forbiddenRoots);
        if (forbiddenRoots.Count > MaxForbiddenRoots)
        {
            throw new ArgumentException("The forbidden-root list exceeds the supported limit.", nameof(forbiddenRoots));
        }

        if ((packagePath is null) != (installedPackagesPath is null))
        {
            throw new ArgumentException("The package and installed-packages paths must be supplied together.");
        }

        var projectRoot = Path.GetFullPath(projectPath);
        if (!Directory.Exists(projectRoot))
        {
            return new[] { projectRoot + ": project directory does not exist" };
        }

        var failures = VerifyScaffold(projectRoot, forbiddenRoots);
        if (packagePath is not null && installedPackagesPath is not null)
        {
            failures.AddRange(VerifyInstall(
                projectRoot,
                Path.GetFullPath(packagePath),
                Path.GetFullPath(installedPackagesPath)));
        }

        return failures;
    }

    private static List<string> VerifyScaffold(string projectRoot, IReadOnlyList<string> forbiddenRoots)
    {
        var failures = new List<string>();
        var forbidden = forbiddenRoots
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .SelectMany(PathSpellings)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();
        var projectSpellings = PathSpellings(projectRoot).ToList();
        foreach (var root in forbidden)
        {
            if (projectSpellings.Any(project => IsEqualToOrBelow(project, root)))
            {
                failures.Add(projectRoot + ": project is inside forbidden root " + root);
            }
        }

        JsonElement globalJson;
        JsonElement sdkLock;
        try
        {
            globalJson = ReadJsonObject(Path.Combine(projectRoot, "global.json"));
            sdkLock = ReadJsonObject(Path.Combine(projectRoot, "topiaforge.sdk.lock.json"));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            failures.Add(exception.Message);
            return failures;
        }

        var globalPath = Path.Combine(projectRoot, "global.json");
        var sdk = TryGetObject(globalJson, "sdk");
        if (sdk is null)
        {
            failures.Add(globalPath + ": sdk object is missing");
        }

        var dotnetVersion = GetNonEmptyString(sdkLock, "dotnetSdkVersion");
        var sdkVersion = GetNonEmptyString(sdkLock, "sdkVersion");
        if (dotnetVersion is null)
        {
            failures.Add("topiaforge.sdk.lock.json: dotnetSdkVersion is missing");
        }
        else if (sdk is null || GetNonEmptyString(sdk.Value, "version") != dotnetVersion)
        {
            failures.Add("global.json: SDK version does not match the TopiaForge lock");
        }

        if (sdk is null || GetNonEmptyString(sdk.Value, "rollForward") != "disable")
        {
            failures.Add("global.json: sdk.rollForward must be disable");
        }

        if (!sdkLock.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var schema) || schema != 1)
        {
            failures.Add("topiaforge.sdk.lock.json: schemaVersion must be 1");
        }

        if (sdkVersion is null)
        {
            failures.Add("topiaforge.sdk.lock.json: sdkVersion is missing");
        }

        var manifestSha = GetNonEmptyString(sdkLock, "manifestSha256");
        if (!IsLowerHexSha256(manifestSha))
        {
            failures.Add("topiaforge.sdk.lock.json: manifestSha256 is not canonical");
        }

        IReadOnlyList<string> projects;
        try
        {
            projects = EnumerateProjectFiles(projectRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(projectRoot + ": C# projects could not be enumerated safely (" + exception.Message + ")");
            return failures;
        }

        if (projects.Count == 0)
        {
            failures.Add(projectRoot + ": no C# projects found");
        }

        foreach (var projectFile in projects)
        {
            ValidateProject(projectRoot, projectFile, sdkVersion, forbidden, failures);
        }

        var propsPath = Path.Combine(projectRoot, "topiaforge.dev.props");
        if (!IsRegularFile(propsPath))
        {
            failures.Add(propsPath + ": generated restore props are missing");
        }
        else
        {
            try
            {
                var propsText = ReadBoundedText(propsPath);
                CheckTextForForbiddenPaths(propsPath, propsText, forbidden, failures);
                var propsDocument = ParseXmlDocument(propsText);
                CheckXmlValuesForForbiddenPaths(propsPath, propsDocument, forbidden, failures);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException or XmlException)
            {
                failures.Add(propsPath + ": cannot read generated restore props XML (" + exception.Message + ")");
            }
        }

        return failures;
    }

    private static void ValidateProject(
        string projectRoot,
        string projectFile,
        string? sdkVersion,
        IReadOnlyList<string> forbidden,
        ICollection<string> failures)
    {
        string text;
        try
        {
            text = ReadBoundedText(projectFile);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            failures.Add(projectFile + ": cannot read project (" + exception.Message + ")");
            return;
        }

        CheckTextForForbiddenPaths(projectFile, text, forbidden, failures);
        XDocument document;
        try
        {
            document = ParseXmlDocument(text);
        }
        catch (XmlException exception)
        {
            failures.Add(projectFile + ": invalid project XML (" + exception.Message + ")");
            return;
        }

        CheckXmlValuesForForbiddenPaths(projectFile, document, forbidden, failures);

        var projectReferences = document.Descendants()
            .Where(element => element.Name.LocalName == "ProjectReference")
            .Take(MaxProjectReferences + 1)
            .ToList();
        if (projectReferences.Count > MaxProjectReferences)
        {
            failures.Add(projectFile + ": exceeds the ProjectReference limit");
            return;
        }

        foreach (var reference in projectReferences)
        {
            var include = reference.Attribute("Include")?.Value;
            if (string.IsNullOrWhiteSpace(include))
            {
                failures.Add(projectFile + ": ProjectReference is missing Include");
                continue;
            }

            string target;
            try
            {
                var portableInclude = include
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                target = Path.GetFullPath(portableInclude, Path.GetDirectoryName(projectFile)!);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                failures.Add(projectFile + ": ProjectReference path is invalid (" + exception.Message + ")");
                continue;
            }

            var resolvedTarget = ResolveExistingLinks(target);
            var resolvedRoot = ResolveExistingLinks(projectRoot);
            if (!IsPathInside(target, projectRoot) || !IsPathInside(resolvedTarget, resolvedRoot))
            {
                failures.Add(projectFile + ": ProjectReference escapes scaffold (" + target + ")");
            }
            else if (!IsRegularFile(target))
            {
                failures.Add(projectFile + ": ProjectReference is missing (" + target + ")");
            }
        }

        var packageReferences = document.Descendants()
            .Where(element => element.Name.LocalName == "PackageReference")
            .Take(MaxPackageReferences + 1)
            .ToList();
        if (packageReferences.Count > MaxPackageReferences)
        {
            failures.Add(projectFile + ": exceeds the PackageReference limit");
            return;
        }

        var packageIds = new List<string>();
        foreach (var reference in packageReferences)
        {
            var packageId = reference.Attribute("Include")?.Value ?? string.Empty;
            if (!packageId.StartsWith("TopiaForge.Mods.", StringComparison.Ordinal))
            {
                continue;
            }

            packageIds.Add(packageId);
            var version = reference.Attribute("Version")?.Value ??
                reference.Elements().FirstOrDefault(element => element.Name.LocalName == "Version")?.Value;
            if (sdkVersion is null || !string.Equals(version, sdkVersion, StringComparison.Ordinal))
            {
                failures.Add(projectFile + ": " + packageId + " must use exact version " + (sdkVersion ?? "<missing>"));
            }
        }

        if (packageIds.Count == 0)
        {
            failures.Add(projectFile + ": no TopiaForge SDK PackageReference");
        }

        var lockPath = Path.Combine(Path.GetDirectoryName(projectFile)!, "packages.lock.json");
        JsonElement lockFile;
        try
        {
            lockFile = ReadJsonObject(lockPath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            failures.Add(exception.Message);
            return;
        }

        var targets = TryGetObject(lockFile, "dependencies");
        if (targets is null)
        {
            failures.Add(lockPath + ": dependencies object is missing");
            return;
        }

        var targetEntries = targets.Value.EnumerateObject().Take(MaxLockTargets + 1).ToList();
        if (targetEntries.Count > MaxLockTargets)
        {
            failures.Add(lockPath + ": dependencies exceeds the target-framework limit");
            return;
        }

        foreach (var packageId in packageIds.Distinct(StringComparer.Ordinal))
        {
            var entries = new List<JsonElement>();
            foreach (var target in targetEntries)
            {
                if (target.Value.ValueKind == JsonValueKind.Object &&
                    target.Value.TryGetProperty(packageId, out var entry) &&
                    entry.ValueKind == JsonValueKind.Object)
                {
                    entries.Add(entry);
                }
            }

            if (sdkVersion is null || entries.Count == 0 ||
                entries.Any(entry => GetNonEmptyString(entry, "resolved") != sdkVersion))
            {
                failures.Add(lockPath + ": " + packageId + " is not resolved to exact " + (sdkVersion ?? "<missing>"));
            }
        }
    }

    private static List<string> VerifyInstall(string projectRoot, string packagePath, string installedPackagesRoot)
    {
        var failures = new List<string>();
        ModManifest sourceManifest;
        try
        {
            sourceManifest = ModManifestJson.LoadFile(Path.Combine(projectRoot, "topiaforge.mod.json"));
        }
        catch (Exception exception)
        {
            failures.Add(Path.Combine(projectRoot, "topiaforge.mod.json") + ": cannot read manifest (" + exception.Message + ")");
            return failures;
        }

        var sourceManifestPath = Path.Combine(projectRoot, "topiaforge.mod.json");
        var sourceManifestErrors = ManifestValidator.Validate(sourceManifest);
        if (sourceManifestErrors.Count > 0)
        {
            failures.AddRange(sourceManifestErrors.Select(error => sourceManifestPath + ": " + error));
            return failures;
        }

        if (!IsRegularFile(packagePath))
        {
            failures.Add(packagePath + ": packed scaffold archive is missing");
            return failures;
        }

        var packageRoot = Path.Combine(installedPackagesRoot, sourceManifest.Id, sourceManifest.Version);
        if (!Directory.Exists(packageRoot))
        {
            failures.Add(packageRoot + ": packaged CLI did not install the exact id/version layout");
            return failures;
        }

        try
        {
            EnsureBoundedOrdinaryTree(packageRoot);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(packageRoot + ": installed package could not be traversed safely (" + exception.Message + ")");
            return failures;
        }

        var idRoot = Path.Combine(installedPackagesRoot, sourceManifest.Id);
        try
        {
            EnsureOrdinaryDirectory(idRoot);
            var versions = new List<string>();
            foreach (var path in Directory.EnumerateFileSystemEntries(idRoot))
            {
                if (versions.Count >= MaxInstalledVersions)
                {
                    throw new IOException("installed package id exceeds the version-entry limit");
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new IOException("installed package id contains a linked or special entry: " + path);
                }

                versions.Add(Path.GetFileName(path));
            }

            versions.Sort(StringComparer.Ordinal);
            if (versions.Count != 1 || !string.Equals(versions[0], sourceManifest.Version, StringComparison.Ordinal))
            {
                failures.Add(idRoot + ": expected only version " + sourceManifest.Version + ", found [" +
                    string.Join(", ", versions) + "]");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(idRoot + ": installed versions could not be enumerated (" + exception.Message + ")");
        }

        ModManifest installedManifest;
        var installedManifestPath = Path.Combine(packageRoot, "topiaforge.mod.json");
        try
        {
            installedManifest = ModManifestJson.LoadFile(installedManifestPath);
        }
        catch (Exception exception)
        {
            failures.Add(installedManifestPath + ": cannot read manifest (" + exception.Message + ")");
            return failures;
        }

        var installedManifestErrors = ManifestValidator.Validate(installedManifest);
        if (installedManifestErrors.Count > 0)
        {
            failures.AddRange(installedManifestErrors.Select(error => installedManifestPath + ": " + error));
            return failures;
        }

        var installedContentErrors = ManifestContentValidator.Validate(packageRoot, installedManifest);
        if (installedContentErrors.Count > 0)
        {
            failures.AddRange(installedContentErrors.Select(error => installedManifestPath + ": " + error));
            return failures;
        }

        if (sourceManifest.SchemaVersion != installedManifest.SchemaVersion)
        {
            failures.Add(installedManifestPath + ": schemaVersion differs from scaffold");
        }
        CompareManifestField("name", sourceManifest.Id, installedManifest.Id, installedManifestPath, failures);
        CompareManifestField("version", sourceManifest.Version, installedManifest.Version, installedManifestPath, failures);
        CompareManifestField("entryAssembly", sourceManifest.EntryAssembly, installedManifest.EntryAssembly, installedManifestPath, failures);
        CompareManifestField("entryType", sourceManifest.EntryType, installedManifest.EntryType, installedManifestPath, failures);
        if (!(sourceManifest.ApiAssemblies ?? new List<string>()).SequenceEqual(
                installedManifest.ApiAssemblies ?? new List<string>(), StringComparer.Ordinal))
        {
            failures.Add(installedManifestPath + ": apiAssemblies differs from scaffold");
        }

        CompareMultiplayerMetadata(sourceManifest, installedManifest, installedManifestPath, failures);

        var criticalPaths = new[] { "topiaforge.mod.json", installedManifest.EntryAssembly }
            .Concat(installedManifest.ApiAssemblies ?? new List<string>())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.Ordinal);
        foreach (var relative in criticalPaths)
        {
            if (!TryResolvePortableRelativeFile(packageRoot, relative, out var criticalFile) || !IsRegularFile(criticalFile))
            {
                failures.Add(packageRoot + ": critical installed file is missing (" + relative + ")");
            }
        }

        var receiptPath = Path.Combine(packageRoot, PackageInstallReceipt.FileName);
        try
        {
            var receipt = ReadJsonObject(receiptPath);
            failures.AddRange(PackageInstallReceipt.Verify(packageRoot, installedManifest)
                .Select(error => receiptPath + ": " + error));
            VerifyReceiptSource(receipt, receiptPath, packagePath, sourceManifest, failures);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            failures.Add(exception.Message);
        }

        VerifyState(installedPackagesRoot, sourceManifest, failures);
        return failures;
    }

    private static void CompareMultiplayerMetadata(
        ModManifest source,
        ModManifest installed,
        string installedManifestPath,
        ICollection<string> failures)
    {
        var sourceMultiplayer = source.Multiplayer;
        var installedMultiplayer = installed.Multiplayer;
        if ((sourceMultiplayer == null) != (installedMultiplayer == null))
        {
            failures.Add(installedManifestPath + ": multiplayer metadata differs from scaffold");
            return;
        }

        if (sourceMultiplayer == null || installedMultiplayer == null)
        {
            return;
        }

        CompareManifestField(
            "multiplayer.mode",
            sourceMultiplayer.Mode,
            installedMultiplayer.Mode,
            installedManifestPath,
            failures);
        CompareManifestField(
            "multiplayer.presence",
            sourceMultiplayer.Presence,
            installedMultiplayer.Presence,
            installedManifestPath,
            failures);
        CompareManifestField(
            "multiplayer.protocol.version",
            sourceMultiplayer.Protocol?.Version ?? string.Empty,
            installedMultiplayer.Protocol?.Version ?? string.Empty,
            installedManifestPath,
            failures);
        CompareManifestField(
            "multiplayer.protocol.peerVersionRange",
            sourceMultiplayer.Protocol?.PeerVersionRange ?? string.Empty,
            installedMultiplayer.Protocol?.PeerVersionRange ?? string.Empty,
            installedManifestPath,
            failures);
        if (!(sourceMultiplayer.SynchronizedFiles ?? new List<string>()).SequenceEqual(
                installedMultiplayer.SynchronizedFiles ?? new List<string>(),
                StringComparer.Ordinal))
        {
            failures.Add(installedManifestPath + ": multiplayer.synchronizedFiles differs from scaffold");
        }
    }

    private static void VerifyReceiptSource(
        JsonElement receipt,
        string receiptPath,
        string packagePath,
        ModManifest manifest,
        ICollection<string> failures)
    {
        CheckReceiptField(receipt, receiptPath, "schemaVersion", PackageInstallReceipt.CurrentSchemaVersion, failures);
        CheckReceiptField(receipt, receiptPath, "modId", manifest.Id, failures);
        CheckReceiptField(receipt, receiptPath, "version", manifest.Version, failures);
        CheckReceiptField(receipt, receiptPath, "sourceFile", Path.GetFileName(packagePath), failures);
        CheckReceiptField(receipt, receiptPath, "source", PackageInstallReceipt.LocalSource, failures);
        CheckReceiptField(receipt, receiptPath, "validatorVersion", PackageInstallReceipt.CurrentValidatorVersion, failures);

        string archiveDigest;
        try
        {
            archiveDigest = ComputeSha256(packagePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            failures.Add(packagePath + ": archive SHA-256 could not be read (" + exception.Message + ")");
            return;
        }

        CheckReceiptField(receipt, receiptPath, "sourceSha256", archiveDigest, failures);
        var trust = GetNonEmptyString(receipt, "trust");
        if (trust != PackageInstallReceipt.LocalUnverifiedTrust && trust != PackageInstallReceipt.Sha256VerifiedTrust)
        {
            failures.Add(receiptPath + ": trust result is missing or unknown");
        }

        var installedAt = GetNonEmptyString(receipt, "installedAtUtc");
        if (installedAt is null || !DateTimeOffset.TryParse(
                installedAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
        {
            failures.Add(receiptPath + ": installedAtUtc is invalid");
        }
    }

    private static void VerifyState(
        string installedPackagesRoot,
        ModManifest manifest,
        ICollection<string> failures)
    {
        var statePath = Path.Combine(Directory.GetParent(installedPackagesRoot)?.FullName ?? installedPackagesRoot, "state.json");
        JsonElement state;
        try
        {
            state = ReadJsonObject(statePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            failures.Add(exception.Message);
            return;
        }

        if (!state.TryGetProperty("mods", out var mods) || mods.ValueKind != JsonValueKind.Array || mods.GetArrayLength() > 4096)
        {
            failures.Add(statePath + ": mods array is missing or invalid");
            return;
        }

        var matching = mods.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object && GetNonEmptyString(item, "id") == manifest.Id)
            .ToList();
        if (matching.Count != 1)
        {
            failures.Add(statePath + ": expected exactly one state entry for " + manifest.Id);
            return;
        }

        var item = matching[0];
        var enabledByDefault = !string.Equals(
            manifest.Category,
            "DevTool",
            StringComparison.OrdinalIgnoreCase);
        if (GetNonEmptyString(item, "version") != manifest.Version ||
            !IsBoolean(item, "enabled", enabledByDefault) ||
            !IsBoolean(item, "restartRequired", true) ||
            !IsBoolean(item, "uninstallPending", false))
        {
            failures.Add(
                statePath + ": installed state does not match the default activation policy at " + manifest.Version);
        }
    }

    private static JsonElement ReadJsonObject(string path)
    {
        var bytes = ReadBoundedBytes(path);
        using var document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64,
            });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException(path + ": expected a JSON object");
        }

        ValidateNoDuplicateJsonFields(document.RootElement, path);

        return document.RootElement.Clone();
    }

    private static void ValidateNoDuplicateJsonFields(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var fields = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!fields.Add(property.Name))
                {
                    throw new JsonException(path + ": duplicate JSON field '" + property.Name + "'");
                }

                ValidateNoDuplicateJsonFields(property.Value, path);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                ValidateNoDuplicateJsonFields(item, path);
            }
        }
    }

    private static byte[] ReadBoundedBytes(string path)
    {
        if (!IsRegularFile(path))
        {
            throw new FileNotFoundException(path + ": file does not exist or is not regular", path);
        }

        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxMetadataBytes)
        {
            throw new IOException(path + ": file exceeds the 4 MiB metadata limit");
        }

        using var output = new MemoryStream(checked((int)input.Length));
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxMetadataBytes)
            {
                throw new IOException(path + ": file grew beyond the 4 MiB metadata limit");
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static string ReadBoundedText(string path) =>
        new UTF8Encoding(false, true).GetString(ReadBoundedBytes(path));

    private static XDocument ParseXmlDocument(string text)
    {
        using var input = XmlReader.Create(
            new StringReader(text),
            new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxMetadataBytes,
            });
        return XDocument.Load(input, LoadOptions.None);
    }

    private static IReadOnlyList<string> EnumerateProjectFiles(string root)
    {
        var projects = new List<string>();
        var directories = new Stack<string>();
        directories.Push(root);
        var entries = 0;
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            EnsureOrdinaryDirectory(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).OrderBy(path => path, StringComparer.Ordinal))
            {
                if (++entries > MaxTraversedEntries)
                {
                    throw new IOException("scaffold exceeds the filesystem entry limit");
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new IOException("scaffold contains a linked or special filesystem entry: " + path);
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    var name = Path.GetFileName(path);
                    if (!string.Equals(name, "bin", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(name, "obj", StringComparison.OrdinalIgnoreCase))
                    {
                        directories.Push(path);
                    }

                    continue;
                }

                if (string.Equals(Path.GetExtension(path), ".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    if (projects.Count >= MaxProjectFiles)
                    {
                        throw new IOException("scaffold exceeds the C# project limit");
                    }

                    projects.Add(path);
                }
            }
        }

        return projects.OrderBy(path => path, StringComparer.Ordinal).ToList();
    }

    private static void EnsureBoundedOrdinaryTree(string root)
    {
        var directories = new Stack<string>();
        directories.Push(root);
        var entries = 0;
        while (directories.Count > 0)
        {
            var directory = directories.Pop();
            EnsureOrdinaryDirectory(directory);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++entries > MaxTraversedEntries)
                {
                    throw new IOException("installed package exceeds the filesystem entry limit");
                }

                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                {
                    throw new IOException("installed package contains a linked or special filesystem entry: " + path);
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    directories.Push(path);
                }
            }
        }
    }

    private static void EnsureOrdinaryDirectory(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
        {
            throw new IOException("directory is linked or special: " + path);
        }
    }

    private static bool IsRegularFile(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CheckTextForForbiddenPaths(
        string sourcePath,
        string text,
        IReadOnlyList<string> forbidden,
        ICollection<string> failures)
    {
        var folded = NormalizePathText(text);
        foreach (var path in forbidden)
        {
            if (folded.Contains(path, StringComparison.Ordinal))
            {
                var failure = sourcePath + ": contains forbidden path " + path;
                if (!failures.Contains(failure))
                {
                    failures.Add(failure);
                }
            }
        }
    }

    private static void CheckXmlValuesForForbiddenPaths(
        string sourcePath,
        XDocument document,
        IReadOnlyList<string> forbidden,
        ICollection<string> failures)
    {
        if (document.Root is null)
        {
            return;
        }

        foreach (var element in document.Root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes())
            {
                CheckTextForForbiddenPaths(sourcePath, attribute.Value, forbidden, failures);
            }
        }

        foreach (var text in document.Root.DescendantNodesAndSelf().OfType<XText>())
        {
            CheckTextForForbiddenPaths(sourcePath, text.Value, forbidden, failures);
        }
    }

    private static IEnumerable<string> PathSpellings(string path)
    {
        var absolute = Path.GetFullPath(path);
        yield return NormalizePathText(absolute).TrimEnd('/');
        var resolved = ResolveExistingLinks(absolute);
        var normalizedResolved = NormalizePathText(resolved).TrimEnd('/');
        if (!string.Equals(normalizedResolved, NormalizePathText(absolute).TrimEnd('/'), StringComparison.Ordinal))
        {
            yield return normalizedResolved;
        }
    }

    private static string ResolveExistingLinks(string absolutePath)
    {
        try
        {
            var root = Path.GetPathRoot(absolutePath);
            if (string.IsNullOrEmpty(root))
            {
                return absolutePath;
            }

            var current = root;
            foreach (var segment in absolutePath[root.Length..]
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, segment);
                FileSystemInfo? info = Directory.Exists(current)
                    ? new DirectoryInfo(current)
                    : File.Exists(current) ? new FileInfo(current) : null;
                if (info is not null && (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    current = info.ResolveLinkTarget(true)?.FullName ?? current;
                }
            }

            return Path.GetFullPath(current);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return absolutePath;
        }
    }

    private static string NormalizePathText(string value) =>
        value.Replace('\\', '/').ToLowerInvariant();

    private static bool IsEqualToOrBelow(string candidate, string root) =>
        string.Equals(candidate, root, StringComparison.Ordinal) ||
        candidate.StartsWith(root + "/", StringComparison.Ordinal);

    private static bool IsPathInside(string candidate, string root)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return !Path.IsPathRooted(relative) &&
            !string.Equals(relative, "..", StringComparison.Ordinal) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    private static bool TryResolvePortableRelativeFile(string root, string relative, out string path)
    {
        path = string.Empty;
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || Path.IsPathRooted(relative))
        {
            return false;
        }

        var segments = relative.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            return false;
        }

        path = Path.GetFullPath(Path.Combine(new[] { root }.Concat(segments).ToArray()));
        return IsPathInside(path, root);
    }

    private static JsonElement? TryGetObject(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;

    private static string? GetNonEmptyString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String &&
        !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private static bool IsBoolean(JsonElement parent, string name, bool expected) =>
        parent.TryGetProperty(name, out var value) &&
        value.ValueKind == (expected ? JsonValueKind.True : JsonValueKind.False);

    private static bool IsLowerHexSha256(string? value) =>
        value is { Length: 64 } && value.All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string ComputeSha256(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > MaxArchiveBytes)
        {
            throw new IOException("archive exceeds the 2 GiB verification limit");
        }

        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private static void CompareManifestField(
        string field,
        string expected,
        string actual,
        string manifestPath,
        ICollection<string> failures)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            failures.Add(manifestPath + ": " + field + " differs from scaffold");
        }
    }

    private static void CheckReceiptField(
        JsonElement receipt,
        string receiptPath,
        string field,
        object expected,
        ICollection<string> failures)
    {
        var matches = receipt.TryGetProperty(field, out var value) && expected switch
        {
            string text => value.ValueKind == JsonValueKind.String && value.GetString() == text,
            int number => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var actual) && actual == number,
            _ => false,
        };
        if (!matches)
        {
            failures.Add(receiptPath + ": " + field + " does not match " + expected);
        }
    }
}
