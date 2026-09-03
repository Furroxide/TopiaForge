part of '../models.dart';

/// Reference resolution and the small bounded checks the contribution rules
/// share. Split from `manifest_contribution_validation.dart` so both stay under
/// the repository's 500-line cap for non-generated Dart.
const _minDeclarationIdLength = 4;

/// Wider than `modId`'s 64 because a declaration id is always namespaced under
/// the declaring package's name, and a first-party package name already spends
/// most of that budget:
/// `io.github.furroxide.topiaforge.sandbox.creator.menu` is 51 characters
/// before anything is declared under it.
const _maxDeclarationIdLength = 96;

/// R4 and R11. A reference this package does not own must be prefix-owned by a
/// package it requires -- `optionalDependencies` never qualifies, because a
/// reference that resolves only sometimes is a launch that fails only
/// sometimes. Ownership goes to the longest matching name, so a package cannot
/// squat inside a longer-named one's namespace.
void _validateDeclarationReference(
  ModManifest manifest,
  String reference,
  String path,
  void Function(String) error,
) {
  if (reference.isEmpty) {
    error('$path is required.');
    return;
  }
  if (!_isValidDeclarationId(reference)) {
    error(
      '$path must be $_minDeclarationIdLength-$_maxDeclarationIdLength '
      'characters and use letters, numbers, underscore, dot, or dash.',
    );
    return;
  }
  if (_isLocalReference(manifest, reference)) {
    return;
  }

  final lowered = reference.toLowerCase();
  final owners =
      manifest.dependencies
          .map((dependency) => dependency.id)
          .where((id) => lowered.startsWith('${id.toLowerCase()}.'))
          .toList()
        ..sort((left, right) => right.length.compareTo(left.length));
  if (owners.isEmpty) {
    final optional = manifest.optionalDependencies.any(
      (dependency) => lowered.startsWith('${dependency.id.toLowerCase()}.'),
    );
    final tail = optional
        ? '. An optional dependency cannot own a reference: a launch that '
              'resolves only when it happens to be installed is a launch that '
              'fails without warning.'
        : '.';
    error('$path names $reference, which no required dependency owns$tail');
    return;
  }
  if (owners.length > 1 && owners[0].length == owners[1].length) {
    error('$path names $reference, which two dependencies both claim to own.');
  }
}

/// R7's last clause. A discovered family is a prefix, not a world: nothing
/// under it exists until the game has run and reported it, so a policy that
/// names one is naming content that may never appear.
void _validateWorldReference(
  ModManifest manifest,
  String reference,
  String path,
  Set<String> owned,
  Set<String> discovered,
  void Function(String) error,
) {
  _validateDeclarationReference(manifest, reference, path, error);
  if (reference.isEmpty) {
    return;
  }

  final lowered = reference.toLowerCase();
  for (final family in discovered) {
    if (lowered == family || lowered.startsWith('$family.')) {
      error(
        '$path names $family, a discovered world family. Its instances only '
        'exist once the game has reported them, so a policy cannot name one.',
      );
      return;
    }
  }

  if (_isLocalReference(manifest, reference) &&
      !owned.contains(lowered) &&
      _isValidDeclarationId(reference)) {
    error(
      '$path names an id inside this package that this manifest does not '
      'declare.',
    );
  }
}

/// R3. A binding may only point at bytes the installer verified, so naming an
/// assembly the manifest does not hash would let a declaration bind to
/// something never checked.
void _validateImplementationBinding(
  ModManifest manifest,
  ModImplementationBinding binding,
  String path,
  void Function(String) error,
) {
  if (!_isValidTypeName(binding.type)) {
    error(
      '$path.type must be a namespace-qualified CLR type name, with no '
      'assembly qualifier and no nested-type syntax.',
    );
  }
  if (binding.assembly.isEmpty) {
    return;
  }
  if (_portableManifestPathCollisionKey(binding.assembly) == null ||
      !binding.assembly.toLowerCase().endsWith('.dll')) {
    error(
      '$path.assembly must be a safe portable .dll path inside the package.',
    );
    return;
  }
  final assembly = binding.assembly.toLowerCase();
  if (!manifest.hashes.keys.any((key) => key.toLowerCase() == assembly)) {
    error(
      '$path.assembly must also appear in hashes, so a declaration can only '
      'bind to bytes the installer verified.',
    );
  }
}

