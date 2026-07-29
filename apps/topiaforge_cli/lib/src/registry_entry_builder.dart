import 'dart:convert';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';

import 'spdx_expression.dart';

/// What [readModPackage] extracted from a `.topiaforgemod` zip.
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

/// Reads a `.topiaforgemod` package from raw bytes: hashes them, rejects
/// unsafe archive paths, and requires `topiaforge.mod.json` plus the manifest's
/// `entryAssembly` to be present. Throws [StateError] with a player-safe
/// message on any structural problem.
ModPackageSummary readModPackage(List<int> bytes) {
  final sha = sha256.convert(bytes).toString();
  final archive = SafeZipArchive.decode(bytes, label: 'Package');
  final entryNames = [
    for (final entry in archive.entries)
      if (entry.isFile) entry.name,
  ];
  final manifestFile = archive.entryNamed('topiaforge.mod.json');
  if (manifestFile == null || !manifestFile.isFile) {
    throw StateError('Package is missing topiaforge.mod.json.');
  }

  final ModManifest manifest;
  try {
    manifest = ModManifest.fromJson(
      jsonDecode(
            utf8.decode(
              manifestFile.readBytes(
                maxBytes: 1024 * 1024,
                label: 'topiaforge.mod.json',
              ),
              allowMalformed: false,
            ),
          )
          as Map<String, Object?>,
    );
  } on FormatException catch (error) {
    throw StateError(error.message);
  } on Object {
    throw StateError('topiaforge.mod.json in the package is not valid JSON.');
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
/// Registry publication holds manifests to the zero-finding bar: any
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

  if (existing != null) {
    issues.addAll(existing.validate());
  }

  final manifestIssues = manifest.validate();
  if (manifestIssues.isNotEmpty) {
    issues.addAll(manifestIssues);
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: manifest.id,
        message:
            'Registry publication requires a manifest with zero validation '
            'findings (${manifestIssues.length} found). Fix them with '
            '`topiaforge check package` and repack.',
      ),
    );
  }
  issues.addAll(
    validateManifestPublicationLicense(
      manifest,
      packageEntries: package.entryNames,
    ),
  );

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
            '(`topiaforge mod bump`) and repack instead of replacing it.',
      ),
    );
  }

  if (issues.any((issue) => issue.isBlocking)) {
    return RegistryEntryBuildResult(issues: issues);
  }

  final entry = RegistryEntryFile(
    id: manifest.id,
    extraFields: existing?.extraFields ?? const {},
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

/// Registry-publication licensing gate. The manifest carries a concrete SPDX
/// expression and names every package-relative license/notice file; the
/// hosted package must contain those exact regular-file entries.
List<LauncherIssue> validateManifestPublicationLicense(
  ModManifest manifest, {
  Iterable<String>? packageEntries,
}) {
  final issues = <LauncherIssue>[];
  final spdxIssue = SpdxExpressionValidator.validate(manifest.license);
  if (spdxIssue != null) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: manifest.id,
        message: spdxIssue,
      ),
    );
  }
  if (manifest.licenseFiles.isEmpty) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: manifest.id,
        message:
            'licenseFiles must list the license/notice files included in the package.',
      ),
    );
    return issues;
  }
  final normalized = manifest.licenseFiles
      .map((value) => value.trim())
      .toList();
  if (normalized.toSet().length != normalized.length ||
      normalized.any((value) => !isSafePackageRelativePath(value))) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: manifest.id,
        message: 'licenseFiles contains a duplicate or unsafe package path.',
      ),
    );
  }
  if (packageEntries != null) {
    final entries = packageEntries
        .map((value) => value.replaceAll('\\', '/'))
        .toSet();
    for (final file in normalized) {
      if (!entries.contains(file)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: manifest.id,
            message:
                'Declared license file is missing from the package: $file.',
          ),
        );
      }
    }
  }
  return issues;
}
