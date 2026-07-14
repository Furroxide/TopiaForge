import 'spdx_ids_3_28.g.dart';

/// Minimal, deterministic SPDX expression grammar validation for publication
/// gates. Identifier membership is pinned to the vendored SPDX License List
/// 3.28.0 so a typo cannot silently become a publishable license.
class SpdxExpressionValidator {
  const SpdxExpressionValidator._();

  static String? validate(String value, {bool allowNoAssertion = false}) {
    final expression = value.trim();
    if (expression.isEmpty) {
      return 'license must be a non-empty SPDX expression.';
    }
    final normalized = expression.toUpperCase();
    if (allowNoAssertion && normalized == 'NOASSERTION') return null;
    if (_blockedPlaceholders.contains(normalized) &&
        !(allowNoAssertion && normalized == 'NOASSERTION')) {
      return 'license "$expression" is a placeholder, not a publishable SPDX expression.';
    }

    final tokenizer = _SpdxTokenizer(expression);
    try {
      final parser = _SpdxParser(tokenizer.tokens());
      parser.parse();
    } on FormatException catch (error) {
      return 'license is not a valid SPDX expression: ${error.message}';
    }
    return null;
  }

  static const _blockedPlaceholders = {
    'UNSPECIFIED',
    'UNKNOWN',
    'TBD',
    'TODO',
    'NONE',
    'NOASSERTION',
    'OWNER_DECISION_REQUIRED',
  };
}

bool isSafePackageRelativePath(String value) {
  final path = value.trim();
  if (path.isEmpty || path.length > 240) {
    return false;
  }
  if (path.startsWith('/') ||
      path.contains(r'\') ||
      path.contains(':') ||
      path.contains('//') ||
      RegExp(r'[\x00-\x1f]').hasMatch(path)) {
    return false;
  }
  for (final segment in path.split('/')) {
    if (segment.isEmpty ||
        segment == '.' ||
        segment == '..' ||
        segment.endsWith('.') ||
        segment.endsWith(' ')) {
      return false;
    }
  }
  return true;
}

class _SpdxTokenizer {
  const _SpdxTokenizer(this.source);

  final String source;

  List<String> tokens() {
    final tokens = <String>[];
    var offset = 0;
    while (offset < source.length) {
      final character = source[offset];
      if (character.trim().isEmpty) {
        offset += 1;
        continue;
      }
      if (character == '(' || character == ')') {
        tokens.add(character);
        offset += 1;
        continue;
      }
      final match = RegExp(
        r'^(?:DocumentRef-[A-Za-z0-9.-]+:)?LicenseRef-[A-Za-z0-9.-]+|^[A-Za-z0-9][A-Za-z0-9.+-]*',
      ).firstMatch(source.substring(offset));
      if (match == null) {
        throw FormatException('unexpected character at column ${offset + 1}.');
      }
      tokens.add(match.group(0)!);
      offset += match.group(0)!.length;
    }
    return tokens;
  }
}

class _SpdxParser {
  _SpdxParser(this.tokens);

  final List<String> tokens;
  var _offset = 0;

  void parse() {
    if (tokens.isEmpty) {
      throw const FormatException('expression is empty.');
    }
    _parseOr();
    if (_offset != tokens.length) {
      throw FormatException('unexpected token "${tokens[_offset]}".');
    }
  }

  void _parseOr() {
    _parseAnd();
    while (_take('OR')) {
      _parseAnd();
    }
  }

  void _parseAnd() {
    _parseWith();
    while (_take('AND')) {
      _parseWith();
    }
  }

  void _parseWith() {
    _parsePrimary();
    if (_take('WITH')) {
      final exception = _nextIdentifier('exception identifier');
      if (!spdxExceptionIds.contains(exception)) {
        throw FormatException('unknown SPDX exception "$exception".');
      }
    }
  }

  void _parsePrimary() {
    if (_take('(')) {
      _parseOr();
      if (!_take(')')) {
        throw const FormatException('missing closing parenthesis.');
      }
      return;
    }
    final license = _nextIdentifier('license identifier');
    if (!_isLicenseReference(license) && !spdxLicenseIds.contains(license)) {
      throw FormatException(
        'unknown SPDX license "$license" (list $spdxLicenseListVersion).',
      );
    }
  }

  String _nextIdentifier(String label) {
    if (_offset >= tokens.length) {
      throw FormatException('expected $label.');
    }
    final value = tokens[_offset];
    if (value == '(' ||
        value == ')' ||
        value == 'AND' ||
        value == 'OR' ||
        value == 'WITH') {
      throw FormatException('expected $label, got "$value".');
    }
    _offset += 1;
    return value;
  }

  bool _take(String value) {
    if (_offset < tokens.length && tokens[_offset] == value) {
      _offset += 1;
      return true;
    }
    return false;
  }
}

bool _isLicenseReference(String value) => RegExp(
  r'^(?:DocumentRef-[A-Za-z0-9.-]+:)?LicenseRef-[A-Za-z0-9.-]+$',
).hasMatch(value);
