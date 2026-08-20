import 'dart:convert';
import 'dart:io';

import 'package:test/test.dart';
import 'package:topiaforge/src/creator_acceptance_models.dart';
import 'package:topiaforge/src/creator_persistence_probe.dart';
import 'package:path/path.dart' as p;

/// Writes a Robotopia-shaped save document, optionally with a stamped gzip
/// header so the mtime-sensitivity of raw-byte hashing is exercised.
void _writeSave(
  Directory root,
  Map<String, Object?> data, {
  int gzipLevel = 6,
}) {
  final json = utf8.encode(jsonEncode({'version': 2, 'data': data}));
  File(p.join(root.path, CreatorPersistenceProbe.saveDocument))
    ..createSync(recursive: true)
    ..writeAsBytesSync(GZipCodec(level: gzipLevel).encode(json), flush: true);
}

const Map<String, Object?> _baseSave = {
  'CURRENT_MIC': 'Microphone (Some Device)',
  'ftue_audio_complete': true,
  'CURRENT_CHECKPOINT': 'F0Ql0Uceu2E',
  'SGJzGz9Pevo_reached': true,
  'INPUT_KEY': 1,
};

void main() {
  late Directory root;
  late CreatorPersistenceProbe probe;

  setUp(() {
    root = Directory.systemTemp.createTempSync('creator-probe-');
    probe = CreatorPersistenceProbe(rootOverride: root.path);
  });

  tearDown(() => root.deleteSync(recursive: true));

  test('digests decompressed content so gzip framing cannot fake a change', () {
    _writeSave(root, _baseSave);
    final first = probe.capture();
    // Re-encode the identical payload at a different compression level: the
    // raw bytes differ, the decompressed content does not.
    _writeSave(root, _baseSave, gzipLevel: 1);
    final second = probe.capture();
    expect(second.save, first.save);
    expect(second.checkpoint, first.checkpoint);
  });

  test('ignores declared volatile members', () {
    _writeSave(root, _baseSave);
    final before = probe.capture();
    File(p.join(root.path, 'Player.log')).writeAsStringSync('noise');
    File(p.join(root.path, 'robo_token.json')).writeAsStringSync('{"t":1}');
    Directory(
      p.join(root.path, 'PostHog', 'state'),
    ).createSync(recursive: true);
    File(
      p.join(root.path, 'PostHog', 'state', 'identity.json'),
    ).writeAsStringSync('{"id":2}');
    expect(probe.capture().save, before.save);
  });

  test('detects a mutated save payload', () {
    _writeSave(root, _baseSave);
    final before = probe.capture();
    _writeSave(root, {..._baseSave, 'INPUT_KEY': 2});
    expect(probe.capture().save, isNot(before.save));
  });

  test('detects a mutated checkpoint cursor', () {
    _writeSave(root, _baseSave);
    final before = probe.capture();
    _writeSave(root, {..._baseSave, 'CURRENT_CHECKPOINT': 'different'});
    final after = probe.capture();
    expect(after.checkpoint, isNot(before.checkpoint));
  });

  test('detects a newly reached checkpoint flag', () {
    _writeSave(root, _baseSave);
    final before = probe.capture();
    _writeSave(root, {..._baseSave, 'AAnewCheckpoint_reached': true});
    expect(probe.capture().checkpoint, isNot(before.checkpoint));
  });

  test('non-checkpoint settings do not perturb the checkpoint digest', () {
    _writeSave(root, _baseSave);
    final before = probe.capture();
    _writeSave(root, {..._baseSave, 'CURRENT_MIC': 'Another Device'});
    final after = probe.capture();
    expect(after.checkpoint, before.checkpoint);
    expect(after.save, isNot(before.save));
  });

  test('captures a save document introduced by a later build', () {
    _writeSave(root, _baseSave);
    final before = probe.capture();
    File(p.join(root.path, 'new_progress.dat')).writeAsStringSync('progress');
    expect(probe.capture().save, isNot(before.save));
  });

  test('fails closed when the save document is absent', () {
    expect(
      () => probe.capture(),
      throwsA(
        isA<CreatorAcceptanceError>().having(
          (error) => error.code,
          'code',
          'TFCREATOR142',
        ),
      ),
    );
  });

  test('fails closed when the declared root is absent', () {
    final missing = CreatorPersistenceProbe(
      rootOverride: p.join(root.path, 'nope'),
    );
    expect(
      () => missing.capture(),
      throwsA(
        isA<CreatorAcceptanceError>().having(
          (error) => error.code,
          'code',
          'TFCREATOR140',
        ),
      ),
    );
  });

  test('fails closed when the save document changes shape', () {
    final bytes = utf8.encode(jsonEncode({'version': 2, 'unexpected': {}}));
    File(p.join(root.path, CreatorPersistenceProbe.saveDocument))
      ..createSync(recursive: true)
      ..writeAsBytesSync(gzip.encode(bytes), flush: true);
    expect(
      () => probe.capture(),
      throwsA(
        isA<CreatorAcceptanceError>().having(
          (error) => error.code,
          'code',
          'TFCREATOR144',
        ),
      ),
    );
  });

  test('declares the layout it actually used', () {
    final layout = probe.describeLayout();
    expect(layout.version, CreatorPersistenceLayout.currentVersion);
    expect(layout.roots, isNotEmpty);
    expect(layout.exclusions, contains('PostHog/'));
    expect(layout.exclusions, contains('Player.log'));
  });
}
