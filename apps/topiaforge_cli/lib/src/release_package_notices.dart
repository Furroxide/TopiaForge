import 'dart:convert';
import 'dart:io';

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
