part of 'mod_registry_index_builder.dart';

/// Validates community entry files (`registry/*.json`) the way registry CI
/// does: structural checks for every file, plus optional download checks
/// (sha of the hosted bytes, packaged-vs-inline manifest equality) for the
/// files listed in [onlyFiles] — typically the ones a PR changed.
class ModRegistryValidationOptions {
  const ModRegistryValidationOptions({
    required this.entriesDirectory,
    this.modsDirectory = '',
    this.onlyFiles = const [],
    this.download = true,
    this.publication = false,
    this.previousEntriesDirectory = '',
  });

  final String entriesDirectory;

  /// First-party `mods/` tree, scanned for id collisions and dependency
  /// targets. Optional so self-hosters can validate standalone entry sets.
  final String modsDirectory;

  /// Entry files (paths or bare file names) that get the expensive download
  /// checks. Empty means all files when [download] is true.
  final List<String> onlyFiles;

  final bool download;
  final bool publication;
  final String previousEntriesDirectory;
}

class RegistryFileReport {
  const RegistryFileReport({required this.fileName, required this.issues});

  final String fileName;
  final List<LauncherIssue> issues;

  bool get ok => issues.every((issue) => !issue.isBlocking);
}

class ModRegistryValidationResult {
  const ModRegistryValidationResult({
    required this.reports,
    this.globalIssues = const [],
  });

  final List<RegistryFileReport> reports;
  final List<LauncherIssue> globalIssues;

  bool get ok =>
      globalIssues.every((issue) => !issue.isBlocking) &&
      reports.every((report) => report.ok);
}

class ModRegistryValidator {
  ModRegistryValidator({Future<List<int>> Function(Uri uri)? fetchBytes})
    : _fetchBytes = fetchBytes ?? _fetchPackageBytes;

  final Future<List<int>> Function(Uri uri) _fetchBytes;

  Future<ModRegistryValidationResult> validate(
    ModRegistryValidationOptions options,
  ) async {
    final directory = Directory(options.entriesDirectory);
    if (!directory.existsSync()) {
      return const ModRegistryValidationResult(
        reports: [],
        globalIssues: [
          LauncherIssue(
            severity: IssueSeverity.info,
            message: 'No registry entries directory found — nothing to check.',
          ),
        ],
      );
    }

    final firstParty = _firstPartyManifests(options.modsDirectory);
    final files =
        listBoundedDirectorySync(directory)
            .whereType<File>()
            .where(
              (file) =>
                  file.path.toLowerCase().endsWith('.json') &&
                  p.basename(file.path).toLowerCase() != 'readme.json',
            )
            .toList()
          ..sort((a, b) => a.path.compareTo(b.path));
    final globalIssues = <LauncherIssue>[
      if (options.publication && files.isNotEmpty)
        const LauncherIssue(
          severity: IssueSeverity.error,
          message:
              'Official community registry intake is closed for the initial release; use a self-hosted registry.',
        ),
      ...validateRegistryPublicationHistory(
        entriesDirectory: options.entriesDirectory,
        previousEntriesDirectory: options.previousEntriesDirectory,
      ),
    ];

    // First pass: parse + structural issues, and collect the known version
    // set for dependency resolution.
    final parsed = <String, RegistryEntryFile>{};
    final reports = <String, List<LauncherIssue>>{};
    final seenIds = <String>{};
    final knownVersions = <String, List<SemanticVersion>>{
      for (final entry in firstParty.entries)
        entry.key: [
          if (SemanticVersion.tryParse(entry.value.version) != null)
            SemanticVersion.tryParse(entry.value.version)!,
        ],
    };

    for (final file in files) {
      final name = p.basename(file.path);
      final issues = reports.putIfAbsent(name, () => []);
      final RegistryEntryFile entry;
      try {
        entry = RegistryEntryFile.fromJson(
          readBoundedJsonObjectSync(
            file,
            maxBytes: CliFileLimits.registryEntry,
          ),
        );
      } on Object {
        issues.add(
          const LauncherIssue(
            severity: IssueSeverity.error,
            message: 'File is not a valid JSON object.',
          ),
        );
        continue;
      }
      parsed[name] = entry;
      issues.addAll(entry.validate());
      for (final version in entry.versions) {
        final manifest = version.manifest;
        if (manifest == null) continue;
        for (final finding in [
          ...manifest.validate(),
          ...validateManifestPublicationLicense(manifest),
        ]) {
          issues.add(
            LauncherIssue(
              severity: IssueSeverity.error,
              subjectId: '${entry.id}@${version.version}',
              message: 'Manifest publication finding: ${finding.message}',
            ),
          );
        }
      }
      issues.addAll(
        _entryPlacementIssues(
          entry,
          fileName: name,
          firstPartyIds: firstParty.keys.toSet(),
          seenIds: seenIds,
        ),
      );
      seenIds.add(entry.id.toLowerCase());
      knownVersions.putIfAbsent(entry.id.toLowerCase(), () => []).addAll([
        for (final version in entry.versions)
          if (SemanticVersion.tryParse(version.version) != null)
            SemanticVersion.tryParse(version.version)!,
      ]);
    }

    // Second pass: dependency resolvability against the merged registry.
    for (final entry in parsed.entries) {
      final file = entry.value;
      for (final version in file.sortedVersions) {
        final manifest = version.manifest;
        if (manifest == null) continue;
        reports[entry.key]!.addAll(
          _dependencyIssues(manifest, knownVersions, firstParty),
        );
      }
    }

    // Third pass: download checks.
    if (options.download) {
      final selected = _selectedFiles(options.onlyFiles, parsed.keys);
      for (final name in selected) {
        final file = parsed[name];
        if (file == null || !reports[name]!.every((i) => !i.isBlocking)) {
          continue; // Structural failures already block; skip the downloads.
        }
        for (final version in file.versions) {
          reports[name]!.addAll(await _downloadIssues(file, version));
        }
      }
    }

    return ModRegistryValidationResult(
      reports: [
        for (final entry in reports.entries)
          RegistryFileReport(fileName: entry.key, issues: entry.value),
      ],
      globalIssues: globalIssues,
    );
  }

