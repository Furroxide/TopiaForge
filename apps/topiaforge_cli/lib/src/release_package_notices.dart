import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

import 'bounded_file_reader.dart';
import 'release_package_io.dart';

/// Copies the license texts that correspond to the exact Dart packages linked
/// into the standalone CLI. Flutter emits its own NOTICES.Z inside the app.
class ReleasePackageNoticeWriter {
  const ReleasePackageNoticeWriter({
    required this.repositoryRoot,
    required this.fileOps,
  });

  final String repositoryRoot;
  final ReleaseFileOps fileOps;

  /// Copies every corresponding-source archive named by the vendored BepInEx
  /// provenance into the package.
  ///
  /// UnityDoorstop ships as `winhttp.dll` inside the loader runtime and is
  /// LGPL-2.1, so redistributing the binary obliges us to redistribute its
  /// source too. Shipping only `LICENSES/UnityDoorstop-LGPL-2.1.txt` satisfies
  /// the notice requirement and not the source one. The file list is read from
  /// `provenance.json` rather than hard-coded so a Doorstop bump cannot
  /// silently drop it, and a declared-but-missing archive throws instead of
  /// being quietly skipped.
  void copyBepInExCorrespondingSource(String destinationRoot) {
    final bepInExRoot = Directory(
      p.join(repositoryRoot, 'third_party', 'BepInEx'),
    );
    if (!bepInExRoot.existsSync()) {
      // Nothing is being redistributed, so nothing has to accompany it.
      return;
    }
    final provenanceFile = File(p.join(bepInExRoot.path, 'provenance.json'));
    if (!provenanceFile.existsSync()) {
      throw StateError(
        'Missing third_party/BepInEx/provenance.json; '
        'cannot resolve corresponding source.',
      );
    }
    final provenance =
        jsonDecode(
              readBoundedTextFileSync(provenanceFile, maxBytes: 256 * 1024),
            )
            as Map<String, Object?>;
    final entries = provenance['correspondingSource'];
    if (entries is! List || entries.isEmpty) {
      throw StateError(
        'third_party/BepInEx/provenance.json declares no corresponding source.',
      );
    }
    for (final entry in entries) {
      final relative = (entry as Map<String, Object?>)['file'] as String?;
      if (relative == null || relative.isEmpty || relative.contains('..')) {
        throw StateError('Invalid corresponding-source file entry: $relative');
      }
      final source = File(p.join(bepInExRoot.path, relative));
      if (!source.existsSync()) {
        throw StateError(
          'Corresponding source declared but missing: ${source.path}',
        );
      }
      fileOps.copyFileIfExists(
        source.path,
        p.join(destinationRoot, 'third_party', 'BepInEx', relative),
      );
    }
  }

  /// Copies and verifies notices for the exact managed validator dependencies
  /// that are installed beside the game-side loader.
  Future<void> copyRuntimeLoaderNotices(
    String destinationRoot, {
    String nugetPackagesRoot = '',
  }) async {
    final packagesRoot = _resolveNugetPackagesRoot(nugetPackagesRoot);
    final destination = Directory(
      p.join(destinationRoot, 'third_party', 'dotnet', 'runtime-loader'),
    )..createSync(recursive: true);
    final license = File(
      p.join(
        repositoryRoot,
        'third_party',
        'dotnet',
        'runtime-loader',
        'LICENSE.txt',
      ),
    );
    await _requireSha256(
      license,
      _dotnetRuntimeLoaderLicenseSha256,
      '.NET runtime-loader license',
    );
    fileOps.copyFileIfExists(
      license.path,
      p.join(destination.path, 'LICENSE.txt'),
    );

    final packages = <Map<String, Object>>[];
    for (final assembly in topiaForgeRuntimeLoaderAssemblies.where(
      (entry) => entry.isPinnedPackage,
    )) {
      final packageRoot = p.join(
        packagesRoot,
        assembly.packageId.toLowerCase(),
        assembly.packageVersion,
      );
      final packageStem = assembly.packageId.toLowerCase();
      final packageAssembly = File(
        p.join(packageRoot, 'lib', 'netstandard2.0', assembly.fileName),
      );
      final notices = File(p.join(packageRoot, 'THIRD-PARTY-NOTICES.TXT'));
      final nuspec = File(p.join(packageRoot, '$packageStem.nuspec'));
      await _requireSha256(
        packageAssembly,
        assembly.sha256,
        '${assembly.packageId} runtime assembly',
      );
      await _requireSha256(
        notices,
        assembly.thirdPartyNoticesSha256,
        '${assembly.packageId} third-party notices',
      );
      _validateRuntimePackageNuspec(nuspec, assembly);
      final noticesName = '${assembly.packageId}-ThirdPartyNotices.txt';
      fileOps.copyFileIfExists(
        notices.path,
        p.join(destination.path, noticesName),
      );
      packages.add({
        'id': assembly.packageId,
        'version': assembly.packageVersion,
        'assembly': assembly.fileName,
        'assemblyVersion': assembly.assemblyVersion,
        'sha256': assembly.sha256,
        'license': 'MIT',
        'licenseFile': 'LICENSE.txt',
        'thirdPartyNotices': noticesName,
        'thirdPartyNoticesSha256': assembly.thirdPartyNoticesSha256,
        'repository': 'https://github.com/dotnet/dotnet',
        'repositoryCommit': assembly.repositoryCommit,
      });
    }

    final profile = [
      for (final assembly in topiaForgeRuntimeProfileAssemblies)
        {
          'assembly': assembly.fileName,
          'assemblyVersion': assembly.assemblyVersion,
          'sha256': assembly.sha256,
          'providedBy': 'Robotopia build 2409 Unity/Mono profile',
        },
    ];
    File(p.join(destination.path, 'PROVENANCE.json')).writeAsStringSync(
      '${const JsonEncoder.withIndent('  ').convert({'schemaVersion': 1, 'packages': packages, 'playerProfileDependencies': profile})}\n',
      flush: true,
    );
  }

