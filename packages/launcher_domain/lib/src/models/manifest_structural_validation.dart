part of '../models.dart';

List<String> _manifestStructuralIssues(Map<String, Object?> json) {
  final issues = <String>[];

  void stringValue(
    String path,
    Object? value, {
    int minimum = 0,
    required int maximum,
    bool required = false,
  }) {
    if (value == null) {
      if (required) issues.add('$path is required.');
      return;
    }
    if (value is! String) {
      issues.add('$path must be a string.');
      return;
    }
    final length = value.runes.length;
    if (length < minimum || length > maximum) {
      issues.add(
        '$path must contain between $minimum and $maximum Unicode characters.',
      );
    }
  }

  void stringArray(
    String path,
    Object? value, {
    required int maximumItems,
    int minimumItems = 0,
    int minimumLength = 1,
    int maximumLength = 1024,
  }) {
    if (value == null) return;
    if (value is! List) {
      issues.add('$path must be an array.');
      return;
    }
    if (value.length < minimumItems || value.length > maximumItems) {
      issues.add(
        '$path must contain between $minimumItems and $maximumItems entries.',
      );
    }
    final seen = <String>{};
    for (var index = 0; index < value.length; index++) {
      final item = value[index];
      stringValue(
        '$path[$index]',
        item,
        minimum: minimumLength,
        maximum: maximumLength,
        required: true,
      );
      if (item is String && !seen.add(item)) {
        issues.add('$path contains duplicate value $item.');
      }
    }
  }

  void dependencyMap(String path, Object? value) {
    if (value == null) return;
    if (value is! Map) {
      issues.add('$path must be an object.');
      return;
    }
    if (value.length > 128) {
      issues.add('$path cannot contain more than 128 entries.');
    }
    for (final entry in value.entries) {
      stringValue(
        '$path.${entry.key}',
        entry.value,
        minimum: 1,
        maximum: 256,
        required: true,
      );
    }
  }

  void closedObject(
    String path,
    Object? value,
    Set<String> allowed,
    Set<String> required, {
    bool requireAtLeastOne = false,
  }) {
    if (value is! Map) {
      issues.add('$path must be an object.');
      return;
    }

    final keys = value.keys.map((key) => key.toString()).toList();
    if (requireAtLeastOne && keys.isEmpty) {
      issues.add('$path must contain at least one property.');
    }
    for (final key in keys) {
      if (!allowed.contains(key)) {
        issues.add('$path contains unknown field $key.');
      }
    }
    for (final key in required) {
      if (!keys.contains(key)) {
        issues.add('$path is missing required field $key.');
      }
    }
  }

  void closedObjectArray(
    String path,
    Object? value,
    Set<String> allowed,
    Set<String> required,
  ) {
    if (value == null) return;
    if (value is! List) {
      issues.add('$path must be an array.');
      return;
    }
    for (var index = 0; index < value.length; index++) {
      closedObject('$path[$index]', value[index], allowed, required);
    }
  }

  if (json.length > 64) {
    issues.add('Manifest cannot contain more than 64 top-level fields.');
  }

  stringValue(r'$schema', json[r'$schema'], maximum: 512);
  stringValue('displayName', json['displayName'], maximum: 128);
  stringValue('description', json['description'], maximum: 4096);
  stringValue('entryType', json['entryType'], maximum: 512);
  stringValue('category', json['category'], maximum: 64);
  stringValue('icon', json['icon'], minimum: 1, maximum: 1024);
  stringValue('homepage', json['homepage'], maximum: 2048);
  stringValue('source', json['source'], maximum: 2048);
  stringValue('license', json['license'], minimum: 1, maximum: 256);
  for (final rangeField in const [
    'supportedGameVersionRange',
    'supportedLoaderVersionRange',
    'supportedSdkVersionRange',
  ]) {
    stringValue(rangeField, json[rangeField], maximum: 256);
  }

  dependencyMap('dependencies', json['dependencies']);
  dependencyMap('optionalDependencies', json['optionalDependencies']);
  stringArray(
    'loadAfter',
    json['loadAfter'],
    maximumItems: 128,
    maximumLength: 64,
  );
  stringArray(
    'loadBefore',
    json['loadBefore'],
    maximumItems: 128,
    maximumLength: 64,
  );
  stringArray('tags', json['tags'], maximumItems: 64, maximumLength: 64);
  stringArray(
    'capabilities',
    json['capabilities'],
    maximumItems: 64,
    maximumLength: 64,
  );
  stringArray(
    'platforms',
    json['platforms'],
    maximumItems: 3,
    maximumLength: 16,
  );
  stringArray(
    'architectures',
    json['architectures'],
    maximumItems: 2,
    maximumLength: 16,
  );
  stringArray(
    'contentTargets',
    json['contentTargets'],
    maximumItems: 64,
    maximumLength: 64,
  );
  stringArray('screenshots', json['screenshots'], maximumItems: 32);
  stringArray(
    'licenseFiles',
    json['licenseFiles'],
    maximumItems: 32,
    minimumItems: 1,
  );
  stringArray('apiAssemblies', json['apiAssemblies'], maximumItems: 64);

  final hashes = json['hashes'];
  if (hashes != null) {
    if (hashes is! Map) {
      issues.add('hashes must be an object.');
    } else {
      if (hashes.length > 8192) {
        issues.add('hashes cannot contain more than 8192 entries.');
      }
      final digestPattern = RegExp(r'^[A-Fa-f0-9]{64}$');
      for (final entry in hashes.entries) {
        final digest = entry.value;
        if (digest is! String || !digestPattern.hasMatch(digest)) {
          issues.add(
            'hashes value for ${entry.key} must be a 64-character SHA-256 digest.',
          );
        }
      }
    }
  }

  closedObject(
    'author',
    json['author'],
    const {'name', 'email', 'url'},
    const {'name'},
  );
  final author = json['author'];
  if (author is Map) {
    stringValue('author.name', author['name'], maximum: 128);
    stringValue('author.email', author['email'], maximum: 254);
    stringValue('author.url', author['url'], maximum: 2048);
  }
  if (json.containsKey('builtWith')) {
    closedObject(
      'builtWith',
      json['builtWith'],
      const {'sdkVersion', 'loaderVersion', 'gameVersion', 'toolVersion'},
      const {},
      requireAtLeastOne: true,
    );
  }
  closedObjectArray(
    'conflicts',
    json['conflicts'],
    const {'id', 'versionRange', 'reason'},
    const {'id'},
  );
  final conflicts = json['conflicts'];
  if (conflicts is List) {
    if (conflicts.length > 128) {
      issues.add('conflicts cannot contain more than 128 entries.');
    }
    for (var index = 0; index < conflicts.length; index++) {
      final conflict = conflicts[index];
      if (conflict is! Map) continue;
      stringValue(
        'conflicts[$index].versionRange',
        conflict['versionRange'],
        minimum: 1,
        maximum: 256,
      );
      stringValue('conflicts[$index].reason', conflict['reason'], maximum: 512);
    }
  }
  // The two version-specific fields. Known keys are the union across schemas, so
  // each version has to reject the other's by name; otherwise adding V6's key
  // would silently widen the V5 reader. Mirrors ForeignFieldMessage in
  // src/TopiaForge.ModManager.Core/ModManifestJson.Contributions.cs.
  final declaredVersion = (json['schemaVersion'] as num?)?.toInt();
  final isV6 = declaredVersion == ModManifest.manifestV6SchemaVersion;
  if (isV6 && json.containsKey('worldGamemodes')) {
    issues.add(
      'worldGamemodes was retired in schemaVersion 6. Split it into '
      'contributions.gamemodes (identity, implementation binding and world '
      'requirements) and contributions.launchTargets (what the player picks, '
      'and which world it starts in). Run `topiaforge migrate-manifest '
      '--project <path>`.',
    );
  }
  if (!isV6 && json.containsKey('contributions')) {
    issues.add(
      'contributions requires schemaVersion 6; schemaVersion 5 cannot declare '
      'worlds, gamemodes or launch targets.',
    );
  }
  if (isV6) {
    _contributionStructuralIssues(json['contributions'], issues);
  }

  closedObjectArray(
    'worldGamemodes',
    json['worldGamemodes'],
    const {'id', 'name', 'description'},
    const {'id', 'name'},
  );
  final gamemodes = isV6 ? null : json['worldGamemodes'];
  if (gamemodes is List) {
    if (gamemodes.length > 64) {
      issues.add('worldGamemodes cannot contain more than 64 entries.');
    }
    for (var index = 0; index < gamemodes.length; index++) {
      final gamemode = gamemodes[index];
      if (gamemode is! Map) continue;
      stringValue(
        'worldGamemodes[$index].name',
        gamemode['name'],
        maximum: 128,
      );
      stringValue(
        'worldGamemodes[$index].description',
        gamemode['description'],
        maximum: 1024,
      );
    }
  }

  final multiplayer = json['multiplayer'];
  if (json.containsKey('multiplayer')) {
    closedObject(
      'multiplayer',
      multiplayer,
      const {'mode', 'presence', 'protocol', 'synchronizedFiles'},
      const {'mode'},
    );
    if (multiplayer is Map) {
      stringValue(
        'multiplayer.mode',
        multiplayer['mode'],
        minimum: 1,
        maximum: 32,
        required: true,
      );
      stringValue(
        'multiplayer.presence',
        multiplayer['presence'],
        minimum: 1,
        maximum: 16,
      );
      if (multiplayer.containsKey('protocol')) {
        closedObject(
          'multiplayer.protocol',
          multiplayer['protocol'],
          const {'version', 'peerVersionRange'},
          const {'version'},
        );
        final protocol = multiplayer['protocol'];
        if (protocol is Map) {
          stringValue(
            'multiplayer.protocol.version',
            protocol['version'],
            minimum: 1,
            maximum: 256,
            required: true,
          );
          stringValue(
            'multiplayer.protocol.peerVersionRange',
            protocol['peerVersionRange'],
            minimum: 1,
            maximum: 256,
          );
        }
      }
      stringArray(
        'multiplayer.synchronizedFiles',
        multiplayer['synchronizedFiles'],
        maximumItems: 256,
      );
    }
  }

  return List.unmodifiable(issues);
}