  List<LauncherIssue> _dependencyIssues(
    ModManifest manifest,
    Map<String, List<SemanticVersion>> knownVersions,
    Map<String, ModManifest> firstParty,
  ) {
    final issues = <LauncherIssue>[];
    for (final dependency in manifest.dependencies) {
      final depId = dependency.id.toLowerCase();
      final versions = knownVersions[depId];
      if (versions == null) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: manifest.id,
            message:
                'Required dependency "${dependency.id}" is not in the '
                'registry (first-party or community).',
          ),
        );
        continue;
      }
      final satisfied = versions.any(
        (version) => dependency.versionRange.allows(version.toString()),
      );
      if (!satisfied) {
        // Only the current first-party version is known here; older ones may
        // exist in past releases, so keep that case a warning.
        final isFirstParty = firstParty.containsKey(depId);
        issues.add(
          LauncherIssue(
            severity: isFirstParty
                ? IssueSeverity.warning
                : IssueSeverity.error,
            subjectId: manifest.id,
            message:
                'No known version of "${dependency.id}" satisfies '
                '"${dependency.versionRange}"'
                '${isFirstParty ? ' (checked against the current first-party version only)' : ''}.',
          ),
        );
      }
    }
    return issues;
  }

  Future<List<LauncherIssue>> _downloadIssues(
    RegistryEntryFile file,
    RegistryEntryVersion version,
  ) async {
    final label = '${file.id}@${version.version}';
    final uri = Uri.tryParse(version.downloadUrl);
    if (uri == null) {
      return const [];
    }
    final List<int> bytes;
    try {
      bytes = await _fetchBytes(uri);
    } on Object catch (error) {
      return [
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: label,
          message: 'Download failed for ${version.downloadUrl}: $error',
        ),
      ];
    }

    final issues = <LauncherIssue>[];
    final ModPackageSummary package;
    try {
      package = readModPackage(bytes);
    } on StateError catch (error) {
      return [
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: label,
          message: 'Hosted package is invalid: ${error.message}',
        ),
      ];
    }
    if (package.sha256Hex != version.packageSha256) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: label,
          message:
              'packageSha256 does not match the hosted bytes (expected '
              '${version.packageSha256}, got ${package.sha256Hex}). '
              'Re-run `topiaforge registry add-entry` against the exact '
              'uploaded file.',
        ),
      );
    }
    issues.addAll(
      validateManifestPublicationLicense(
        package.manifest,
        packageEntries: package.entryNames,
      ),
    );
    final inline = version.manifest;
    if (inline != null &&
        _canonicalJson(inline.toJson()) !=
            _canonicalJson(package.manifest.toJson())) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: label,
          message:
              'The inline manifest differs from topiaforge.mod.json inside '
              'the hosted package. Regenerate the entry with '
              '`topiaforge registry add-entry`.',
        ),
      );
    }
    return issues;
  }

  Map<String, ModManifest> _firstPartyManifests(String modsDirectory) {
    if (modsDirectory.trim().isEmpty) {
      return const {};
    }
    final directory = Directory(modsDirectory);
    if (!directory.existsSync()) {
      return const {};
    }
    final result = <String, ModManifest>{};
    for (final modDir in listBoundedDirectorySync(
      directory,
    ).whereType<Directory>()) {
      final manifestFile = File(p.join(modDir.path, 'topiaforge.mod.json'));
      if (!manifestFile.existsSync()) {
        continue;
      }
      try {
        final manifest = ModManifest.fromJson(
          readBoundedJsonObjectSync(
            manifestFile,
            maxBytes: CliFileLimits.manifest,
          ),
        );
        if (manifest.id.trim().isNotEmpty) {
          result[manifest.id.toLowerCase()] = manifest;
        }
      } on Object {
        // A broken first-party manifest is caught by its own checks; it just
        // does not participate in registry collision/dependency data here.
      }
    }
    return result;
  }

  Iterable<String> _selectedFiles(
    List<String> onlyFiles,
    Iterable<String> allNames,
  ) {
    if (onlyFiles.isEmpty) {
      return allNames;
    }
    final wanted = onlyFiles
        .map((path) => p.basename(path).toLowerCase())
        .toSet();
    return allNames.where((name) => wanted.contains(name.toLowerCase()));
  }
}

