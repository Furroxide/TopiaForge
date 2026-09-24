import 'dart:io';
import 'package:test/test.dart';
import 'package:topiaforge/src/sandbox_acceptance/native_driver_vocabulary.dart';

/// The verifier's driver vocabulary is an independent mirror of the broker's
/// C# source. These checks parse that source, so a vocabulary, per-kind field
/// set or bound changed on one side only fails here until both sides agree.
void main() {
  final vocabulary = _BrokerSource('DriverVocabulary.cs');
  final manifest = _BrokerSource('DriverManifest.cs');
  final validateStep = vocabulary.region(
    'internal static void ValidateStep',
    'internal static string ReplacementText',
  );
  const mirrors = <String, Set<String>>{
    'Keys': nativeDriverKeys,
    'Surfaces': nativeDriverSurfaces,
    'ScrollContainers': nativeScrollContainers,
    'Nodes': nativeDriverNodes,
    'RequestOperations': nativeDriverRequestOperations,
    'WireOperations': nativeOperations,
    'AimFacts': nativeDriverAimFacts,
    'DynamicTexts': nativeDriverDynamicTexts,
    'DynamicRows': nativeDriverDynamicRows,
    'Actions': nativeDriverActions,
  };

  test('closed vocabularies equal DriverVocabulary.cs', () {
    for (final MapEntry(key: name, value: mirror) in mirrors.entries) {
      final values = vocabulary.strings(name);
      expect(values.toSet(), hasLength(values.length), reason: '$name repeats');
      expect(values.toSet(), mirror, reason: name);
    }
  });

  test('atom kinds and closed per-kind field sets equal ValidateStep', () {
    final cases = _cases(validateStep);
    expect(cases.keys.toSet(), nativeDriverAtomSchemas.keys.toSet());
    for (final MapEntry(key: kind, value: schema) in cases.entries) {
      final mirror = nativeDriverAtomSchemas[kind]!;
      expect(schema.required, mirror.required, reason: '$kind required');
      expect(schema.oneOf, mirror.oneOf, reason: '$kind one-of');
      expect(schema.optional, mirror.optional, reason: '$kind optional');
    }
    // Every kind requires `kind` and admits an optional declared `surfaceId`.
    expect(
      validateStep,
      contains('new[] { "kind" }.Concat(fields).Concat(optionalSurface)'),
    );
    expect(
      validateStep,
      contains(
        'step.TryGetProperty("surfaceId", out _) ? new[] { "surfaceId" } : '
        'Array.Empty<string>()',
      ),
    );
  });

  test('field value vocabularies equal ValidateStep', () {
    final direct = RegExp(
      r'(\w+)\.Contains\(BoundedJson\.Text\(step, "(\w+)"\)\)',
    );
    final chosen = RegExp(
      r'(\w+) == "(\w+)" && !(\w+)\.Contains\(BoundedJson\.Text\(step, \1\)\)',
    );
    final fields = {
      for (final m in direct.allMatches(validateStep)) m[2]!: m[1]!,
      for (final m in chosen.allMatches(validateStep)) m[2]!: m[3]!,
    };
    expect(fields.keys.toSet(), nativeDriverFieldVocabularies.keys.toSet());
    for (final MapEntry(key: field, value: array) in fields.entries) {
      expect(
        nativeDriverFieldVocabularies[field],
        mirrors[array],
        reason: field,
      );
    }
  });

  test('integer and text bounds equal ValidateStep', () {
    final constants = vocabulary.constants();
    int bound(String token) => token.startsWith('-')
        ? -bound(token.substring(1))
        : int.tryParse(token) ?? constants[token] ?? fail('Unknown $token');
    final integers = {
      for (final m in RegExp(
        r'(?:BoundedJson\.Integer|SignedInteger)\(step, "(\w+)", (-?\w+), (-?\w+)\)',
      ).allMatches(validateStep))
        m[1]!: (bound(m[2]!), bound(m[3]!)),
    };
    expect(integers, nativeDriverIntegerFields);
    expect(
      int.parse(vocabulary.match(r'BoundedJson\.Text\(step, itemKey, (\d+)\)')),
      nativeDriverTextFields['itemId']!.$2,
    );
    expect(
      int.parse(
        vocabulary.match(
          r'text\.Length > (\d+) \|\| text\.Any\(char\.IsControl\)',
        ),
      ),
      nativeDriverTextFields['text']!.$2,
    );
  });

  test('recipe bounds and the complete inventory equal DriverManifest.cs', () {
    final bounds = RegExp(
      r'action\.Value\.GetArrayLength\(\) is < (\d+) or > (\d+)',
    ).firstMatch(manifest.text);
    expect(bounds, isNotNull, reason: 'DriverManifest recipe bound moved');
    expect(int.parse(bounds![1]!), 1);
    expect(int.parse(bounds[2]!), nativeDriverMaximumAtoms);
    expect(
      manifest.text,
      contains('!DriverVocabulary.Actions.Contains(action.Name)'),
    );
    expect(
      manifest.text,
      contains('DriverVocabulary.Actions.All(actions.ContainsKey)'),
    );
  });
}

