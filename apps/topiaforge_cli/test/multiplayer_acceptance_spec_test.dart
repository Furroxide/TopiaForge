import 'dart:convert';
import 'dart:io';
import 'package:test/test.dart';
import 'package:topiaforge/src/multiplayer_acceptance_spec.dart';
import 'package:topiaforge/src/multiplayer_acceptance_runner.dart';

void main() {
  final source = File(
    '../../tests/multiplayer-live-acceptance.json',
  ).readAsStringSync();
  final spec = MultiplayerAcceptanceSpec.decode(source);
  test(
    'service prerequisites and not-run observations cannot become a pass',
    () {
      expect(spec.cases.where((c) => c.requiresService).map((c) => c.id), [
        'multiplayer.invite',
        'multiplayer.relay',
      ]);
      final observations = MultiplayerAcceptanceObservations(
        'a' * 64,
        spec.select(['multiplayer.invite']),
      );
      for (final peer in ['host', 'guest']) {
        for (final message in [
          'TF-MP-SESSION|READY|${'a' * 64}|$peer',
          'TF-MP-ACCEPT|NOT-RUN|${'a' * 64}|$peer|multiplayer.invite|automatic',
        ]) {
          observations.observe(
            peer: peer,
            source: MultiplayerAcceptanceSpec.packageId,
            level: 'INFO',
            message: message,
          );
        }
      }
      expect(observations.settled, isTrue);
      expect(observations.complete, isFalse);
      expect(observations.notRun, hasLength(2));
      for (final peer in ['host', 'guest']) {
        observations.observe(
          peer: peer,
          source: MultiplayerAcceptanceSpec.packageId,
          level: 'INFO',
          message:
              'TF-MP-ACCEPT|PASS|${'a' * 64}|$peer|multiplayer.invite|automatic',
        );
      }
      expect(observations.missing, isEmpty);
      expect(observations.complete, isFalse, reason: 'Not-run is sticky.');
    },
  );
  test('separate listen-pair inventory contains every acceptance row', () {
    expect(spec.cases.length, 25);
    expect(spec.cases.where((c) => c.manual).length, 13);
    final dry = spec.dryRun([]);
    expect(dry['requiredObservations'], 49);
    expect(dry['liveEvidence'], isFalse);
    expect(dry['dryRun'], isTrue);
    expect(dry['peers'], ['host', 'guest']);
    expect((dry['stages'] as List).take(2), [
      'validate-package',
      'admit-both-layouts',
    ]);
  });
  test(
    'selection preserves canonical order and rejects unknown or repeated cases',
    () {
      expect(
        spec.select(['multiplayer.lan', 'multiplayer.host-start']).first.id,
        'multiplayer.host-start',
      );
      expect(() => spec.select(['unknown']), throwsFormatException);
      expect(
        () => spec.select(['multiplayer.lan', 'multiplayer.lan']),
        throwsFormatException,
      );
    },
  );
  for (final field in ['schemaVersion', 'topology', 'packageId', 'cases']) {
    test('invalid $field fails closed', () {
      final bad = jsonDecode(source) as Map<String, dynamic>;
      bad[field] = null;
      expect(
        () => MultiplayerAcceptanceSpec.decode(jsonEncode(bad)),
        throwsFormatException,
      );
    });
  }
  test('overlapping peer roots are refused', () {
    final roots = List.generate(6, (i) => Directory('peer-$i').absolute.path);
    requireSeparateMultiplayerRoots(roots);
    roots[5] = roots[0];
    expect(() => requireSeparateMultiplayerRoots(roots), throwsStateError);
  });
  test('each peer needs its own challenge-bound attributed observation', () {
    final observations = MultiplayerAcceptanceObservations(
      'a' * 64,
      spec.select(['multiplayer.lan']),
    );
    void emit(
      String peer, {
      String? source,
      String? challenge,
      String? claimed,
    }) => observations.observe(
      peer: peer,
      source: source ?? MultiplayerAcceptanceSpec.packageId,
      level: 'INFO',
      message:
          'TF-MP-ACCEPT|PASS|${challenge ?? 'a' * 64}|${claimed ?? peer}|multiplayer.lan|automatic',
    );
    emit('host', source: 'other');
    emit('host', challenge: 'b' * 64);
    emit('host', claimed: 'guest');
    expect(observations.missing.length, 2);
    emit('host');
    emit('host');
    expect(observations.missing, ['guest:multiplayer.lan']);
    emit('guest');
    expect(
      observations.complete,
      isFalse,
      reason: 'Case markers alone cannot certify a session.',
    );
    for (final peer in ['host', 'guest']) {
      observations.observe(
        peer: peer,
        source: MultiplayerAcceptanceSpec.packageId,
        level: 'INFO',
        message: 'TF-MP-SESSION|READY|${'a' * 64}|$peer',
      );
    }
    expect(observations.complete, isTrue);
  });
  test(
    'automatic marker cannot stand in for a manual check; failure is sticky',
    () {
      final observations = MultiplayerAcceptanceObservations(
        'a' * 64,
        spec.select(['multiplayer.voice-ptt']),
      );
      void emit(
        String peer,
        String result,
        String mode,
      ) => observations.observe(
        peer: peer,
        source: MultiplayerAcceptanceSpec.packageId,
        level: result == 'PASS' ? 'INFO' : 'ERROR',
        message:
            'TF-MP-ACCEPT|$result|${'a' * 64}|$peer|multiplayer.voice-ptt|$mode',
      );
      emit('host', 'PASS', 'automatic');
      expect(observations.missing.length, 2);
      emit('host', 'FAIL', 'manual');
      emit('host', 'PASS', 'manual');
      emit('guest', 'PASS', 'manual');
      expect(observations.complete, isFalse);
      expect(observations.failures, ['host:multiplayer.voice-ptt']);
    },
  );
}