/// Streams a package for validation: https (or loopback http for tests),
/// hard-capped at the launcher's 512 MB limit.
Future<List<int>> _fetchPackageBytes(Uri uri) async {
  final isLoopback =
      uri.host == '127.0.0.1' || uri.host == 'localhost' || uri.host == '::1';
  if ((uri.scheme != 'https' && !(uri.scheme == 'http' && isLoopback)) ||
      uri.userInfo.isNotEmpty ||
      uri.hasQuery ||
      uri.hasFragment) {
    throw StateError('Only https package URLs are allowed.');
  }
  final client = HttpClient();
  client.connectionTimeout = const Duration(seconds: 15);
  client.idleTimeout = const Duration(seconds: 30);
  try {
    final request = await client.getUrl(uri);
    request.followRedirects = false;
    final response = await request.close().timeout(const Duration(seconds: 30));
    if (response.isRedirect) {
      throw StateError(
        'Package URL redirects are not allowed; submit the final HTTPS URL.',
      );
    }
    if (response.statusCode < 200 || response.statusCode >= 300) {
      throw StateError('HTTP ${response.statusCode}');
    }
    final bytes = <int>[];
    await for (final chunk in response) {
      bytes.addAll(chunk);
      if (bytes.length > ModRegistryFormat.maxPackageBytes) {
        throw StateError('Package is larger than the 512 MB launcher limit.');
      }
    }
    return bytes;
  } finally {
    client.close(force: true);
  }
}

/// JSON with recursively sorted object keys, for order-insensitive equality.
String _canonicalJson(Object? value) {
  if (value is Map) {
    final keys = value.keys.map((key) => key.toString()).toList()..sort();
    final parts = [
      for (final key in keys)
        '${jsonEncode(key)}:${_canonicalJson(value[key])}',
    ];
    return '{${parts.join(',')}}';
  }
  if (value is List) {
    return '[${value.map(_canonicalJson).join(',')}]';
  }
  return jsonEncode(value);
}