  void copyDartCliNotices(String destinationRoot) {
    final dartSdk = _resolveDartSdkRoot();
    final dartLicense = _firstExistingFile([
      p.join(dartSdk, 'LICENSE'),
      p.join(dartSdk, 'LICENSE.txt'),
    ]);
    if (dartLicense == null) {
      throw StateError('The Dart SDK license could not be located.');
    }

    final packageConfig = File(
      p.join(
        repositoryRoot,
        'apps',
        'topiaforge_cli',
        '.dart_tool',
        'package_config.json',
      ),
    );
    if (!packageConfig.existsSync()) {
      throw StateError(
        'CLI package_config.json is missing; run dart pub get first.',
      );
    }
    final decoded = readBoundedJsonObjectSync(
      packageConfig,
      maxBytes: CliFileLimits.metadata,
    );
    if (decoded['packages'] is! List) {
      throw StateError('CLI package_config.json has an invalid shape.');
    }
    final packages = <String, Map<String, dynamic>>{};
    for (final value in decoded['packages'] as List) {
      if (value is Map<String, dynamic> && value['name'] is String) {
        packages[value['name'] as String] = value;
      }
    }

    final destination = Directory(
      p.join(destinationRoot, 'third_party', 'dart', 'LICENSES'),
    )..createSync(recursive: true);
    fileOps.copyFileIfExists(
      dartLicense,
      p.join(destination.path, 'Dart-SDK-LICENSE.txt'),
    );

    final versions = <String, String>{};
    for (final name in _dartCliRuntimePackages) {
      final entry = packages[name];
      if (entry == null || entry['rootUri'] is! String) {
        throw StateError('The runtime Dart package $name is unresolved.');
      }
      final packageRoot = packageConfig.absolute.uri
          .resolve(entry['rootUri'] as String)
          .toFilePath();
      final license = _firstExistingFile([
        p.join(packageRoot, 'LICENSE'),
        p.join(packageRoot, 'LICENSE.txt'),
        p.join(packageRoot, 'LICENSE.md'),
        p.join(packageRoot, 'COPYING'),
      ]);
      if (license == null) {
        throw StateError('The runtime Dart package $name has no license file.');
      }
      final version = _readPubspecVersion(p.join(packageRoot, 'pubspec.yaml'));
      versions[name] = version;
      fileOps.copyFileIfExists(
        license,
        p.join(destination.path, '$name-LICENSE.txt'),
      );
    }

    final dartVersionFile = File(p.join(dartSdk, 'version'));
    if (!dartVersionFile.existsSync()) {
      throw StateError('The Dart SDK version file could not be located.');
    }
    File(p.join(destination.path, 'VERSIONS.json')).writeAsStringSync(
      '${const JsonEncoder.withIndent('  ').convert({'schemaVersion': 2, 'dartSdk': readBoundedTextFileSync(dartVersionFile, maxBytes: CliFileLimits.session).trim(), 'packages': versions})}\n',
      flush: true,
    );
  }

