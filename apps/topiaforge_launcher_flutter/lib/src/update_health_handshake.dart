import 'dart:convert';
import 'dart:io';

import 'package:flutter/scheduler.dart';
import 'package:flutter/widgets.dart';
import 'package:path/path.dart' as p;

typedef UpdateHealthFrameScheduler = void Function(FrameCallback callback);

/// The options the update helper starts a freshly installed launcher with.
const updateHealthNonceOption = '--topiaforge-update-health-nonce';
const updateHealthFileOption = '--topiaforge-update-health-file';

final _nonceShape = RegExp(r'^[0-9a-f]{64}$');

/// Whether [arguments] are exactly what the update helper starts a launcher
/// with: the nonce option, a nonce of 64 lowercase hexadecimal characters,
/// the file option and a path, in that order and nothing else.
///
/// Only such a start is a health check. It never hands over to another
/// launcher, because the helper waits for its marker and rolls the update
/// back without it.
bool isUpdateHealthStart(List<String> arguments) =>
    arguments.length == 4 &&
    arguments[0] == updateHealthNonceOption &&
    _nonceShape.hasMatch(arguments[1]) &&
    arguments[2] == updateHealthFileOption &&
    arguments[3].isNotEmpty;

/// An update health check, read and checked before the launcher claims its
/// data root.
final class UpdateHealthRequest {
  const UpdateHealthRequest._(this.nonce, this.markerPath, this._allowedRoot);

  final String nonce;
  final String markerPath;
  final String _allowedRoot;

  /// Writes the health marker after the first frame.
  void schedule({UpdateHealthFrameScheduler? frameScheduler}) {
    final scheduleFrame =
        frameScheduler ?? WidgetsBinding.instance.addPostFrameCallback;
    scheduleFrame((_) {
      _requireSafeHealthParents(markerPath, _allowedRoot);
      if (FileSystemEntity.typeSync(markerPath, followLinks: false) !=
          FileSystemEntityType.notFound) {
        throw StateError('Launcher update health marker already exists.');
      }
      final marker = File(markerPath);
      final temporary = File('$markerPath.tmp-$pid');
      if (FileSystemEntity.typeSync(temporary.path, followLinks: false) !=
          FileSystemEntityType.notFound) {
        throw StateError('Launcher update health temporary path is unsafe.');
      }
      temporary.createSync(exclusive: true);
      temporary.writeAsStringSync(
        '${jsonEncode({'formatVersion': 1, 'nonce': nonce, 'healthy': true, 'processId': pid, 'reportedAtUtc': DateTime.now().toUtc().toIso8601String()})}\n',
        flush: true,
      );
      temporary.renameSync(marker.path);
    });
  }
}

/// Reads the update health check in [arguments], or null when they name no
/// health option at all. Throws [StateError] when they name one in any shape
/// but [isUpdateHealthStart]'s, or point the marker anywhere unsafe.
UpdateHealthRequest? readUpdateHealthRequest(
  List<String> arguments, {
  required String dataRoot,
}) {
  if (!arguments.contains(updateHealthNonceOption) &&
      !arguments.contains(updateHealthFileOption)) {
    return null;
  }
  if (!isUpdateHealthStart(arguments)) {
    throw StateError('Launcher update health arguments are invalid.');
  }
  final allowedRoot = p.normalize(
    p.absolute(p.join(dataRoot, 'updates', 'transactions')),
  );
  final markerPath = p.normalize(p.absolute(arguments[3]));
  if (!p.isWithin(allowedRoot, markerPath) ||
      p.basename(markerPath) != 'health.json' ||
      !RegExp(r'^[0-9a-f]{32}$').hasMatch(p.basename(p.dirname(markerPath))) ||
      FileSystemEntity.typeSync(markerPath, followLinks: false) !=
          FileSystemEntityType.notFound) {
    throw StateError('Launcher update health marker path is unsafe.');
  }
  _requireSafeHealthParents(markerPath, allowedRoot);
  return UpdateHealthRequest._(arguments[1], markerPath, allowedRoot);
}

/// Reads the update health check in [arguments], when there is one, and
/// writes its marker after the first frame.
void scheduleUpdateHealthHandshake(
  List<String> arguments, {
  required String dataRoot,
  UpdateHealthFrameScheduler? frameScheduler,
}) {
  readUpdateHealthRequest(
    arguments,
    dataRoot: dataRoot,
  )?.schedule(frameScheduler: frameScheduler);
}

void _requireSafeHealthParents(String markerPath, String allowedRoot) {
  var parent = p.dirname(markerPath);
  while (!p.equals(parent, allowedRoot)) {
    if (!p.isWithin(allowedRoot, parent) ||
        FileSystemEntity.typeSync(parent, followLinks: false) !=
            FileSystemEntityType.directory) {
      throw StateError('Launcher update health marker parent is unsafe.');
    }
    parent = p.dirname(parent);
  }
  if (FileSystemEntity.typeSync(allowedRoot, followLinks: false) !=
      FileSystemEntityType.directory) {
    throw StateError('Launcher update transaction storage is unsafe.');
  }
}
