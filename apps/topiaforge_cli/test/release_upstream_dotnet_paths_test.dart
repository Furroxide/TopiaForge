import 'dart:convert';
import 'dart:io';

import 'package:path/path.dart' as p;
import 'package:test/test.dart';
import 'package:topiaforge/src/release_build_host_paths.dart';
import 'package:topiaforge/src/release_upstream_dotnet_paths.dart';

String _root() {
  var directory = Directory.current.absolute;
  while (!File(p.join(directory.path, 'TopiaForge.slnx')).existsSync()) {
    final parent = directory.parent;
    if (parent.path == directory.path) throw StateError('Repository not found');
    directory = parent;
  }
  return directory.path;
}

void main() {
  final provenance =
      jsonDecode(
            File(
              p.join(_root(), 'release/dotnet-upstream-source-paths-v1.json'),
            ).readAsStringSync(),
          )
          as Map<String, dynamic>;
  final members = (provenance['members'] as List).cast<Map<String, dynamic>>();
  final literals = {
    for (final member in members)
      for (final finding in member['findings'] as List)
        (finding as Map)['literal'] as String,
  }.toList()..sort();
  late Directory temp;
  setUp(() => temp = Directory.systemTemp.createTempSync('upstream-paths-'));
  tearDown(() => temp.deleteSync(recursive: true));

  File binary(List<int> bytes) =>
      File(p.join(temp.path, 'binary'))
        ..writeAsBytesSync([0xCF, 0xFA, 0xED, 0xFE, ...bytes]);
  void clean({int chunk = 64}) =>
      BuildHostPathScanner(chunkSize: chunk).assertClean(temp.path);

  test('provenance binds both upstream hosts to the pinned runtime', () {
    expect(provenance['runtimeVersion'], '10.0.9');
    expect(
      UpstreamDotnetSourcePaths.runtimeVersion,
      provenance['runtimeVersion'],
    );
    expect(UpstreamDotnetSourcePaths.literals, unorderedEquals(literals));
    expect(provenance['commit'], '901ca941248413c79832d2fdbd709da0c4386353');
    expect(members.map((m) => (m['findings'] as List).length), [19, 14]);
    expect(
      File(p.join(_root(), 'Directory.Build.props')).readAsStringSync(),
      contains(
        '<TopiaForgeDotNetRuntimeVersion>10.0.9</TopiaForgeDotNetRuntimeVersion>',
      ),
    );
    for (final member in members) {
      expect(member['packageSha256'], matches(r'^[a-f0-9]{64}$'));
      expect(member['memberSha256'], matches(r'^[a-f0-9]{64}$'));
      expect(member['encoding'], 'ASCII');
    }
  });

  test('curated classifier cannot be mutated', () {
    const unknown = '/Users/runner/work/1/s/src/runtime/src/unreviewed.cs';
    final curated = UpstreamDotnetSourcePaths.literals;
    try {
      expect(() => curated.add(unknown), throwsUnsupportedError);
    } finally {
      if (curated.contains(unknown)) curated.remove(unknown);
    }
  });
  for (var index = 0; index < literals.length; index++) {
    final literal = literals[index];
    test('accepts verified complete upstream literal $index', () {
      binary([0, ...literal.codeUnits, 0]);
      expect(clean, returnsNormally);
    });
  }

  test('accepts all public literals across every small chunk alignment', () {
    for (final chunk in [1, 2, 7, 16, 64, 257]) {
      for (var padding = 0; padding < chunk; padding++) {
        binary([
          ...List.filled(padding, 0x41),
          0,
          for (final literal in literals) ...[...literal.codeUnits, 0],
          ...List.filled(300, 0x41),
        ]);
        expect(
          () => clean(chunk: chunk),
          returnsNormally,
          reason: 'chunk=$chunk padding=$padding',
        );
      }
    }
  });

  test('terminator at EOF still admits the complete upstream literal', () {
    binary([0, ...literals.first.codeUnits, 0]);
    expect(() => clean(chunk: 1), returnsNormally);
  });

  test('known public paths never hide an adjacent private path', () {
    for (final suffix in [
      '/Users/private-builder/code/main.dart',
      r'C:\Users\private-builder\code\main.cs',
      '/home/private-builder/code/main.dart',
    ]) {
      for (final chunk in [1, 7, 64, 257]) {
        binary([0, ...literals.first.codeUnits, 0, ...suffix.codeUnits, 0]);
        expect(() => clean(chunk: chunk), throwsStateError);
      }
    }
  });

  test('unknown source files under the same public-looking prefix fail', () {
    binary('/Users/runner/work/1/s/src/runtime/src/private.cs\u0000'.codeUnits);
    expect(clean, throwsStateError);
  });

  test('known literal with suffix, prefix or missing NUL bounds fails', () {
    for (final bytes in [
      [0, ...'${literals.first}.private'.codeUnits, 0],
      [0, ...'private${literals.first}'.codeUnits, 0],
      [0, ...literals.first.codeUnits],
      [...literals.first.codeUnits, 0],
    ]) {
      binary(bytes);
      expect(() => clean(chunk: 1), throwsStateError);
    }
  });

  test('UTF16 copies of public-looking paths remain unapproved', () {
    for (final littleEndian in [true, false]) {
      final utf16 = <int>[
        for (final byte in literals.first.codeUnits)
          ...littleEndian ? [byte, 0] : [0, byte],
      ];
      binary([0, ...utf16, 0, 0]);
      expect(() => clean(chunk: 7), throwsStateError);
    }
  });
}
