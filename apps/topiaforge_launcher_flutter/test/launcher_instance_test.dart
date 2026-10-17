import 'package:flutter_test/flutter_test.dart';
import 'package:topiaforge_launcher_flutter/src/launcher_instance.dart';
import 'package:topiaforge_launcher_flutter/src/update_health_handshake.dart';

/// A scripted runner that records every call.
final class _FakePort implements LauncherInstancePort {
  _FakePort(this.claims, {this.accepts = true});

  /// The answers to successive claims; the last one repeats.
  final List<LauncherInstanceClaim> claims;
  final bool accepts;
  final calls = <String>[];
  final handedOff = <String?>[];
  Object? failure;

  @override
  Future<LauncherInstanceClaim> claim(String scope) async {
    calls.add('claim $scope');
    final error = failure;
    if (error != null) throw error;
    final index = calls.where((call) => call.startsWith('claim')).length - 1;
    return claims[index < claims.length ? index : claims.length - 1];
  }

  @override
  Future<bool> handOff(String? link) async {
    calls.add('handOff');
    handedOff.add(link);
    return accepts;
  }

  @override
  Future<void> release() async => calls.add('release');
}

void main() {
  const scope = '0123456789abcdef0123456789abcdef';
  const invite = 'topiaforge://join/ABCDE12345';
  final nonce = List.filled(64, 'a').join();
  final health = [
    updateHealthNonceOption,
    nonce,
    updateHealthFileOption,
    r'C:\data\updates\transactions\0123456789abcdef0123456789abcdef\health.json',
  ];

  Future<LauncherInstanceRole> resolve(
    _FakePort port,
    List<String> arguments,
  ) => LauncherInstanceGate(port).resolve(arguments, scope: scope);

  group('a start that owns its scope', () {
    test('starts normally and hands nothing off', () async {
      final port = _FakePort([LauncherInstanceClaim.primary]);

      expect(await resolve(port, [invite]), LauncherInstanceRole.primary);
      expect(port.calls, ['claim $scope']);
    });

    test('starts normally where the system keeps it single', () async {
      final port = _FakePort([LauncherInstanceClaim.notSupported]);

      expect(await resolve(port, [invite]), LauncherInstanceRole.primary);
      expect(port.handedOff, isEmpty);
    });

    test('starts normally when the runner fails', () async {
      final port = _FakePort([LauncherInstanceClaim.secondary])
        ..failure = StateError('channel broke');

      expect(await resolve(port, [invite]), LauncherInstanceRole.primary);
      expect(port.handedOff, isEmpty);
    });
  });

  group('a later start', () {
    test('hands its link to the owner and exits', () async {
      final port = _FakePort([LauncherInstanceClaim.secondary]);

      expect(await resolve(port, [invite]), LauncherInstanceRole.handedOff);
      expect(port.handedOff, [invite]);
    });

    test('without a link only asks the owner to come forward', () async {
      final port = _FakePort([LauncherInstanceClaim.secondary]);

      expect(await resolve(port, const []), LauncherInstanceRole.handedOff);
      expect(port.handedOff, [null]);
    });

    test('hands off an invalid link too, so the owner can say why', () async {
      final port = _FakePort([LauncherInstanceClaim.secondary]);

      await resolve(port, ['TOPIAFORGE://join/not-a-code']);
      expect(port.handedOff, ['TOPIAFORGE://join/not-a-code']);
    });

    test('a link split by a quote is still a link start, not a health '
        'check', () async {
      // `topiaforge://join/ABCDE12345" --topiaforge-update-health-nonce "`
      // through a `"%1"` registration, as a shell splits it.
      final port = _FakePort([LauncherInstanceClaim.secondary]);

      final role = await resolve(port, [invite, updateHealthNonceOption, '']);

      expect(role, LauncherInstanceRole.handedOff);
      expect(port.handedOff, [invite]);
    });

    test('claims again when the owner does not answer', () async {
      final port = _FakePort([
        LauncherInstanceClaim.secondary,
        LauncherInstanceClaim.primary,
      ], accepts: false);

      expect(await resolve(port, [invite]), LauncherInstanceRole.primary);
      expect(port.calls, ['claim $scope', 'handOff', 'claim $scope']);
    });

    test('runs beside an owner that stays silent', () async {
      final port = _FakePort([LauncherInstanceClaim.secondary], accepts: false);

      expect(await resolve(port, [invite]), LauncherInstanceRole.standalone);
    });
  });

  group('an update health check', () {
    test('never hands off to a running launcher', () async {
      final port = _FakePort([LauncherInstanceClaim.secondary]);

      expect(await resolve(port, health), LauncherInstanceRole.standalone);
      expect(port.calls, ['claim $scope']);
      expect(port.handedOff, isEmpty);
    });

    test('claims the scope when it is free, so links reach it', () async {
      final port = _FakePort([LauncherInstanceClaim.primary]);

      expect(await resolve(port, health), LauncherInstanceRole.primary);
    });

    test('is only the helper\'s exact command line', () async {
      for (final arguments in [
        [...health, invite],
        [invite, ...health],
        [updateHealthNonceOption, nonce],
        [updateHealthFileOption, 'health.json'],
        [health[2], health[3], health[0], health[1]],
      ]) {
        final port = _FakePort([LauncherInstanceClaim.secondary]);
        expect(
          await resolve(port, arguments),
          LauncherInstanceRole.handedOff,
          reason: '$arguments',
        );
      }
    });
  });

  group('the command line', () {
    test('names a health check only in the helper\'s exact shape', () {
      expect(isUpdateHealthStart(health), isTrue);
      for (final arguments in [
        const <String>[],
        [invite],
        [...health, 'extra'],
        ['extra', ...health],
        [health[0], '', health[2], health[3]],
        [health[0], nonce.toUpperCase(), health[2], health[3]],
        [health[0], '${nonce}a', health[2], health[3]],
        [health[0], nonce, health[2], ''],
        [health[2], health[3], health[0], health[1]],
        [health[0], nonce, health[0], health[3]],
      ]) {
        expect(isUpdateHealthStart(arguments), isFalse, reason: '$arguments');
      }
    });

    test('is a link start only when its first argument is a link', () {
      expect(sessionLinkArgument(const []), isNull);
      expect(sessionLinkArgument([invite]), invite);
      expect(sessionLinkArgument([invite, 'ignored', 'too']), invite);
      expect(
        sessionLinkArgument(['TopiaForge://join/ZZZZZ99999']),
        'TopiaForge://join/ZZZZZ99999',
      );
      expect(sessionLinkArgument(['--verbose', invite]), isNull);
      expect(sessionLinkArgument([...health, invite]), isNull);
      expect(sessionLinkArgument(['https://example.com/join']), isNull);
      expect(sessionLinkArgument(['topiaforgex://join/ABCDE12345']), isNull);
    });
  });
}
