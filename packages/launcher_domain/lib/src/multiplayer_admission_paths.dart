part of 'multiplayer_admission.dart';

String? _admissionPathCollisionKey(String value) {
  if (value.runes.length > 1024 ||
      value.contains(r'\') ||
      value.startsWith('/') ||
      value.trim().isEmpty) {
    return null;
  }
  try {
    if (unicode.nfc(value) != value) return null;
    final folded = <String>[];
    for (final segment in value.split('/')) {
      if (segment.runes.length > 255 ||
          _isUnsafeAdmissionPathSegment(segment)) {
        return null;
      }
      final key = unicode
          .nfkc(segment)
          .toUpperCase()
          .replaceAll('\u00df', 'SS')
          .replaceAll('\u1e9e', 'SS');
      if (key.contains('/') ||
          key.contains(r'\') ||
          _isUnsafeAdmissionPathSegment(key)) {
        return null;
      }
      folded.add(key);
    }
    return folded.join('/');
  } on Object {
    return null;
  }
}

bool _isUnsafeAdmissionPathSegment(String segment) {
  if (segment.isEmpty ||
      segment == '.' ||
      segment == '..' ||
      segment.contains(':') ||
      segment.endsWith(' ') ||
      segment.endsWith('.') ||
      segment.codeUnits.any((unit) => unit < 0x20)) {
    return true;
  }
  return _admissionWindowsDeviceNames.contains(
    segment.split('.').first.toLowerCase(),
  );
}

const _admissionWindowsDeviceNames = {
  'con',
  'prn',
  'aux',
  'nul',
  'com1',
  'com2',
  'com3',
  'com4',
  'com5',
  'com6',
  'com7',
  'com8',
  'com9',
  'lpt1',
  'lpt2',
  'lpt3',
  'lpt4',
  'lpt5',
  'lpt6',
  'lpt7',
  'lpt8',
  'lpt9',
};
