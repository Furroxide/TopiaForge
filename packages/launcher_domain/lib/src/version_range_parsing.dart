part of 'versioning.dart';

SemanticVersion _parseVersionRangeVersion(String value, String? input) {
  try {
    return SemanticVersion.parse(value);
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
