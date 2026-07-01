final _versionPattern = RegExp(
  r'^\s*(\d+)(?:\.(\d+))?(?:\.(\d+))?(?:[-+][0-9A-Za-z_.-]+)?\s*$',
);

class SemanticVersion implements Comparable<SemanticVersion> {
  const SemanticVersion(this.major, this.minor, this.patch);

  final int major;
  final int minor;
  final int patch;

  factory SemanticVersion.parse(String value) {
    final match = _versionPattern.firstMatch(value);
    if (match == null) {
      throw FormatException('Invalid semantic version: $value');
    }

    return SemanticVersion(
      int.parse(match.group(1)!),
      int.parse(match.group(2) ?? '0'),
      int.parse(match.group(3) ?? '0'),
    );
  }

  static SemanticVersion? tryParse(String? value) {
    if (value == null || value.trim().isEmpty) {
      return null;
    }

    try {
      return SemanticVersion.parse(value);
    } on FormatException {
      return null;
    }
  }

  @override
  int compareTo(SemanticVersion other) {
    final majorCompare = major.compareTo(other.major);
    if (majorCompare != 0) {
      return majorCompare;
    }

    final minorCompare = minor.compareTo(other.minor);
    if (minorCompare != 0) {
      return minorCompare;
    }

    return patch.compareTo(other.patch);
  }

  @override
  bool operator ==(Object other) {
    return other is SemanticVersion &&
        major == other.major &&
        minor == other.minor &&
        patch == other.patch;
  }

  @override
  int get hashCode => Object.hash(major, minor, patch);

  @override
  String toString() => '$major.$minor.$patch';
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
      r'^([0-9]+)(?:\.([0-9]+|x|\*))?(?:\.([0-9]+|x|\*))?$',
      caseSensitive: false,
    ).firstMatch(text);
    if (wildcard != null &&
        [
          wildcard.group(2),
          wildcard.group(3),
        ].any((part) => part == 'x' || part == 'X' || part == '*')) {
      return _wildcardRange(wildcard);
    }

    if (!RegExp(r'^(>=|>|<=|<|=)').hasMatch(text)) {
      final exact = SemanticVersion.parse(text);
      return VersionRange(min: exact, max: exact, includeMax: true);
    }

    SemanticVersion? min;
    SemanticVersion? max;
    var includeMin = true;
    var includeMax = false;

    final matches = RegExp(
      r'(>=|>|<=|<|=)\s*([0-9]+(?:\.[0-9]+){0,2}(?:[-+][0-9A-Za-z_.-]+)?)',
    ).allMatches(text);

    if (matches.isEmpty) {
      throw FormatException('Invalid version range: $input');
    }

    for (final match in matches) {
      final op = match.group(1)!;
      final version = SemanticVersion.parse(match.group(2)!);
      switch (op) {
        case '>=':
          min = _higherMin(min, version);
          includeMin = true;
          break;
        case '>':
          min = _higherMin(min, version);
          includeMin = false;
          break;
        case '<=':
          max = _lowerMax(max, version);
          includeMax = true;
          break;
        case '<':
          max = _lowerMax(max, version);
          includeMax = false;
          break;
        case '=':
          min = version;
          max = version;
          includeMin = true;
          includeMax = true;
          break;
      }
    }

    return VersionRange(
      min: min,
      max: max,
      includeMin: includeMin,
      includeMax: includeMax,
    );
  }

  static SemanticVersion _higherMin(
    SemanticVersion? current,
    SemanticVersion candidate,
  ) {
    if (current == null || candidate.compareTo(current) > 0) {
      return candidate;
    }
    return current;
  }

  static SemanticVersion _lowerMax(
    SemanticVersion? current,
    SemanticVersion candidate,
  ) {
    if (current == null || candidate.compareTo(current) < 0) {
      return candidate;
    }
    return current;
  }

  static VersionRange _wildcardRange(RegExpMatch match) {
    final major = int.parse(match.group(1)!);
    final minorText = match.group(2);
    final patchText = match.group(3);
    if (minorText == null ||
        minorText == 'x' ||
        minorText == 'X' ||
        minorText == '*') {
      return VersionRange(
        min: SemanticVersion(major, 0, 0),
        max: SemanticVersion(major + 1, 0, 0),
      );
    }
    final minor = int.parse(minorText);
    if (patchText == null ||
        patchText == 'x' ||
        patchText == 'X' ||
        patchText == '*') {
      return VersionRange(
        min: SemanticVersion(major, minor, 0),
        max: SemanticVersion(major, minor + 1, 0),
      );
    }
    final exact = SemanticVersion(major, minor, int.parse(patchText));
    return VersionRange(min: exact, max: exact, includeMax: true);
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