  String _resolveDartSdkRoot() {
    final candidates = <String>[
      if ((Platform.environment['DART_SDK'] ?? '').trim().isNotEmpty)
        Platform.environment['DART_SDK']!.trim(),
      p.join(repositoryRoot, '.fvm', 'flutter_sdk', 'bin', 'cache', 'dart-sdk'),
      if (p.basename(Platform.resolvedExecutable).startsWith('dart'))
        p.dirname(p.dirname(Platform.resolvedExecutable)),
    ];
    for (final candidate in candidates) {
      if (File(p.join(candidate, 'version')).existsSync()) {
        return candidate;
      }
    }
    throw StateError('The Dart SDK root could not be located.');
  }

  String _resolveNugetPackagesRoot(String configuredRoot) {
    final configured = configuredRoot.trim().isNotEmpty
        ? configuredRoot.trim()
        : (Platform.environment['NUGET_PACKAGES'] ?? '').trim();
    if (configured.isNotEmpty) {
      return configured;
    }
    final home =
        Platform.environment[Platform.isWindows ? 'USERPROFILE' : 'HOME'];
    if (home == null || home.trim().isEmpty) {
      throw StateError('The NuGet package cache root could not be located.');
    }
    return p.join(home, '.nuget', 'packages');
  }

  void _validateRuntimePackageNuspec(
    File nuspec,
    TopiaForgeRuntimeAssembly assembly,
  ) {
    if (!nuspec.existsSync()) {
      throw StateError('${assembly.packageId} package metadata is missing.');
    }
    final text = readBoundedTextFileSync(
      nuspec,
      maxBytes: CliFileLimits.metadata,
    );
    final exactIdentity =
        text.contains('<id>${assembly.packageId}</id>') &&
        text.contains('<version>${assembly.packageVersion}</version>');
    final declaresMit = RegExp(
      r'''<license\s+type=["']expression["']\s*>\s*MIT\s*</license>''',
      caseSensitive: false,
    ).hasMatch(text);
    final exactRepository =
        text.contains('url="https://github.com/dotnet/dotnet"') &&
        text.contains('commit="${assembly.repositoryCommit}"');
    if (!exactIdentity || !declaresMit || !exactRepository) {
      throw StateError(
        '${assembly.packageId} ${assembly.packageVersion} license or provenance metadata did not match the pinned package.',
      );
    }
  }

  Future<void> _requireSha256(File file, String expected, String label) async {
    if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
        FileSystemEntityType.file) {
      throw StateError('$label is missing or is not a regular file.');
    }
    final actual = (await sha256.bind(file.openRead()).first).toString();
    if (actual != expected) {
      throw StateError(
        '$label SHA-256 mismatch. Expected $expected but got $actual.',
      );
    }
  }

  String? _firstExistingFile(List<String> candidates) {
    for (final candidate in candidates) {
      if (File(candidate).existsSync()) {
        return candidate;
      }
    }
    return null;
  }

  String _readPubspecVersion(String path) {
    final file = File(path);
    if (!file.existsSync()) {
      throw StateError('Package pubspec is missing: $path');
    }
    final match = RegExp(r'^version:\s*([^\s#]+)', multiLine: true).firstMatch(
      readBoundedTextFileSync(file, maxBytes: CliFileLimits.manifest),
    );
    if (match == null) {
      throw StateError('Package pubspec has no version: $path');
    }
    return match.group(1)!;
  }
}

const _dartCliRuntimePackages = [
  'archive',
  'async',
  'boolean_selector',
  'collection',
  'crypto',
  'ffi',
  'http',
  'http_parser',
  'json_schema',
  'logging',
  'matcher',
  'meta',
  'path',
  'posix',
  'quiver',
  'rfc_6901',
  'source_span',
  'stack_trace',
  'stream_channel',
  'string_scanner',
  'term_glyph',
  'test_api',
  'typed_data',
  'unorm_dart',
  'uri',
  'web',
];

final List<String> dartCliLicenseNames = List.unmodifiable([
  'Dart-SDK-LICENSE.txt',
  'VERSIONS.json',
  for (final package in _dartCliRuntimePackages) '$package-LICENSE.txt',
]);

const runtimeLoaderNoticeNames = <String>[
  'LICENSE.txt',
  'PROVENANCE.json',
  'System.Collections.Immutable-ThirdPartyNotices.txt',
  'System.Reflection.Metadata-ThirdPartyNotices.txt',
];

const _dotnetRuntimeLoaderLicenseSha256 =
    'cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310';
