import 'dart:convert';
import 'dart:io';

import 'bounded_file_reader.dart';

/// Hosted pub packages linked into the standalone `topiaforge` executable.
///
/// This is the runtime (non-dev) dependency closure of `apps/topiaforge_cli`
/// without its first-party path packages. The CLI licence bundle copies one
/// licence text per entry and the release SBOM describes one package per
/// entry, so both read this one list; `release_spdx_third_party_test.dart`
/// fails when it drifts from the resolved package graph.
const dartCliRuntimePackages = <String>[
  'archive',
  'async',
  'boolean_selector',
  'collection',
  'crypto',
  'cryptography',
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

/// Licence, notice and patent-grant files that [dartCliRuntimePackages]
/// entries carry beside their primary licence.
///
/// Apache-2.0 section 4(d) requires a NOTICE file's attributions to travel
/// with redistributions, and `archive`'s `LICENSE-other.md` holds the notices
/// of the zlib, JZLib, bzip2 and Bouncy Castle code it derives from, so the
/// licence bundle copies each file here as `<package>-<file>`. Packaging fails
/// when a package carries such a file this map does not name, so a dependency
/// update cannot drop one silently.
const dartCliSupplementaryNotices = <String, List<String>>{
  'archive': ['LICENSE-other.md'],
  'quiver': ['NOTICE', 'PATENTS'],
  'uri': ['PATENTS'],
};

/// Hosted pub packages compiled into the Flutter launcher.
///
/// The runtime dependency closure of `apps/topiaforge_launcher_flutter`
/// without the SDK-provided packages, which are part of Flutter itself, and
/// without the first-party path packages. Flutter writes the matching notices
/// into the launcher's `NOTICES.Z`; the release SBOM describes one package per
/// entry.
const launcherRuntimePackages = <String>[
  'archive',
  'async',
  'bloc',
  'bloc_concurrency',
  'characters',
  'collection',
  'cross_file',
  'crypto',
  'cryptography',
  'ffi',
  'file_selector',
  'file_selector_android',
  'file_selector_ios',
  'file_selector_linux',
  'file_selector_macos',
  'file_selector_platform_interface',
  'file_selector_web',
  'file_selector_windows',
  'flutter_bloc',
  'http',
  'http_parser',
  'material_color_utilities',
  'meta',
  'nested',
  'path',
  'plugin_platform_interface',
  'posix',
  'provider',
  'source_span',
  'stream_transform',
  'string_scanner',
  'term_glyph',
  'typed_data',
  'unorm_dart',
  'vector_math',
  'web',
];

/// The Dart executables a release ships, keyed by release-catalog component:
/// the tracked lockfile that pins each one and the hosted packages it links.
const releaseDartShipments =
    <String, ({String lockfile, List<String> packages})>{
      'cli': (
        lockfile: 'apps/topiaforge_cli/pubspec.lock',
        packages: dartCliRuntimePackages,
      ),
      'launcher': (
        lockfile: 'apps/topiaforge_launcher_flutter/pubspec.lock',
        packages: launcherRuntimePackages,
      ),
    };

/// One package entry of a pub-generated `pubspec.lock`.
typedef LockedPubPackage = ({String dependency, String source, String version});

/// Reads every package entry of a pub-generated `pubspec.lock`.
///
/// Parses pub's fixed lockfile layout instead of taking a YAML dependency: a
/// two-space-indented name opens an entry, and its four-space `dependency`,
/// `source` and `version` fields describe it.
Map<String, LockedPubPackage> readPubLockfile(File lockfile) {
  final fields = <String, Map<String, String>>{};
  String? current;
  final text = readBoundedTextFileSync(
    lockfile,
    maxBytes: CliFileLimits.metadata,
  );
  for (final raw in const LineSplitter().convert(text)) {
    final line = raw.trimRight();
    final package = RegExp(r'^  ([A-Za-z0-9_]+):$').firstMatch(line);
    if (package != null) {
      current = package.group(1)!;
      fields[current] = {};
      continue;
    }
    if (!line.startsWith('  ')) {
      current = null;
      continue;
    }
    final field = RegExp(
      r'^    (dependency|source|version): "?([^"]+?)"?$',
    ).firstMatch(line);
    if (current != null && field != null) {
      fields[current]![field.group(1)!] = field.group(2)!;
    }
  }
  return {
    for (final entry in fields.entries)
      entry.key: (
        dependency: entry.value['dependency'] ?? '',
        source: entry.value['source'] ?? '',
        version: entry.value['version'] ?? '',
      ),
  };
}

/// Exact versions a tracked `pubspec.lock` pins for the hosted [names].
///
/// Fails closed: a name the lockfile does not pin as a hosted package is an
/// error, never a component silently left out of the release record.
Map<String, String> readLockedHostedVersions(
  File lockfile,
  Iterable<String> names,
) {
  final locked = readPubLockfile(lockfile);
  final versions = <String, String>{};
  for (final name in names) {
    final entry = locked[name];
    if (entry == null || entry.source != 'hosted' || entry.version.isEmpty) {
      throw StateError(
        '${lockfile.path} does not pin $name as a hosted package.',
      );
    }
    versions[name] = entry.version;
  }
  return versions;
}
