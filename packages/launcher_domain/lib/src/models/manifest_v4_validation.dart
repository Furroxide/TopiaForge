part of '../models.dart';

void _validateManifestV4Constraints(
  ModManifest manifest,
  List<LauncherIssue> issues,
) {
  void stringLength(
    String field,
    String value, {
    int minimum = 0,
    required int maximum,
    bool required = false,
  }) {
    final length = value.runes.length;
    if ((required && value.trim().isEmpty) ||
        length < minimum ||
        length > maximum) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: manifest.id,
          message:
              '$field must contain between $minimum and $maximum Unicode characters.',
        ),
      );
    }
  }

  void count(String field, int count, int maximum) {
    if (count > maximum) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: manifest.id,
          message: '$field cannot contain more than $maximum entries.',
        ),
      );
    }
  }

  count('dependencies', manifest.dependencies.length, 128);
  count('optionalDependencies', manifest.optionalDependencies.length, 128);
  count('conflicts', manifest.conflicts.length, 128);
  count('loadAfter', manifest.loadAfter.length, 128);
  count('loadBefore', manifest.loadBefore.length, 128);
  count('capabilities', manifest.capabilities.length, 64);
  count('platforms', manifest.platforms.length, 3);
  count('architectures', manifest.architectures.length, 2);
  count('contentTargets', manifest.contentTargets.length, 64);
  count('tags', manifest.tags.length, 64);
  count('screenshots', manifest.screenshots.length, 32);
  count('hashes', manifest.hashes.length, 8192);
  count('worldGamemodes', manifest.worldGamemodes.length, 64);
  count('apiAssemblies', manifest.apiAssemblies.length, 64);
  count('x-* extensions', manifest.extraFields.length, 32);
  count('top-level manifest fields', manifest.toJson().length, 64);

  if (manifest.schemaUrl.isNotEmpty) {
    stringLength(r'$schema', manifest.schemaUrl, maximum: 512);
  }
  if (manifest.name.isNotEmpty) {
    stringLength('displayName', manifest.name, maximum: 128);
  }
  if (manifest.author.name.isNotEmpty) {
    stringLength('author.name', manifest.author.name, maximum: 128);
  }
  if (manifest.author.email.isNotEmpty) {
    stringLength('author.email', manifest.author.email, maximum: 254);
  }
  if (manifest.author.url.isNotEmpty) {
    stringLength('author.url', manifest.author.url, maximum: 2048);
  }
  if (manifest.description.isNotEmpty) {
    stringLength('description', manifest.description, maximum: 4096);
  }
  if (manifest.entryType.isNotEmpty) {
    stringLength('entryType', manifest.entryType, maximum: 512);
  }
  if (manifest.category.isNotEmpty) {
    stringLength('category', manifest.category, maximum: 64);
  }
  if (manifest.homepage.isNotEmpty) {
    stringLength('homepage', manifest.homepage, maximum: 2048);
  }
  if (manifest.source.isNotEmpty) {
    stringLength('source', manifest.source, maximum: 2048);
  }
  if (manifest.license.isNotEmpty) {
    stringLength('license', manifest.license, minimum: 1, maximum: 256);
  }

  final tagValues = <String>{};
  for (final tag in manifest.tags) {
    stringLength('tags', tag, minimum: 1, maximum: 64, required: true);
    if (!tagValues.add(tag)) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: manifest.id,
          message: 'tags contains duplicate value $tag.',
        ),
      );
    }
  }

  void portablePaths(String field, Iterable<String> paths) {
    final seen = <String>{};
    for (final path in paths) {
      final collisionKey = _portableManifestPathCollisionKey(path);
      if (collisionKey == null) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: manifest.id,
            message:
                '$field entry $path must be a safe portable relative path.',
          ),
        );
      } else if (!seen.add(collisionKey)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: manifest.id,
            message:
                '$field contains duplicate or portable-collision path $path.',
          ),
        );
      }
    }
  }

  if (manifest.icon.isNotEmpty) {
    portablePaths('icon', [manifest.icon]);
  }
  portablePaths('screenshots', manifest.screenshots);
  portablePaths('hashes', manifest.hashes.keys);
  final digestPattern = RegExp(r'^[A-Fa-f0-9]{64}$');
  for (final entry in manifest.hashes.entries) {
    if (!digestPattern.hasMatch(entry.value)) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: manifest.id,
          message:
              'hashes value for ${entry.key} must be a 64-character SHA-256 digest.',
        ),
      );
    }
  }

  for (final conflict in manifest.conflicts) {
    if (conflict.reason.isNotEmpty) {
      stringLength('conflicts.reason', conflict.reason, maximum: 512);
    }
  }

  for (final hints in <(String, List<String>)>[
    ('loadAfter', manifest.loadAfter),
    ('loadBefore', manifest.loadBefore),
  ]) {
    final seen = <String>{};
    for (final value in hints.$2) {
      final normalized = value.toLowerCase();
      if (!seen.add(normalized) || normalized == manifest.id.toLowerCase()) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: manifest.id,
            message: '${hints.$1} cannot contain duplicates or the owning mod.',
          ),
        );
      }
    }
  }

  void knownValues(String field, List<String> values, Set<String> known) {
    final seen = <String>{};
    for (final value in values) {
      if (!seen.add(value)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: manifest.id,
            message: '$field contains duplicate value $value.',
          ),
        );
      } else if (!known.contains(value)) {
        issues.add(
          LauncherIssue(
            severity: IssueSeverity.error,
            subjectId: manifest.id,
            message: '$field contains unknown value $value.',
          ),
        );
      }
    }
  }

  knownValues('capabilities', manifest.capabilities, _knownCapabilities);
  knownValues('platforms', manifest.platforms, const {
    'windows',
    'macos',
    'linux',
  });
  knownValues('architectures', manifest.architectures, const {'x64', 'arm64'});

  final targetPattern = RegExp(r'^[a-z0-9][a-z0-9_.-]{0,63}$');
  final targets = <String>{};
  for (final target in manifest.contentTargets) {
    if (!targetPattern.hasMatch(target) || !targets.add(target)) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: manifest.id,
          message:
              'contentTargets contains invalid or duplicate value $target.',
        ),
      );
    }
  }

  for (final entry in <(String, String)>[
    ('builtWith.sdkVersion', manifest.builtWith?.sdkVersion ?? ''),
    ('builtWith.loaderVersion', manifest.builtWith?.loaderVersion ?? ''),
    ('builtWith.gameVersion', manifest.builtWith?.gameVersion ?? ''),
    ('builtWith.toolVersion', manifest.builtWith?.toolVersion ?? ''),
  ]) {
    if (entry.$2.isNotEmpty && SemanticVersion.tryParse(entry.$2) == null) {
      issues.add(
        LauncherIssue(
          severity: IssueSeverity.error,
          subjectId: manifest.id,
          message: '${entry.$1} must be an exact semantic version.',
        ),
      );
    }
  }

  if (manifest.builtWith?.isEmpty ?? false) {
    issues.add(
      LauncherIssue(
        severity: IssueSeverity.error,
        subjectId: manifest.id,
        message: 'builtWith must contain at least one version.',
      ),
    );
  }
}
