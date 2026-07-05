import 'dart:convert';

import 'package:archive/archive.dart';
import 'package:crypto/crypto.dart';
import 'package:launcher_domain/launcher_domain.dart';

/// What [readModPackage] extracted from a `.robotopiamod` zip.
class ModPackageSummary {
  const ModPackageSummary({
    required this.manifest,
    required this.sha256Hex,
    required this.byteLength,
    required this.entryNames,
  });

  final ModManifest manifest;
  final String sha256Hex;
  final int byteLength;
  final List<String> entryNames;
}

/// Reads a `.robotopiamod` package from raw bytes: hashes them, rejects
/// unsafe archive paths, and requires `robotopia.mod.json` plus the manifest's
/// `entryAssembly` to be present. Throws [StateError] with a player-safe
/// message on any structural problem.
ModPackageSummary readModPackage(List<int> bytes) {
  final sha = sha256.convert(bytes).toString();
  final Archive archive;
  try {
    archive = ZipDecoder().decodeBytes(bytes);
  } on Object {
    throw StateError('The file is not a readable zip archive.');
  }

  final entryNames = <String>[];
  for (final file in archive.files) {
    final normalized = file.name.replaceAll('\\', '/');
    if (normalized.startsWith('/') ||
        RegExp(r'^[A-Za-z]:/').hasMatch(normalized) ||
        normalized.split('/').contains('..')) {
      throw StateError(
        'Package contains a path outside the install directory: ${file.name}',
      );
    }
    if (file.isFile) {
      entryNames.add(normalized);
    }
  }

  final manifestFile = archive.files
      .where(
        (file) =>
            file.isFile &&
            file.name.replaceAll('\\', '/') == 'robotopia.mod.json',
      )
      .firstOrNull;
  if (manifestFile == null) {
    throw StateError('Package is missing robotopia.mod.json.');
  }

  final ModManifest manifest;
  try {
    manifest = ModManifest.fromJson(
      jsonDecode(utf8.decode(manifestFile.content as List<int>))
          as Map<String, Object?>,
    );
  } on Object {
    throw StateError('robotopia.mod.json in the package is not valid JSON.');
  }

  final entryAssembly = manifest.entryAssembly.replaceAll('\\', '/');
  if (entryAssembly.isNotEmpty && !entryNames.contains(entryAssembly)) {
    throw StateError(
      'entryAssembly was not found in package: ${manifest.entryAssembly}',
    );
  }

  return ModPackageSummary(
    manifest: manifest,
    sha256Hex: sha,
    byteLength: bytes.length,
    entryNames: entryNames,
  );
}

class RegistryEntryBuildResult {
  const RegistryEntryBuildResult({required this.issues, this.entryFile});

  final RegistryEntryFile? entryFile;
  final List<LauncherIssue> issues;

  bool get ok =>
      entryFile != null && issues.every((issue) => !issue.isBlocking);
}

/// Builds (or updates) the `registry/<id>.json` entry for [package].
///
/// The official registry holds manifests to the zero-finding bar: any
/// validation issue on the manifest — warnings included — blocks the entry.
/// Re-publishing an already-listed version is refused; released packages are
/// immutable, so a changed build must bump its version instead.
RegistryEntryBuildResult buildRegistryEntry({
  required ModPackageSummary package,
  required String downloadUrl,
  String changelog = '',
  RegistryEntryFile? existing,
}) {
  final issues = <LauncherIssue>[];
  final manifest = package.manifest;

  final manifestIssues = manifest.validate();
  if (manifestIssues.isNotEmpty) {
    issues.addAll(manifestIssues);
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: manifest.id,
        message:
            'The official registry requires a manifest with zero validation '
            'findings (${manifestIssues.length} found). Fix them with '
            '`robotopia check package` and repack.',
      ),
    );
  }

  if (package.byteLength > ModRegistryFormat.maxPackageBytes) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.warning,
        subjectId: manifest.id,
        message:
            'Package is larger than the 512 MB launcher limit — players will '
            'not be able to install it.',
      ),
    );
  }

  if (existing != null &&
      existing.id.toLowerCase() != manifest.id.toLowerCase()) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: manifest.id,
        message:
            'The existing entry file is for "${existing.id}", not '
            '"${manifest.id}".',
      ),
    );
  }

  final priorVersions = existing?.versions ?? const <RegistryEntryVersion>[];
  if (priorVersions.any(
    (item) => item.version.trim() == manifest.version.trim(),
  )) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: '${manifest.id}@${manifest.version}',
        message:
            'Version ${manifest.version} is already published. Released '
            'packages are immutable — bump the version '
            '(`robotopia mod bump`) and repack instead of replacing it.',
      ),
    );
  }

  if (issues.any((issue) => issue.isBlocking)) {
    return RegistryEntryBuildResult(issues: issues);
  }

  final entry = RegistryEntryFile(
    id: manifest.id,
    homepage: existing?.homepage.isNotEmpty == true
        ? existing!.homepage
        : manifest.homepage,
    versions: [
      RegistryEntryVersion(
        version: manifest.version,
        downloadUrl: downloadUrl,
        packageSha256: package.sha256Hex,
        changelog: changelog,
        manifest: manifest,
      ),
      ...priorVersions,
    ],
  );

  issues.addAll(entry.validate());
  return RegistryEntryBuildResult(
    issues: issues,
    entryFile: issues.any((issue) => issue.isBlocking) ? null : entry,
  );
}

extension _FirstOrNull<T> on Iterable<T> {
  T? get firstOrNull {
    final iterator = this.iterator;
    return iterator.moveNext() ? iterator.current : null;
  }
}
