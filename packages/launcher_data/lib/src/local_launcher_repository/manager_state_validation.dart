part of '../local_launcher_repository.dart';

Map<String, Object?> _validateManagerStateEnvelope(Object? decoded) {
  if (decoded is! Map<String, Object?>) {
    throw const _ManagerStateContentException(
      'Manager state must be an object. Repair the original state.',
    );
  }
  if (decoded.containsKey('schemaVersion') &&
      (decoded['schemaVersion'] is! int ||
          !const [0, 1].contains(decoded['schemaVersion']))) {
    throw const _ManagerStateContentException(
      'Manager state schemaVersion must be integer 0 or 1 when present. Repair the original state.',
    );
  }
  if (decoded.containsKey('autoLoadOnStart') &&
      decoded['autoLoadOnStart'] is! bool) {
    throw const _ManagerStateContentException(
      'Manager state autoLoadOnStart must be boolean when present. Repair the original state.',
    );
  }
  if (decoded.containsKey('launchSelection')) {
    try {
      LaunchSelection.fromJson(decoded['launchSelection']);
    } on FormatException catch (error) {
      throw _ManagerStateContentException(
        'Manager state launchSelection is invalid. Repair the original state. $error',
      );
    }
  }
  if (!decoded.containsKey('mods')) return {...decoded, 'mods': <Object?>[]};
  if (decoded['mods'] is! List || (decoded['mods'] as List).length > 4096) {
    throw const _ManagerStateContentException(
      'Manager state mods must be an array with at most 4096 records. Repair the original state.',
    );
  }
  return decoded;
}

Iterable<(String, String)> _managerStateRecordIssues(
  Map<String, Object?> state,
) sync* {
  final ids = <String>{};
  for (final raw in state['mods'] as List) {
    if (raw is! Map) {
      yield (
        '(invalid state entry)',
        'Manager state contains a malformed package entry. Repair the original state.',
      );
      continue;
    }
    final id = raw['id'];
    if (id is! String || !ModManifest.isValidId(id)) {
      yield (
        id is String ? id : '(missing id)',
        'Manager state contains a malformed package identity. Repair the original entry.',
      );
      continue;
    }
    if (!ids.add(id.toLowerCase())) {
      yield (
        id,
        'Manager state has duplicate entries for $id. Repair the ambiguous selection.',
      );
    }
    final invalid = [
      'enabled',
      'versionPinned',
      'uninstallPending',
      'restartRequired',
    ].where((key) => raw.containsKey(key) && raw[key] is! bool).toList();
    for (final key in [
      'version',
      'name',
      'installedAtUtc',
      'updatedAtUtc',
      'quarantineReason',
      'quarantinedAtUtc',
    ]) {
      if (raw.containsKey(key) && raw[key] is! String) invalid.add(key);
    }
    if (invalid.isNotEmpty) {
      yield (
        id,
        'Manager state has malformed values for $id: ${invalid.join(', ')}. Repair the original entry.',
      );
    }
  }
}
