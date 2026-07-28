import 'dart:convert';
import 'dart:io';

import 'package:flutter/widgets.dart';
import 'package:path/path.dart' as p;

void scheduleUpdateHealthHandshake(
  List<String> arguments, {
  required String dataRoot,
}) {
  final nonce = _option(arguments, '--topiaforge-update-health-nonce');
  final path = _option(arguments, '--topiaforge-update-health-file');
  if (nonce == null && path == null) return;
  if (nonce == null ||
      path == null ||
      !RegExp(r'^[0-9a-f]{64}$').hasMatch(nonce)) {
    throw StateError('Launcher update health arguments are invalid.');
  }
  final allowedRoot = p.normalize(
    p.absolute(p.join(dataRoot, 'updates', 'transactions')),
  );
  final markerPath = p.normalize(p.absolute(path));
  if (!p.isWithin(allowedRoot, markerPath) ||
      p.basename(markerPath) != 'health.json' ||
      !RegExp(r'^[0-9a-f]{32}$').hasMatch(p.basename(p.dirname(markerPath))) ||
      FileSystemEntity.typeSync(markerPath, followLinks: false) !=
          FileSystemEntityType.notFound) {
    throw StateError('Launcher update health marker path is unsafe.');
  }
  _requireSafeHealthParents(markerPath, allowedRoot);
  WidgetsBinding.instance.addPostFrameCallback((_) {
    _requireSafeHealthParents(markerPath, allowedRoot);
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

String? _option(List<String> arguments, String name) {
  final matches = <String>[];
  for (var index = 0; index < arguments.length; index++) {
    if (arguments[index] == name && index + 1 < arguments.length) {
      matches.add(arguments[index + 1]);
    }
  }
  if (matches.length > 1) {
    throw StateError('Launcher update health option is duplicated.');
  }
  return matches.firstOrNull;
}
