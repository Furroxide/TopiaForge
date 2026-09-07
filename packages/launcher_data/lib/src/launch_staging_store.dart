import 'dart:convert';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;

import 'launch_storage_keys.dart';

/// One bounded launch protocol directory beneath a caller-established game root.
final class LaunchStagingStore {
  LaunchStagingStore(String trustedGameRoot)
    : trustedGameRoot = p.normalize(p.absolute(trustedGameRoot));
  final String trustedGameRoot;
  static const documentLimit = 4 * 1024 * 1024;
  static const observationLimit = 16 * 1024 * 1024;
  String get _staging =>
      p.join(trustedGameRoot, 'BepInEx', 'TopiaForge', 'staging');

  /// Takes a nonblocking OS lease. The persistent inode is never removed:
  /// removing a locked file could let another process lock a replacement inode.
  Future<LaunchStagingLease?> acquireLaunchLease() async {
    _directories(create: true);
    final path = p.join(_staging, 'launch-admission.lock');
    final key = Platform.isWindows ? path.toLowerCase() : path;
    if (!_activeLaunchLeases.add(key)) return null;
    RandomAccessFile? handle;
    var retained = false;
    try {
      final file = File(path);
      _ordinaryFile(file, allowMissing: true);
      handle = await file.open(mode: FileMode.append);
      _directories();
      _ordinaryFile(file);
      try {
        await handle.lock();
      } on FileSystemException {
        return null;
      }
      retained = true;
      return LaunchStagingLease._(handle, key);
    } finally {
      if (!retained) {
        try {
          await handle?.close();
        } finally {
          _activeLaunchLeases.remove(key);
        }
      }
    }
  }

  Future<File> writeRequest(ProfileLaunchConfigurationV4 value) async {
    final payload = jsonEncode(value.toJson());
    final bytes = utf8.encode(payload);
    if (bytes.length > documentLimit) {
      throw const FormatException('Launch request exceeds its byte limit.');
    }
    final file = _channel('launch-profile', value.requestId);
    _directories(create: true);
    _ordinaryFile(file, allowMissing: true);
    if (file.existsSync()) {
      throw FileSystemException(
        'Launch request identity already exists.',
        file.path,
      );
    }
    final random = Random.secure();
    final nonce = List.generate(
      16,
      (_) => random.nextInt(256).toRadixString(16).padLeft(2, '0'),
    ).join();
    final temp = File('${file.path}.tmp-$nonce');
    var created = false;
    try {
      temp.createSync(exclusive: true);
      created = true;
      _directories();
      _ordinaryFile(temp);
      final output = await temp.open(mode: FileMode.write);
      try {
        await output.writeFrom(bytes);
        await output.flush();
      } finally {
        await output.close();
      }
      _directories();
      _ordinaryFile(temp);
      _ordinaryFile(file, allowMissing: true);
      if (file.existsSync()) {
        throw FileSystemException(
          'Launch request identity was claimed during publication.',
          file.path,
        );
      }
      temp.renameSync(file.path);
      created = false;
      return file;
    } finally {
      if (created) {
        try {
          _directories();
          _ordinaryFile(temp, allowMissing: true);
          if (temp.existsSync()) temp.deleteSync();
        } on Object {
          /* Never follow a substituted ancestor to remove an unowned file. */
        }
      }
    }
  }

  Future<void> deleteRequest(ProfileLaunchConfigurationV4 expected) async {
    final file = _channel('launch-profile', expected.requestId);
    try {
      final json = await _read(file, documentLimit);
      if (json == null) return;
      final actual = ProfileLaunchConfigurationV4.fromJson(json);
      if (jsonEncode(actual.toJson()) != jsonEncode(expected.toJson())) return;
      _directories();
      _ordinaryFile(file);
      // Read again immediately before deletion; mismatched replacements are not ours.
      final replacement = await _read(file, documentLimit);
      if (replacement == null) return;
      final current = ProfileLaunchConfigurationV4.fromJson(replacement);
      _directories();
      _ordinaryFile(file);
      if (jsonEncode(current.toJson()) == jsonEncode(expected.toJson())) {
        file.deleteSync();
      }
    } on Object {
      /* Failed or foreign requests remain inspectable, never swept. */
    }
  }

  Future<LaunchProgress?> readProgress(String requestId) async {
    try {
      final raw = await _read(
        _channel('launch-progress', requestId),
        documentLimit,
      );
      if (raw == null) return null;
      final value = LaunchProgress.fromJson(raw);
      return value.requestId == requestId ? value : null;
    } on Object {
      return null;
    }
  }