void _validateTransitionList(
  List<String> transitions,
  String path,
  bool required,
  void Function(String) error,
) {
  if (transitions.isEmpty) {
    if (required) {
      error('$path must name at least one transition.');
    }
    return;
  }
  if (transitions.length > 2) {
    error('$path cannot contain more than 2 entries.');
  }
  final seen = <String>{};
  for (final transition in transitions) {
    if (transition != ModTransitions.sceneReplacement &&
        transition != ModTransitions.additiveArena) {
      error('$path must contain only scene-replacement or additive-arena.');
    } else if (!seen.add(transition)) {
      error('$path repeats $transition.');
    }
  }
}

void _contributionCount(
  int count,
  String path,
  int maximum,
  void Function(String) error,
) {
  if (count > maximum) {
    error('$path cannot contain more than $maximum entries.');
  }
}

void _contributionText(
  String value,
  String path,
  int minimum,
  int maximum,
  void Function(String) error,
) {
  if (value.length < minimum || value.length > maximum) {
    error('$path must contain between $minimum and $maximum characters.');
  }
}

bool _isLocalReference(ModManifest manifest, String reference) =>
    reference.toLowerCase().startsWith('${manifest.id.toLowerCase()}.');

bool _isValidDeclarationId(String id) {
  if (id.length < _minDeclarationIdLength ||
      id.length > _maxDeclarationIdLength) {
    return false;
  }
  if (!_isAsciiLetterOrDigit(id.codeUnitAt(0))) {
    return false;
  }
  for (final unit in id.codeUnits) {
    if (!_isAsciiLetterOrDigit(unit) &&
        unit != 0x5f /* _ */ &&
        unit != 0x2e /* . */ &&
        unit != 0x2d /* - */ ) {
      return false;
    }
  }
  // A declaration id is namespaced under its own package, but a cross-package
  // reference is not, so the retired-ecosystem rule applies here as well as to
  // package names.
  final normalized = id.toLowerCase();
  return !_retiredEcosystemIdPrefixes.any(normalized.startsWith);
}

bool _isValidTypeName(String type) {
  if (type.length < 3 || type.length > 512) {
    return false;
  }
  final segments = type.split('.');
  if (segments.length < 2) {
    return false;
  }
  for (final segment in segments) {
    if (segment.isEmpty) {
      return false;
    }
    final first = segment.codeUnitAt(0);
    if (!_isAsciiLetter(first) && first != 0x5f) {
      return false;
    }
    for (final unit in segment.codeUnits) {
      if (!_isAsciiLetterOrDigit(unit) && unit != 0x5f) {
        return false;
      }
    }
  }
  return true;
}

bool _isAsciiLetter(int unit) =>
    (unit >= 0x41 && unit <= 0x5a) || (unit >= 0x61 && unit <= 0x7a);

bool _isAsciiLetterOrDigit(int unit) =>
    _isAsciiLetter(unit) || (unit >= 0x30 && unit <= 0x39);

Iterable<_ContributionDeclaration> _allDeclarations(
  ModManifest manifest,
) sync* {
  final contributions = manifest.contributions!;
  for (var index = 0; index < contributions.worlds.length; index++) {
    yield _ContributionDeclaration(
      contributions.worlds[index].id,
      'contributions.worlds[$index]',
    );
  }
  for (var index = 0; index < contributions.gamemodes.length; index++) {
    yield _ContributionDeclaration(
      contributions.gamemodes[index].id,
      'contributions.gamemodes[$index]',
    );
  }
  for (var index = 0; index < contributions.launchTargets.length; index++) {
    yield _ContributionDeclaration(
      contributions.launchTargets[index].id,
      'contributions.launchTargets[$index]',
    );
  }
}

class _ContributionDeclaration {
  const _ContributionDeclaration(this.id, this.path);

  final String id;
  final String path;
}
