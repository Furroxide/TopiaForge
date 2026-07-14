part of 'versioning.dart';

/// Parses the one- and two-component shorthands historically accepted by
/// ranges while keeping standalone semantic versions strict.
SemanticVersion _parseVersionRangeVersion(String value, String? input) {
  var suffixStart = value.length;
  final prereleaseStart = value.indexOf('-');
  final buildStart = value.indexOf('+');
  if (prereleaseStart >= 0 && prereleaseStart < suffixStart) {
    suffixStart = prereleaseStart;
  }
  if (buildStart >= 0 && buildStart < suffixStart) {
    suffixStart = buildStart;
  }

  final core = value.substring(0, suffixStart);
  final parts = core.split('.');
  if (parts.isEmpty ||
      parts.length > 3 ||
      parts.any((part) => !_rangeCoreComponentPattern.hasMatch(part))) {
    throw FormatException('Invalid version range: $input');
  }

  final normalized = <String>[...parts];
  while (normalized.length < 3) {
    normalized.add('0');
  }
  final suffix = value.substring(suffixStart);
  try {
    return SemanticVersion.parse('${normalized.join('.')}$suffix');
  } on FormatException {
    throw FormatException('Invalid version range: $input');
  }
}

VersionRange _parseWildcardRange(RegExpMatch match) {
  final major = SemanticVersionNumber.parse(match.group(1)!);
  final minorText = match.group(2);
  final patchText = match.group(3);
  if (minorText == null ||
      minorText == 'x' ||
      minorText == 'X' ||
      minorText == '*') {
    final patchIsNumeric =
        patchText != null &&
        patchText != 'x' &&
        patchText != 'X' &&
        patchText != '*';
    if (patchIsNumeric) {
      throw FormatException(
        'Invalid wildcard version range: ${match.group(0)}',
      );
    }
    return VersionRange(
      min: SemanticVersion._fromNumbers(
        major,
        const SemanticVersionNumber.fromInt(0),
        const SemanticVersionNumber.fromInt(0),
      ),
      max: SemanticVersion._fromNumbers(
        major.increment(),
        const SemanticVersionNumber.fromInt(0),
        const SemanticVersionNumber.fromInt(0),
      ),
    );
  }
  final minor = SemanticVersionNumber.parse(minorText);
  if (patchText == null ||
      patchText == 'x' ||
      patchText == 'X' ||
      patchText == '*') {
    return VersionRange(
      min: SemanticVersion._fromNumbers(
        major,
        minor,
        const SemanticVersionNumber.fromInt(0),
      ),
      max: SemanticVersion._fromNumbers(
        major,
        minor.increment(),
        const SemanticVersionNumber.fromInt(0),
      ),
    );
  }
  throw FormatException('Invalid wildcard version range: ${match.group(0)}');
}
