import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;

import 'live_acceptance_models.dart';

LiveAcceptancePackageReceipt readLiveAcceptancePackageReceipt(
  String packagePath,
) {
  try {
    return _inspectPackage(packagePath).receipt;
  } on LiveAcceptanceError {
    rethrow;
  } on Object catch (error) {
    throw LiveAcceptanceError(
      'TFACCEPT121',
      'The exact acceptance package receipt could not be derived: $error',
      'Rebuild the package from the frozen source and retry.',
    );
  }
}

LiveAcceptancePackageReceipt readGeneratedJourneyReceipt(
  LiveAcceptanceOptions options,
) {
  final packageDirectory = Directory(
    p.join(options.devProjectPath, 'bin', 'TopiaForgeDev', 'Release'),
  );
  if (FileSystemEntity.typeSync(packageDirectory.path, followLinks: false) !=
      FileSystemEntityType.directory) {
    throw const LiveAcceptanceError(
      'TFACCEPT122',
      'The packaged CLI did not retain its generated journey package.',
      'Use the exact packaged CLI development loop and do not remove its '
          'Release output before acceptance completes.',
    );
  }
  final matches = <_PackageInspection>[];
  final entries = packageDirectory.listSync(followLinks: false);
  if (entries.length > 64) {
    throw const LiveAcceptanceError(
      'TFACCEPT122',
      'The generated journey package directory is unbounded.',
      'Use a fresh generated release-journey project.',
    );
  }
  for (final entry in entries) {
    if (entry is! File ||
        !entry.path.endsWith('.topiaforgemod') ||
        !_isRegularFile(entry)) {
      continue;
    }
    final inspected = _inspectPackage(entry.path);
    if (inspected.id == options.requiredLoadedPackageId) {
      matches.add(inspected);
    }
  }
  if (matches.length != 1) {
    throw LiveAcceptanceError(
      'TFACCEPT122',
      'Expected exactly one generated package for '
          '${options.requiredLoadedPackageId}; found ${matches.length}.',
      'Use a fresh generated release-journey project and rerun.',
    );
  }
  return matches.single.receipt;
}

_PackageInspection _inspectPackage(String packagePath) {
  final file = File(packagePath);
  if (!_isRegularFile(file)) {
    throw StateError('package is not a regular file');
  }
  final packageBytes = file.readAsBytesSync();
  final archive = SafeZipArchive.decode(
    packageBytes,
    label: 'Live acceptance package',
  );
  final manifestEntry = archive.entryNamed('topiaforge.mod.json');
  if (manifestEntry == null || !manifestEntry.isFile) {
    throw StateError('topiaforge.mod.json is missing');
  }
  final manifestBytes = manifestEntry.readBytes(
    maxBytes: 2 * 1024 * 1024,
    label: 'Live acceptance package manifest',
  );
  final decoded = jsonDecode(utf8.decode(manifestBytes, allowMalformed: false));
  if (decoded is! Map ||
      decoded['name'] is! String ||
      decoded['entryAssembly'] is! String) {
    throw StateError('package manifest identity is invalid');
  }
  final id = decoded['name'] as String;
  if (!RegExp(r'^[a-z0-9][a-z0-9._-]{0,127}$').hasMatch(id)) {
    throw StateError('package manifest id is invalid');
  }
  final criticalPaths = <String>{'topiaforge.mod.json'};
  void addPath(Object? value) {
    if (value is! String ||
        value.isEmpty ||
        value.length > 512 ||
        value.contains(r'\') ||
        value.startsWith('/') ||
        value.endsWith('/') ||
        value.contains('//') ||
        value.split('/').any((part) => part == '.' || part == '..')) {
      throw StateError('package critical-file path is invalid');
    }
    criticalPaths.add(value);
  }

  addPath(decoded['entryAssembly']);
  final apiAssemblies = decoded['apiAssemblies'];
  if (apiAssemblies != null) {
    if (apiAssemblies is! List || apiAssemblies.length > 256) {
      throw StateError('package API assembly inventory is invalid');
    }
    for (final path in apiAssemblies) {
      addPath(path);
    }
  }
  final multiplayer = decoded['multiplayer'];
  if (multiplayer != null) {
    if (multiplayer is! Map) {
      throw StateError('package multiplayer metadata is invalid');
    }
    final synchronizedFiles = multiplayer['synchronizedFiles'];
    if (synchronizedFiles != null) {
      if (synchronizedFiles is! List || synchronizedFiles.length > 1024) {
        throw StateError('package synchronized-file inventory is invalid');
      }
      for (final path in synchronizedFiles) {
        addPath(path);
      }
    }
  }
  final sortedPaths = criticalPaths.toList()..sort();
  final criticalFiles = <LiveAcceptanceFileDigest>[];
  for (final path in sortedPaths) {
    final entry = archive.entryNamed(path);
    if (entry == null || !entry.isFile) {
      throw StateError('critical package entry is missing: $path');
    }
    final bytes = entry.readBytes(
      maxBytes: 256 * 1024 * 1024,
      label: 'Critical package entry',
    );
    criticalFiles.add(
      LiveAcceptanceFileDigest(
        path: path,
        sha256: sha256.convert(bytes).toString(),
      ),
    );
  }
  return _PackageInspection(
    id,
    LiveAcceptancePackageReceipt(
      sourceSha256: sha256.convert(packageBytes).toString(),
      criticalFiles: List.unmodifiable(criticalFiles),
    ),
  );
}

final class LiveAcceptanceManagerLine {
  const LiveAcceptanceManagerLine({
    required this.level,
    required this.source,
    required this.message,
  });

  final String level;
  final String source;
  final String message;
}

LiveAcceptanceManagerLine? tryParseLiveAcceptanceManagerLine(String line) {
  final match = RegExp(
    r'^\S+ \[(DEBUG|INFO|WARN|ERROR)\] '
    r'\[([a-z0-9][a-z0-9._-]{0,127})\] ([^\r\n]*)$',
  ).firstMatch(line);
  if (match == null) return null;
  return LiveAcceptanceManagerLine(
    level: match.group(1)!,
    source: match.group(2)!,
    message: match.group(3)!,
  );
}

bool _isRegularFile(File file) =>
    FileSystemEntity.typeSync(file.path, followLinks: false) ==
    FileSystemEntityType.file;

final class _PackageInspection {
  const _PackageInspection(this.id, this.receipt);

  final String id;
  final LiveAcceptancePackageReceipt receipt;
}