  Future<LaunchOutcome?> readOutcome(
    String requestId, {
    required bool session,
  }) async {
    try {
      final prefix = session ? 'session-outcome' : 'launch-outcome';
      final raw = await _read(_channel(prefix, requestId), documentLimit);
      if (raw == null) return null;
      final value = LaunchOutcome.fromJson(raw);
      return value.requestId == requestId &&
              value.kind == (session ? 'session' : 'launch')
          ? value
          : null;
    } on Object {
      return null;
    }
  }

  Future<List<LaunchObservationEnvelope>> readObservations() async {
    try {
      if (!_directories()) return const [];
      final result = <LaunchObservationEnvelope>[];
      final candidates = <File>[];
      var count = 0;
      for (final entry in Directory(_staging).listSync(followLinks: false)) {
        if (++count > 16384) {
          throw const FormatException('Launch staging has excessive entries.');
        }
        if (entry is File &&
            RegExp(
              r'^runtime-observation-[0-9a-f]{64}\.json$',
            ).hasMatch(p.basename(entry.path))) {
          candidates.add(entry);
          if (candidates.length > 4096) {
            throw const FormatException('Excessive runtime observations.');
          }
        }
      }
      candidates.sort((a, b) => a.path.compareTo(b.path));
      for (final file in candidates) {
        try {
          final raw = await _read(file, observationLimit);
          if (raw == null) continue;
          final value = LaunchObservationEnvelope.fromJson(raw);
          if (p.basename(file.path) ==
              'runtime-observation-${LaunchStorageKeys.observation(value)}.json') {
            result.add(value);
          }
        } on Object {
          /* Malformed/foreign candidates are observations of nothing. */
        }
      }
      return List.unmodifiable(result);
    } on Object {
      return const [];
    }
  }

  File _channel(String prefix, String requestId) => File(
    p.join(_staging, '$prefix-${LaunchStorageKeys.request(requestId)}.json'),
  );

  bool _directories({bool create = false}) {
    var current = trustedGameRoot;
    for (final segment in ['', 'BepInEx', 'TopiaForge', 'staging']) {
      if (segment.isNotEmpty) current = p.join(current, segment);
      var type = FileSystemEntity.typeSync(current, followLinks: false);
      if (type == FileSystemEntityType.notFound) {
        if (!create || segment.isEmpty) return false;
        Directory(current).createSync();
        type = FileSystemEntity.typeSync(current, followLinks: false);
      }
      if (type != FileSystemEntityType.directory) {
        throw FileSystemException(
          'Launch staging contains a linked or non-directory ancestor.',
          current,
        );
      }
    }
    return true;
  }

  void _ordinaryFile(File file, {bool allowMissing = false}) {
    if (!p.equals(p.dirname(file.path), _staging)) {
      throw FileSystemException('Launch record escapes staging.', file.path);
    }
    final type = FileSystemEntity.typeSync(file.path, followLinks: false);
    if (type != FileSystemEntityType.file &&
        !(allowMissing && type == FileSystemEntityType.notFound)) {
      throw FileSystemException(
        'Launch record must be an ordinary file.',
        file.path,
      );
    }
  }

  Future<Object?> _read(File file, int maximumBytes) async {
    if (!_directories()) return null;
    final type = FileSystemEntity.typeSync(file.path, followLinks: false);
    if (type == FileSystemEntityType.notFound) return null;
    _ordinaryFile(file);
    final input = await file.open();
    try {
      final length = await input.length();
      if (length > maximumBytes) {
        throw const FormatException('Launch record exceeds its byte limit.');
      }
      final bytes = BytesBuilder(copy: false);
      while (true) {
        final chunk = await input.read(
          min(65536, maximumBytes - bytes.length + 1),
        );
        if (chunk.isEmpty) break;
        if (chunk.length > maximumBytes - bytes.length) {
          throw const FormatException(
            'Launch record grew beyond its byte limit.',
          );
        }
        bytes.add(chunk);
      }
      return jsonDecode(utf8.decode(bytes.takeBytes()));
    } finally {
      await input.close();
    }
  }
}

final Set<String> _activeLaunchLeases = {};

final class LaunchStagingLease {
  LaunchStagingLease._(this._handle, this._key);
  final RandomAccessFile _handle;
  final String _key;
  Future<void>? _release;
  Future<void> release() => _release ??= _releaseCore();
  Future<void> _releaseCore() async {
    try {
      await _handle.unlock();
    } finally {
      try {
        await _handle.close();
      } finally {
        _activeLaunchLeases.remove(_key);
      }
    }
  }
}
