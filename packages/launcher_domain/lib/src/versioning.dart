part 'versioning_number.dart';
part 'version_range_parsing.dart';

final _semanticVersionPattern = RegExp(
  r'^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)'
  r'(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?'
  r'(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$',
);

final _numericIdentifierPattern = RegExp(r'^[0-9]+$');
final _rangeCoreComponentPattern = RegExp(r'^(0|[1-9][0-9]*)$');

/// Canonical version mapping for Robotopia game build identifiers.
///
/// The game launcher publishes monotonically increasing integer build ids,
/// while mod compatibility ranges use SemVer. TopiaForge reserves the
/// `0.0.<build>` namespace for that bridge, so build `2227` is represented as
/// `0.0.2227` everywhere a manifest range is evaluated.
abstract final class RobotopiaGameVersion {
  static const int maxBuildId = 2147483647;

  static String? tryFromBuildId(Object? value) {
    final int? buildId;
    if (value is int) {
      buildId = value;
    } else if (value is String &&
        RegExp(r'^(0|[1-9][0-9]*)$').hasMatch(value)) {
      buildId = int.tryParse(value);
    } else {
      buildId = null;
    }

    if (buildId == null || buildId <= 0 || buildId > maxBuildId) {
      return null;
    }
    return '0.0.$buildId';
  }

  static String? tryFromBuildLabel(String? value) {
    final match = RegExp(
      r'^build ([0-9]+)$',
      caseSensitive: false,
    ).firstMatch(value?.trim() ?? '');
    return match == null ? null : tryFromBuildId(match.group(1));
  }

  static int? tryBuildId(String? version) {
    final parsed = SemanticVersion.tryParse(version);
    if (parsed == null ||
        !parsed.majorNumber.isZero ||
        !parsed.minorNumber.isZero ||
        !parsed.patchNumber.isPositive ||
        parsed.prerelease.isNotEmpty ||
        parsed.buildMetadata.isNotEmpty) {
      return null;
    }
    final buildId = parsed.patchNumber.tryToInt();
    return buildId != null && buildId <= maxBuildId ? buildId : null;
  }

  static String? tryBuildLabel(String? version) {
    final buildId = tryBuildId(version);
    return buildId == null ? null : 'build $buildId';
  }
}

class SemanticVersion implements Comparable<SemanticVersion> {
  SemanticVersion(Object major, Object minor, Object patch)
    : _major = SemanticVersionNumber.fromValue(major),
      _minor = SemanticVersionNumber.fromValue(minor),
      _patch = SemanticVersionNumber.fromValue(patch),
      prerelease = '',
      buildMetadata = '';

  SemanticVersion._(
    String major,
    String minor,
    String patch,
    this.prerelease,
    this.buildMetadata,
  ) : _major = SemanticVersionNumber.parse(major),
      _minor = SemanticVersionNumber.parse(minor),
      _patch = SemanticVersionNumber.parse(patch);

  SemanticVersion._fromNumbers(this._major, this._minor, this._patch)
    : prerelease = '',
      buildMetadata = '';

  final SemanticVersionNumber _major;
  final SemanticVersionNumber _minor;
  final SemanticVersionNumber _patch;

  SemanticVersionNumber get majorNumber => _major;
  SemanticVersionNumber get minorNumber => _minor;
  SemanticVersionNumber get patchNumber => _patch;
  String get majorDigits => _major.digits;
  String get minorDigits => _minor.digits;
  String get patchDigits => _patch.digits;
  int get major => _major.requireInt('major');
  int get minor => _minor.requireInt('minor');
  int get patch => _patch.requireInt('patch');

  /// Dot-separated SemVer prerelease identifiers, without the leading `-`.
  final String prerelease;

  /// Dot-separated SemVer build identifiers, without the leading `+`.
  final String buildMetadata;

  bool get isPrerelease => prerelease.isNotEmpty;

  factory SemanticVersion.parse(String value) {
    final match = _semanticVersionPattern.firstMatch(value);
    if (match == null || !_hasValidPrereleaseIdentifiers(match.group(4))) {
      throw FormatException('Invalid semantic version: $value');
    }

    return SemanticVersion._(
      match.group(1)!,
      match.group(2)!,
      match.group(3)!,
      match.group(4) ?? '',
      match.group(5) ?? '',
    );
  }

