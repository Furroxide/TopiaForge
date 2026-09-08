import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:path/path.dart' as p;

import 'acceptance_isolation_identity.dart';
import 'json_duplicate_properties.dart';
import 'launch_process_control.dart';

/// Private, explicitly provisioned acceptance layout. Admission never creates
/// folders, loads another user's profile or derives identity from environment.
final class AcceptanceIsolationContext {
  AcceptanceIsolationContext._(
    this.recordPath,
    this.recordSha256,
    this.kind,
    this.sourceGameRoot,
    this.gameRoot,
    this.launcherRoot,
    this.outputRoot,
    this.persistentDataRoot,
    this.normalUserProfile,
    this.identity,
    this._probe,
  );

  static const environmentVariable = 'TOPIAFORGE_ACCEPTANCE_REQUEST_ID';
  final String recordPath;
  final String recordSha256;
  final String kind;
  final String sourceGameRoot;
  final String gameRoot;
  final String launcherRoot;
  final String outputRoot;
  final String persistentDataRoot;
  final String normalUserProfile;
  final WindowsAcceptanceIdentity identity;
  final WindowsAcceptanceIdentity Function() _probe;
  String get bepInExRoot => p.join(gameRoot, 'BepInEx');
  String get managerRoot => p.join(bepInExRoot, 'TopiaForge');

  static AcceptanceIsolationContext admit({
    required String recordPath,
    required String sourceGameRoot,
    required String outputRoot,
    WindowsAcceptanceIdentity Function()? identityReader,
  }) {
    if (recordPath.isEmpty) {
      throw StateError(
        'Acceptance requires an approved isolated QA layout record.',
      );
    }
    final bytes = readAcceptanceRecord(File(recordPath));
    final raw = decodeAcceptanceObject(bytes);
    requireAcceptanceKeys(raw, const {
      'schemaVersion',
      'kind',
      'sourceGameRoot',
      'gameRoot',
      'launcherRoot',
      'outputRoot',
      'persistentDataRoot',
      'userSid',
      'userProfile',
      'localAppDataLow',
      'normalUserSid',
      'normalUserProfile',
      'reviewerEvidence',
    });
    if (raw['schemaVersion'] != 1 ||
        raw['schemaVersion'] is! int ||
        !['windows-user', 'virtual-machine'].contains(raw['kind'])) {
      throw const FormatException('Unsupported acceptance isolation record.');
    }
    final evidence = acceptanceText(raw, 'reviewerEvidence', 1024);
    if (evidence.trim().isEmpty) {
      throw const FormatException('QA provisioning evidence is required.');
    }
    final probe = identityReader ?? readWindowsAcceptanceIdentity;
    final actual = probe();
    if (actual.userSid != acceptanceText(raw, 'userSid', 256) ||
        actual.userSid == acceptanceText(raw, 'normalUserSid', 256) ||
        !sameAcceptancePath(
          actual.userProfile,
          acceptanceText(raw, 'userProfile'),
        ) ||
        !sameAcceptancePath(
          actual.localAppDataLow,
          acceptanceText(raw, 'localAppDataLow'),
        )) {
      throw StateError(
        'The current Windows user and native profile do not match isolated QA provisioning.',
      );
    }
    final context = AcceptanceIsolationContext._(
      p.normalize(p.absolute(recordPath)),
      sha256.convert(bytes).toString(),
      raw['kind']! as String,
      acceptancePath(raw, 'sourceGameRoot'),
      acceptancePath(raw, 'gameRoot'),
      acceptancePath(raw, 'launcherRoot'),
      acceptancePath(raw, 'outputRoot'),
      acceptancePath(raw, 'persistentDataRoot'),
      acceptancePath(raw, 'normalUserProfile'),
      actual,
      probe,
    );
    if (!sameAcceptancePath(sourceGameRoot, context.sourceGameRoot) ||
        !sameAcceptancePath(outputRoot, context.outputRoot)) {
      throw StateError(
        'Acceptance arguments do not match the approved isolated layout.',
      );
    }
    context.verify();
    return context;
  }

  void verify() {
    if (!_probe().matches(identity) ||
        sha256.convert(readAcceptanceRecord(File(recordPath))).toString() !=
            recordSha256) {
      throw StateError(
        'The admitted acceptance identity or provisioning record changed.',
      );
    }
    for (final path in [
      sourceGameRoot,
      gameRoot,
      bepInExRoot,
      managerRoot,
      launcherRoot,
      outputRoot,
      persistentDataRoot,
      identity.userProfile,
      identity.localAppDataLow,
    ]) {
      requireAcceptanceUnlinkedPath(path);
    }
    for (final path in [bepInExRoot, launcherRoot, outputRoot]) {
      _requireOrdinaryAcceptanceTree(path);
    }
    if (!Directory(gameRoot).existsSync() ||
        !Directory(sourceGameRoot).existsSync()) {
      throw StateError(
        'The source and separately provisioned acceptance game roots must exist.',
      );
    }
    for (final path in [
      gameRoot,
      launcherRoot,
      outputRoot,
      persistentDataRoot,
    ]) {
      if (acceptancePathsOverlap(path, sourceGameRoot) ||
          acceptancePathsOverlap(path, normalUserProfile)) {
        throw StateError(
          'Acceptance writable roots overlap the normal installation or user profile.',
        );
      }
    }
    if (!p.isWithin(
          identity.localAppDataLow.toLowerCase(),
          persistentDataRoot.toLowerCase(),
        ) ||
        sameAcceptancePath(identity.userProfile, normalUserProfile)) {
      throw StateError(
        'Native persistent data is not inside the isolated OS known folder.',
      );
    }
    if (acceptancePathsOverlap(gameRoot, launcherRoot) ||
        acceptancePathsOverlap(gameRoot, outputRoot) ||
        acceptancePathsOverlap(launcherRoot, outputRoot)) {
      throw StateError(
        'Acceptance game, launcher and evidence roots must be separate.',
      );
    }
  }

