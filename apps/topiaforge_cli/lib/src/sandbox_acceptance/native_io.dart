import 'dart:convert';
import 'dart:io';
import 'package:crypto/crypto.dart';
import 'package:launcher_data/launcher_data.dart';
import 'package:path/path.dart' as p;
import '../bounded_file_reader.dart';
import '../live_acceptance_models.dart';
import '../release_strict_json.dart';
import 'native_annex.dart';
import 'sandbox_json.dart';

const nativeInputLimit = 256 * 1024;
const nativeTranscriptLimit = 128 * 1024 * 1024;
const nativeSourceLimit = 4 * 1024 * 1024;

List<int> readNativeFile(
  String path, {
  int maximum = nativeInputLimit,
  bool allowEmpty = false,
}) {
  requireAcceptanceUnlinkedPath(path);
  final result = readBoundedRegularFileSync(
    File(path),
    maxBytes: maximum,
    allowEmpty: allowEmpty,
  );
  requireAcceptanceUnlinkedPath(path);
  return result;
}

String nativeHash(List<int> bytes) => sha256.convert(bytes).toString();
String nativeFileHash(String path, {int maximum = 256 * 1024 * 1024}) =>
    nativeHash(readNativeFile(path, maximum: maximum));
Map<String, Object?> readNativeDocument(String path) =>
    sandboxDocument(readNativeFile(path), 'private native input');

void writeNativeDocument(String path, Object value) {
  requireAcceptanceUnlinkedPath(path);
  final bytes = utf8.encode(
    '${const JsonEncoder.withIndent('  ').convert(value)}\n',
  );
  if (bytes.length > nativeTranscriptLimit) {
    throw StateError('Native output exceeds its bound.');
  }
  final file = File(path);
  file.createSync(exclusive: true);
  final handle = file.openSync(mode: FileMode.writeOnly);
  try {
    handle.writeFromSync(bytes);
    handle.flushSync();
  } finally {
    handle.closeSync();
  }
  requireAcceptanceUnlinkedPath(path);
}

String nativeChild(String root, String relative) {
  nativeRelativePath(relative);
  requireAcceptanceUnlinkedPath(root);
  final child = p.joinAll([root, ...relative.split('/')]);
  requireAcceptanceUnlinkedPath(child);
  if (!p.isWithin(p.normalize(root), p.normalize(child))) {
    throw StateError('Native child escaped its admitted root.');
  }
  return child;
}

final class SandboxNativePackage {
  SandboxNativePackage(this.id, this.path, this.receipt);
  final String id, path;
  final LiveAcceptancePackageReceipt receipt;
  Map<String, Object?> toJson() => {'id': id, ...receipt.toJson()};
}

final class SandboxNativePackageInventory {
  SandboxNativePackageInventory._(
    this.sourceRevision,
    Iterable<SandboxNativePackage> packages,
    this.sha256Digest,
  ) : packages = List.unmodifiable(packages);
  final String sourceRevision, sha256Digest;
  final List<SandboxNativePackage> packages;
  List<Map<String, Object?>> get receipts => [
    for (final package in packages) package.toJson(),
  ];