  static SemanticVersion? tryParse(String? value) {
    if (value == null || value.isEmpty) {
      return null;
    }

    try {
      return SemanticVersion.parse(value);
    } on FormatException {
      return null;
    }
  }

  static bool _hasValidPrereleaseIdentifiers(String? prerelease) {
    if (prerelease == null) {
      return true;
    }

    for (final identifier in prerelease.split('.')) {
      if (_numericIdentifierPattern.hasMatch(identifier) &&
          identifier.length > 1 &&
          identifier.startsWith('0')) {
        return false;
      }
    }
    return true;
  }

  @override
  int compareTo(SemanticVersion other) {
    final majorCompare = _major.compareTo(other._major);
    if (majorCompare != 0) {
      return majorCompare;
    }

    final minorCompare = _minor.compareTo(other._minor);
    if (minorCompare != 0) {
      return minorCompare;
    }

    final patchCompare = _patch.compareTo(other._patch);
    if (patchCompare != 0) {
      return patchCompare;
    }

    if (prerelease.isEmpty) {
      return other.prerelease.isEmpty ? 0 : 1;
    }
    if (other.prerelease.isEmpty) {
      return -1;
    }

    final identifiers = prerelease.split('.');
    final otherIdentifiers = other.prerelease.split('.');
    final sharedLength = identifiers.length < otherIdentifiers.length
        ? identifiers.length
        : otherIdentifiers.length;
    for (var index = 0; index < sharedLength; index += 1) {
      final comparison = _comparePrereleaseIdentifier(
        identifiers[index],
        otherIdentifiers[index],
      );
      if (comparison != 0) {
        return comparison;
      }
    }

    return identifiers.length.compareTo(otherIdentifiers.length);
  }

  static int _comparePrereleaseIdentifier(String left, String right) {
    final leftIsNumeric = _numericIdentifierPattern.hasMatch(left);
    final rightIsNumeric = _numericIdentifierPattern.hasMatch(right);
    if (leftIsNumeric && rightIsNumeric) {
      final lengthComparison = left.length.compareTo(right.length);
      return lengthComparison != 0 ? lengthComparison : left.compareTo(right);
    }
    if (leftIsNumeric) {
      return -1;
    }
    if (rightIsNumeric) {
      return 1;
    }
    return left.compareTo(right);
  }

  SemanticVersion incrementMajor() => SemanticVersion._fromNumbers(
    _major.increment(),
    const SemanticVersionNumber.fromInt(0),
    const SemanticVersionNumber.fromInt(0),
  );

  SemanticVersion incrementMinor() => SemanticVersion._fromNumbers(
    _major,
    _minor.increment(),
    const SemanticVersionNumber.fromInt(0),
  );

  SemanticVersion incrementPatch() =>
      SemanticVersion._fromNumbers(_major, _minor, _patch.increment());

  /// Equality is version identity, including build metadata. SemVer precedence
  /// deliberately ignores build metadata; use [compareTo] when ordering.
  @override
  bool operator ==(Object other) {
    return other is SemanticVersion &&
        _major == other._major &&
        _minor == other._minor &&
        _patch == other._patch &&
        prerelease == other.prerelease &&
        buildMetadata == other.buildMetadata;
  }

  @override
  int get hashCode =>
      Object.hash(_major, _minor, _patch, prerelease, buildMetadata);

  @override
  String toString() {
    final buffer = StringBuffer('$_major.$_minor.$_patch');
    if (prerelease.isNotEmpty) {
      buffer.write('-$prerelease');
    }
    if (buildMetadata.isNotEmpty) {
      buffer.write('+$buildMetadata');
    }
    return buffer.toString();
  }
}

class VersionRange {
  const VersionRange({
    this.min,
    this.max,
    this.includeMin = true,
    this.includeMax = false,
  });

  const VersionRange.any()
    : min = null,
      max = null,
      includeMin = true,
      includeMax = true;

  final SemanticVersion? min;
  final SemanticVersion? max;
  final bool includeMin;
  final bool includeMax;

  bool get isAny => min == null && max == null;