  Map<String, Object?> runtimeRoots() => {
    'gameRoot': gameRoot,
    'bepInExRoot': bepInExRoot,
    'managerRoot': managerRoot,
    'persistentDataRoot': persistentDataRoot,
  };
}

const acceptanceDocumentLimit = 64 * 1024;
List<int> readAcceptanceRecord(File file) {
  requireAcceptanceUnlinkedPath(file.path);
  if (FileSystemEntity.typeSync(file.path, followLinks: false) !=
          FileSystemEntityType.file ||
      file.lengthSync() <= 0 ||
      file.lengthSync() > acceptanceDocumentLimit) {
    throw const FormatException(
      'Acceptance record must be a bounded regular file.',
    );
  }
  final bytes = file.readAsBytesSync();
  if (bytes.length > acceptanceDocumentLimit) {
    throw const FormatException('Acceptance record grew.');
  }
  return bytes;
}

Map<String, Object?> decodeAcceptanceObject(List<int> bytes) {
  if (bytes.isEmpty || bytes.length > acceptanceDocumentLimit) {
    throw const FormatException('Acceptance document exceeds its limit.');
  }
  final text = utf8.decode(bytes, allowMalformed: false);
  rejectDuplicateJsonProperties(text, label: 'Acceptance isolation');
  final value = jsonDecode(text);
  if (value is! Map<String, Object?>) {
    throw const FormatException('Acceptance document must be an object.');
  }
  return value;
}

void requireAcceptanceKeys(Map<String, Object?> value, Set<String> keys) {
  if (value.length != keys.length || !value.keys.every(keys.contains)) {
    throw const FormatException(
      'Acceptance document has missing or unknown fields.',
    );
  }
}

String acceptanceText(
  Map<String, Object?> value,
  String key, [
  int max = 32767,
]) {
  final text = value[key];
  if (text is! String ||
      text.isEmpty ||
      text.length > max ||
      text.codeUnits.any((c) => c < 32)) {
    throw FormatException('Invalid acceptance $key.');
  }
  return text;
}

String acceptancePath(Map<String, Object?> value, String key) {
  final path = acceptanceText(value, key);
  requireAcceptanceUnlinkedPath(path);
  return p.normalize(path);
}

bool sameAcceptancePath(String a, String b) =>
    p.normalize(p.absolute(a)).toLowerCase() ==
    p.normalize(p.absolute(b)).toLowerCase();
bool acceptancePathsOverlap(String a, String b) {
  final left = p.normalize(p.absolute(a)).toLowerCase();
  final right = p.normalize(p.absolute(b)).toLowerCase();
  return left == right || p.isWithin(left, right) || p.isWithin(right, left);
}

void requireAcceptanceUnlinkedPath(String path) {
  if (!p.isAbsolute(path) ||
      path.codeUnits.any((c) => c < 32) ||
      path.startsWith(r'\\') ||
      p.split(path).any((part) => part == '.' || part == '..') ||
      path.contains('*') ||
      path.contains('?')) {
    throw const FormatException(
      'Acceptance requires an absolute local path without aliases.',
    );
  }
  final parts = p.split(p.normalize(path));
  if (Platform.isWindows &&
      parts
          .skip(1)
          .any(
            (part) =>
                part.endsWith('.') ||
                part.endsWith(' ') ||
                RegExp(r'[<>:"|]').hasMatch(part) ||
                RegExp(
                  r'^(?:CON|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\.|$)',
                  caseSensitive: false,
                ).hasMatch(part),
          )) {
    throw const FormatException(
      'Acceptance Windows paths cannot use stream or device aliases.',
    );
  }
  var current = parts.first;
  for (final part in parts.skip(1)) {
    current = p.join(current, part);
    final type = FileSystemEntity.typeSync(current, followLinks: false);
    if (type != FileSystemEntityType.notFound &&
        type != FileSystemEntityType.link) {
      final resolved = type == FileSystemEntityType.directory
          ? Directory(current).resolveSymbolicLinksSync()
          : File(current).resolveSymbolicLinksSync();
      if (!sameAcceptancePath(current, resolved)) {
        throw const FormatException(
          'Acceptance paths must use their actual local filesystem names.',
        );
      }
    }
    if (type == FileSystemEntityType.link ||
        type != FileSystemEntityType.notFound &&
            type != FileSystemEntityType.directory &&
            current != p.normalize(path)) {
      throw const FormatException(
        'Acceptance paths cannot contain linked or non-directory ancestors.',
      );
    }
  }
}

// A copied loader/profile tree may contain a link beneath an otherwise safe
// root. Inspect writable descendants as well; do not follow them while walking.
void _requireOrdinaryAcceptanceTree(String root) {
  if (!Directory(root).existsSync()) return;
  var inspected = 0;
  for (final entry in Directory(
    root,
  ).listSync(recursive: true, followLinks: false)) {
    if (++inspected > 65536 ||
        FileSystemEntity.typeSync(entry.path, followLinks: false) ==
            FileSystemEntityType.link) {
      throw const FormatException(
        'Acceptance writable trees cannot contain links or exceed their inspection bound.',
      );
    }
  }
}