/// One broker source file under `tools/TopiaForge.Acceptance.Windows`.
final class _BrokerSource {
  _BrokerSource(this.name)
    : text = File(
        '../../tools/TopiaForge.Acceptance.Windows/$name',
      ).readAsStringSync();
  final String name, text;

  /// First capture group of [pattern]; a missing shape fails the parity check.
  String match(String pattern) =>
      RegExp(pattern).firstMatch(text)?[1] ??
      fail('$name no longer matches /$pattern/; re-mirror the verifier.');

  /// Text from [start] up to [end], both of which must still exist.
  String region(String start, String end) {
    final from = text.indexOf(start), to = text.indexOf(end, from);
    if (from < 0 || to < 0) fail('$name no longer declares $start.');
    return text.substring(from, to);
  }

  /// A `string[]` array's literals with `.. Other` spreads expanded in place.
  List<String> strings(String array) => [
    for (final token in match(
      'string\\[\\] $array = \\[([^\\]]*)\\];',
    ).split(',').map((t) => t.trim()))
      if (RegExp(r'^"([^"]*)"$').firstMatch(token) case final literal?)
        literal[1]!
      else if (token.startsWith('..'))
        ...strings(token.substring(2).trim())
      else
        fail('$name: unrecognised $array entry $token.'),
  ];

  Map<String, int> constants() => {
    for (final declaration in RegExp(
      r'internal const int ([^;]+);',
    ).allMatches(text))
      for (final part in declaration[1]!.split(','))
        if (RegExp(r'^\s*(\w+) = (-?\d+)\s*$').firstMatch(part)
            case final constant?)
          constant[1]!: int.parse(constant[2]!)
        else
          part: fail('$name: unrecognised constant $part.'),
  };
}

/// The `ValidateStep` switch as closed per-kind field sets: literals passed to
/// `Fields(...)` are required; a field chosen by `TryGetProperty("a") ? "a" :
/// "b"` is one of two alternatives; one admitted only when present
/// (`? new[] { "a" } : Array.Empty<string>()`) is optional.
Map<String, NativeDriverAtomSchema> _cases(String validateStep) {
  final start = validateStep.indexOf('switch (kind)');
  final end = validateStep.indexOf('default: throw', start);
  if (start < 0 || end < 0) fail('ValidateStep no longer switches on kind.');
  final body = validateStep.substring(start, end);
  final labels = RegExp(r'case "([^"]+)":').allMatches(body).toList();
  return {
    for (var i = 0; i < labels.length; i++)
      labels[i][1]!: _schema(
        labels[i][1]!,
        body.substring(
          labels[i].end,
          i + 1 < labels.length ? labels[i + 1].start : body.length,
        ),
      ),
  };
}

NativeDriverAtomSchema _schema(String kind, String segment) {
  final calls = RegExp(r'Fields\((.*?)\);').allMatches(segment).toList();
  if (calls.length != 1) fail('ValidateStep $kind has no single Fields call.');
  final arguments = calls.single[1]!;
  final oneOf = <String>{}, optional = <String>{};
  for (final variable in RegExp(
    r'var (\w+) = step\.TryGetProperty\("(\w+)", out _\) \? ([^;]+);',
  ).allMatches(segment)) {
    if (!RegExp('\\b${variable[1]}\\b').hasMatch(arguments)) continue;
    final tail = variable[3]!;
    final pair = RegExp(r'^"(\w+)" : "(\w+)"$').firstMatch(tail);
    final present = RegExp(
      r'^new\[\] \{ "(\w+)" \} : Array\.Empty<string>\(\)$',
    ).firstMatch(tail);
    if (pair != null && pair[1] == variable[2]) {
      oneOf.addAll([pair[1]!, pair[2]!]);
    } else if (present != null && present[1] == variable[2]) {
      optional.add(present[1]!);
    } else {
      fail('ValidateStep $kind has an unrecognised field choice: $tail');
    }
  }
  return NativeDriverAtomSchema(
    {for (final m in RegExp(r'"(\w+)"').allMatches(arguments)) m[1]!},
    oneOf: oneOf,
    optional: optional,
  );
}