  bool allows(String version) {
    final parsed = SemanticVersion.tryParse(version);
    if (parsed == null) {
      return false;
    }

    final minimum = min;
    if (minimum != null) {
      final comparison = parsed.compareTo(minimum);
      if (comparison < 0 || (comparison == 0 && !includeMin)) {
        return false;
      }
    }

    final maximum = max;
    if (maximum != null) {
      final comparison = parsed.compareTo(maximum);
      if (comparison > 0 || (comparison == 0 && !includeMax)) {
        return false;
      }
    }

    return true;
  }

  static VersionRange parse(String? input) {
    final text = input?.trim() ?? '';
    if (text.isEmpty || text == '*') {
      return const VersionRange.any();
    }

    final wildcard = RegExp(
      r'^(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*|x|\*))?'
      r'(?:\.(0|[1-9][0-9]*|x|\*))?$',
      caseSensitive: false,
    ).firstMatch(text);
    if (wildcard != null &&
        [
          wildcard.group(2),
          wildcard.group(3),
        ].any((part) => part == 'x' || part == 'X' || part == '*')) {
      return _parseWildcardRange(wildcard);
    }

    if (!RegExp(r'^(>=|>|<=|<|=)').hasMatch(text)) {
      final exact = _parseVersionRangeVersion(text, input);
      return VersionRange(min: exact, max: exact, includeMax: true);
    }

    SemanticVersion? min;
    SemanticVersion? max;
    var includeMin = true;
    var includeMax = false;

    final matches = RegExp(
      r'(>=|>|<=|<|=)\s*([^\s]+)',
    ).allMatches(text).toList();

    if (matches.isEmpty) {
      throw FormatException('Invalid version range: $input');
    }

    var cursor = 0;
    for (final match in matches) {
      final separator = text.substring(cursor, match.start);
      final isFirst = cursor == 0;
      if (separator.trim().isNotEmpty || (!isFirst && separator.isEmpty)) {
        throw FormatException('Invalid version range: $input');
      }

      final op = match.group(1)!;
      final version = _parseVersionRangeVersion(match.group(2)!, input);
      switch (op) {
        case '>=':
          final comparison = min == null ? 1 : version.compareTo(min);
          if (comparison > 0) {
            min = version;
            includeMin = true;
          }
          break;
        case '>':
          final comparison = min == null ? 1 : version.compareTo(min);
          if (comparison > 0) {
            min = version;
            includeMin = false;
          } else if (comparison == 0) {
            includeMin = false;
          }
          break;
        case '<=':
          final comparison = max == null ? -1 : version.compareTo(max);
          if (comparison < 0) {
            max = version;
            includeMax = true;
          }
          break;
        case '<':
          final comparison = max == null ? -1 : version.compareTo(max);
          if (comparison < 0) {
            max = version;
            includeMax = false;
          } else if (comparison == 0) {
            includeMax = false;
          }
          break;
        case '=':
          final minComparison = min == null ? 1 : version.compareTo(min);
          if (minComparison > 0) {
            min = version;
            includeMin = true;
          }
          final maxComparison = max == null ? -1 : version.compareTo(max);
          if (maxComparison < 0) {
            max = version;
            includeMax = true;
          }
          break;
      }
      cursor = match.end;
    }

    if (text.substring(cursor).trim().isNotEmpty) {
      throw FormatException('Invalid version range: $input');
    }

    if (min != null && max != null) {
      final comparison = min.compareTo(max);
      if (comparison > 0 || (comparison == 0 && (!includeMin || !includeMax))) {
        throw FormatException('Version range has no allowed versions: $input');
      }
    }

    return VersionRange(
      min: min,
      max: max,
      includeMin: includeMin,
      includeMax: includeMax,
    );
  }

  @override
  String toString() {
    if (isAny) {
      return '*';
    }

    if (min != null && max != null && min == max && includeMin && includeMax) {
      return min.toString();
    }

    final parts = <String>[];
    if (min != null) {
      parts.add('${includeMin ? '>=' : '>'}$min');
    }
    if (max != null) {
      parts.add('${includeMax ? '<=' : '<'}$max');
    }
    return parts.join(' ');
  }
}