  static SandboxNativePackageInventory read(String path) {
    final bytes = readNativeFile(path);
    final value = sandboxDocument(bytes, 'native package inventory');
    sandboxFields(value, {
      'schemaVersion',
      'kind',
      'sourceRevision',
      'packages',
    }, 'native package inventory');
    if (value['schemaVersion'] is! int ||
        value['schemaVersion'] != 1 ||
        value['kind'] != 'sandbox-native-package-inventory-v1') {
      throw StateError('Unsupported native package inventory.');
    }
    final revision = nativeHex(value['sourceRevision'], 40);
    final packages = <SandboxNativePackage>[];
    final ids = <String>{};
    final archivePaths = <String>{};
    for (final row in nativeRows(value['packages'], 32, allowEmpty: false)) {
      sandboxFields(row, {'id', 'path', 'sha256'}, 'native package input');
      final id = sandboxText(row['id'], 'package ID', maximum: 128);
      final archivePath = sandboxText(
        row['path'],
        'package path',
        maximum: 1024,
      );
      if (!RegExp(r'^[a-z0-9]+(?:[.-][a-z0-9]+)*$').hasMatch(id) ||
          !p.isAbsolute(archivePath) ||
          p.normalize(archivePath) != archivePath ||
          !archivePath.endsWith('.topiaforgemod') ||
          !ids.add(id) ||
          !archivePaths.add(archivePath.toLowerCase())) {
        throw StateError(
          'Native package paths or identities are invalid/repeated.',
        );
      }
      nativeHex(row['sha256'], 64);
      final bytes = readNativeFile(archivePath, maximum: 256 * 1024 * 1024);
      if (nativeHash(bytes) != row['sha256']) {
        throw StateError('Native package archive bytes differ.');
      }
      final archive = SafeZipArchive.decode(
        bytes,
        label: 'Native fixture package',
      );
      final entry = archive.entryNamed('topiaforge.mod.json');
      if (entry == null || !entry.isFile) {
        throw StateError('Native package manifest is missing.');
      }
      final manifest = decodeReleaseObject(
        entry.readBytes(
          maxBytes: 2 * 1024 * 1024,
          label: 'native package manifest',
        ),
        maximumBytes: 2 * 1024 * 1024,
        label: 'native package manifest',
      );
      if (manifest['name'] != id || manifest['version'] != '0.1.0-rc.1') {
        throw StateError('Native package manifest identity/version differs.');
      }
      final paths = <String>{
        'topiaforge.mod.json',
        nativeRelativePath(manifest['entryAssembly']),
      };
      final apis = manifest['apiAssemblies'];
      if (apis != null) {
        if (apis is! List || apis.length > 128) {
          throw StateError('Native package APIs are unbounded.');
        }
        paths.addAll(apis.map(nativeRelativePath));
      }
      final multiplayer = manifest['multiplayer'];
      if (multiplayer != null) {
        final files = sandboxObject(
          multiplayer,
          'package multiplayer',
        )['synchronizedFiles'];
        if (files != null) {
          if (files is! List || files.length > 128) {
            throw StateError('Synchronized files are unbounded.');
          }
          paths.addAll(files.map(nativeRelativePath));
        }
      }
      final sorted = paths.toList()..sort();
      if (sorted.length > 128 ||
          sorted.map((s) => s.toLowerCase()).toSet().length != sorted.length) {
        throw StateError('Native critical paths collide.');
      }
      final critical = <LiveAcceptanceFileDigest>[];
      for (final path in sorted) {
        final file = archive.entryNamed(path);
        if (file == null || !file.isFile) {
          throw StateError('Native critical package file is missing.');
        }
        critical.add(
          LiveAcceptanceFileDigest(
            path: path,
            sha256: nativeHash(
              file.readBytes(
                maxBytes: 256 * 1024 * 1024,
                label: 'native critical file',
              ),
            ),
          ),
        );
      }
      packages.add(
        SandboxNativePackage(
          id,
          archivePath,
          LiveAcceptancePackageReceipt(
            sourceSha256: nativeHash(bytes),
            criticalFiles: List.unmodifiable(critical),
          ),
        ),
      );
    }
    for (final id in [
      'io.github.furroxide.topiaforge.sandbox',
      'dev.topiaforge.sandbox-acceptance',
      'dev.topiaforge.sandbox-acceptance-native',
    ]) {
      if (!ids.contains(id)) {
        throw StateError('Required native package $id is absent.');
      }
    }
    return SandboxNativePackageInventory._(
      revision,
      packages,
      nativeHash(bytes),
    );
  }
}

