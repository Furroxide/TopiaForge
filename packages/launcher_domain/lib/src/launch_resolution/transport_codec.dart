part of '../launch_resolution.dart';

/// Strict string codecs also reject duplicate properties before JSON loses them.
abstract final class LaunchTransport {
  static const maxDocumentBytes = 4 * 1024 * 1024;
  static const maxObservationBytes = 16 * 1024 * 1024;

  static LaunchPlanDescriptor readPlan(String json) =>
      LaunchPlanDescriptor.fromJson(_decodeContract(json, maxDocumentBytes));
  static ProfileLaunchConfigurationV4 readProfile(String json) =>
      ProfileLaunchConfigurationV4.fromJson(
        _decodeContract(json, maxDocumentBytes),
      );
  static LaunchProgress readProgress(String json) =>
      LaunchProgress.fromJson(_decodeContract(json, maxDocumentBytes));
  static LaunchOutcome readOutcome(String json) =>
      LaunchOutcome.fromJson(_decodeContract(json, maxDocumentBytes));
  static LaunchObservationEnvelope readObservation(String json) =>
      LaunchObservationEnvelope.fromJson(
        _decodeContract(json, maxObservationBytes),
      );

  static String writePlan(LaunchPlanDescriptor value) =>
      _encodeContract(value.toJson(), maxDocumentBytes);
  static String writeProfile(ProfileLaunchConfigurationV4 value) =>
      _encodeContract(value.toJson(), maxDocumentBytes);
  static String writeProgress(LaunchProgress value) =>
      _encodeContract(value.toJson(), maxDocumentBytes);
  static String writeOutcome(LaunchOutcome value) =>
      _encodeContract(value.toJson(), maxDocumentBytes);
  static String writeObservation(LaunchObservationEnvelope value) =>
      _encodeContract(value.toJson(), maxObservationBytes);
}

Object? _decodeContract(String source, int limit) {
  if (utf8.encode(source).length > limit) {
    throw const FormatException('Launch contract exceeds its size limit.');
  }
  _UniqueJsonProperties(source).validate();
  return jsonDecode(source);
}

String _encodeContract(Map<String, Object?> value, int limit) {
  final json = jsonEncode(value);
  if (utf8.encode(json).length > limit) {
    throw const FormatException('Launch contract exceeds its size limit.');
  }
  return json;
}

class _UniqueJsonProperties {
  _UniqueJsonProperties(this.source);
  final String source;
  int position = 0;
  void validate() {
    _value(0);
    _space();
    if (position != source.length) _invalid();
  }

  void _invalid() =>
      throw const FormatException('Invalid or duplicate JSON property.');
  void _space() {
    while (position < source.length &&
        const [9, 10, 13, 32].contains(source.codeUnitAt(position))) {
      position++;
    }
  }

  bool _take(String char) {
    _space();
    if (position < source.length && source[position] == char) {
      position++;
      return true;
    }
    return false;
  }

  void _need(String char) {
    if (!_take(char)) _invalid();
  }

  void _value(int depth) {
    if (depth > 128) _invalid();
    _space();
    if (position >= source.length) _invalid();
    if (_take('{')) {
      final seen = <String>{};
      if (_take('}')) return;
      do {
        _space();
        final key = _string();
        if (!seen.add(key)) _invalid();
        _need(':');
        _value(depth + 1);
        if (_take('}')) return;
        _need(',');
      } while (true);
    }
    if (_take('[')) {
      if (_take(']')) return;
      do {
        _value(depth + 1);
        if (_take(']')) return;
        _need(',');
      } while (true);
    }
    if (source[position] == '"') {
      _string();
      return;
    }
    final start = position;
    while (position < source.length &&
        !',]} \t\r\n'.contains(source[position])) {
      position++;
    }
    if (start == position) _invalid();
    jsonDecode(source.substring(start, position));
  }

  String _string() {
    final start = position;
    _need('"');
    while (position < source.length) {
      final char = source[position++];
      if (char == '"') {
        return jsonDecode(source.substring(start, position)) as String;
      }
      if (char == '\\') position++;
    }
    _invalid();
    return '';
  }
}