/// Captures actual working-tree bytes, including untracked nonignored source.
/// The record is private; it does not claim these bytes equal a candidate SHA.
List<int> captureSandboxSourceWorkspace(String root) {
  requireAcceptanceUnlinkedPath(root);
  String git(List<String> args) {
    final result = Process.runSync('git', [
      '-C',
      root,
      ...args,
    ], runInShell: false);
    if (result.exitCode != 0 ||
        result.stdout is! String ||
        (result.stdout as String).length > 4 * 1024 * 1024) {
      throw StateError('Cannot bound the source workspace inventory.');
    }
    return result.stdout as String;
  }

  final revision = nativeHex(git(['rev-parse', '--verify', 'HEAD']).trim(), 40);
  final status = git(['status', '--porcelain=v1', '--untracked-files=all']);
  final diff = git(['diff', '--no-ext-diff', '--binary', 'HEAD', '--']);
  final paths = git([
    'ls-files',
    '-z',
    '--cached',
    '--others',
    '--exclude-standard',
  ]).split('\u0000').where((s) => s.isNotEmpty).toSet().toList()..sort();
  if (paths.isEmpty || paths.length > 30000) {
    throw StateError('Source inventory is unbounded.');
  }
  final files = <Map<String, Object?>>[];
  for (final path in paths) {
    if (path.length > 512 ||
        p.posix.isAbsolute(path) ||
        path.contains(r'\') ||
        path.split('/').any((s) => s.isEmpty || s == '.' || s == '..')) {
      throw StateError('Source inventory contains an unsafe path.');
    }
    final full = p.joinAll([root, ...path.split('/')]);
    requireAcceptanceUnlinkedPath(full);
    if (FileSystemEntity.typeSync(full, followLinks: false) ==
        FileSystemEntityType.notFound) {
      files.add({'path': path, 'state': 'deleted'});
      continue;
    }
    final bytes = readNativeFile(
      full,
      maximum: 256 * 1024 * 1024,
      allowEmpty: true,
    );
    files.add({
      'path': path,
      'state': 'present',
      'sha256': nativeHash(bytes),
      'length': bytes.length,
    });
  }
  final bytes = utf8.encode(
    jsonEncode({
      'schemaVersion': 1,
      'kind': 'sandbox-source-workspace-v1',
      'sourceRevision': revision,
      'dirty': status.isNotEmpty,
      'statusSha256': nativeHash(utf8.encode(status)),
      'diffSha256': nativeHash(utf8.encode(diff)),
      'files': files,
    }),
  );
  if (bytes.length > nativeSourceLimit) {
    throw StateError('Source inventory exceeds its byte bound.');
  }
  return bytes;
}

Map<String, Object?> decodeNativeSourceWorkspace(
  List<int> bytes,
  String revision,
) {
  final value = decodeReleaseObject(
    bytes,
    maximumBytes: nativeSourceLimit,
    label: 'private source workspace',
  );
  sandboxFields(value, {
    'schemaVersion',
    'kind',
    'sourceRevision',
    'dirty',
    'statusSha256',
    'diffSha256',
    'files',
  }, 'private source workspace');
  if (value['schemaVersion'] is! int ||
      value['schemaVersion'] != 1 ||
      value['kind'] != 'sandbox-source-workspace-v1' ||
      value['sourceRevision'] != revision ||
      value['dirty'] is! bool) {
    throw StateError('Private source workspace identity differs.');
  }
  nativeHex(value['statusSha256'], 64);
  nativeHex(value['diffSha256'], 64);
  final rows = nativeRows(value['files'], 30000, allowEmpty: false);
  final paths = <String>{};
  String? previous;
  for (final row in rows) {
    final deleted = row['state'] == 'deleted';
    sandboxFields(row, {
      'path',
      'state',
      if (!deleted) 'sha256',
      if (!deleted) 'length',
    }, 'source file');
    final path = sandboxText(row['path'], 'source path', maximum: 512);
    if (p.posix.isAbsolute(path) ||
        path.contains(':') ||
        path.contains(r'\') ||
        path
            .split('/')
            .any(
              (s) =>
                  s.isEmpty ||
                  s == '.' ||
                  s == '..' ||
                  s.toLowerCase() == '.git',
            ) ||
        !paths.add(path.toLowerCase()) ||
        (previous != null && previous.compareTo(path) >= 0)) {
      throw StateError('Source file paths alias or are not strictly ordered.');
    }
    previous = path;
    if (!deleted) {
      if (row['state'] != 'present') {
        throw StateError('Unknown source file state.');
      }
      nativeHex(row['sha256'], 64);
      nativeInteger(row['length'], 0, 256 * 1024 * 1024);
    }
  }
  return value;
}

void writeNativeBytes(String path, List<int> bytes) {
  requireAcceptanceUnlinkedPath(path);
  final file = File(path)..createSync(exclusive: true);
  final output = file.openSync(mode: FileMode.writeOnly);
  try {
    output.writeFromSync(bytes);
    output.flushSync();
  } finally {
    output.closeSync();
  }
  requireAcceptanceUnlinkedPath(path);
}

List<int> verifySandboxSourceSnapshot(String root, String manifestPath) {
  requireAcceptanceUnlinkedPath(root);
  final bytes = readNativeFile(manifestPath, maximum: nativeSourceLimit);
  final raw = decodeReleaseObject(
    bytes,
    maximumBytes: nativeSourceLimit,
    label: 'QA source snapshot input',
  );
  final value = decodeNativeSourceWorkspace(
    bytes,
    nativeHex(raw['sourceRevision'], 40),
  );
  final sourceRows = nativeRows(value['files'], 30000, allowEmpty: false);
  final permitted = <String>{
    for (final row in sourceRows)
      if (row['state'] == 'present') (row['path']! as String).toLowerCase(),
    for (final name in topiaForgeRuntimeLoaderDlls)
      'src/TopiaForge.ModManager/bin/Release/netstandard2.1/$name'
          .toLowerCase(),
  };
  final pending = <Directory>[Directory(root)];
  var visited = 0;
  while (pending.isNotEmpty) {
    final entries = pending.removeLast().listSync(followLinks: false);
    visited += entries.length;
    if (visited > 60000) {
      throw StateError('QA source snapshot directory inventory is unbounded.');
    }
    for (final entry in entries) {
      requireAcceptanceUnlinkedPath(entry.path);
      if (entry is Directory) {
        pending.add(entry);
      } else if (entry is! File ||
          !permitted.contains(
            p
                .relative(entry.path, from: root)
                .replaceAll(r'\', '/')
                .toLowerCase(),
          )) {
        throw StateError(
          'QA source snapshot contains an unreviewed extra input.',
        );
      }
    }
  }
  for (final row in sourceRows) {
    final path = p.joinAll([root, ...(row['path']! as String).split('/')]);
    requireAcceptanceUnlinkedPath(path);
    if (row['state'] == 'deleted') {
      if (FileSystemEntity.typeSync(path, followLinks: false) !=
          FileSystemEntityType.notFound) {
        throw StateError('QA source snapshot contains a deleted source input.');
      }
    } else {
      final actual = readNativeFile(
        path,
        maximum: 256 * 1024 * 1024,
        allowEmpty: true,
      );
      if (actual.length != row['length'] ||
          nativeHash(actual) != row['sha256']) {
        throw StateError(
          'QA source snapshot bytes differ from its private input inventory.',
        );
      }
    }
  }
  return bytes;
}

Map<String, Object?> captureSandboxRuntimeFiles(
  String repositoryRoot,
  AcceptanceIsolationContext context,
) {
  final files = <Map<String, Object?>>[];
  for (final name in topiaForgeRuntimeLoaderDlls) {
    final source = p.join(
      repositoryRoot,
      'src',
      'TopiaForge.ModManager',
      'bin',
      'Release',
      'netstandard2.1',
      name,
    );
    final installed = p.join(
      context.gameRoot,
      'BepInEx',
      'plugins',
      'TopiaForge.ModManager',
      name,
    );
    final sourceBytes = readNativeFile(source, maximum: 128 * 1024 * 1024);
    final actualBytes = readNativeFile(installed, maximum: 128 * 1024 * 1024);
    if (nativeHash(sourceBytes) != nativeHash(actualBytes)) {
      throw StateError('Installed QA loader differs from the staged runtime.');
    }
    files.add({
      'path': 'BepInEx/plugins/TopiaForge.ModManager/$name',
      'sha256': nativeHash(actualBytes),
      'length': actualBytes.length,
    });
  }
  return {
    'schemaVersion': 1,
    'kind': 'sandbox-runtime-files-v1',
    'files': files,
  };
}
