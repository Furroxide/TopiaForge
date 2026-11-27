import 'dart:io';

import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_host_served_support.dart';
import 'multiplayer_join_content_fixtures.dart';
import 'multiplayer_network_support.dart';

void main() {
  test(
    'a host id cannot enter the log when its install folder is blocked',
    () async {
      const address = '203.0.113.77';
      final host = await ServingHost.start([TestArchive(address, '1.0.0')]);
      final join = await HostJoin.create(host);
      await join.hearHost();
      final installing = await join.drive(
        await join.downloading(),
        until: JoinStage.installing,
      );
      final target = packageFolder(join.rig.game, address, '1.0.0');
      target.parent.parent.createSync(recursive: true);
      File(target.parent.path).writeAsStringSync('A conflicting local file.');
      final ended = await join.drive(installing);
      expect(ended.outcome!.failure!.code, JoinFailureCode.installFailed);
      expect(join.launcherLog(), contains('join install failed'));
      expect(join.launcherLog(), isNot(contains(address)));
      expect(target.existsSync(), isFalse);
    },
  );

  for (final field in ['id', 'version']) {
    test(
      'host-chosen $field cannot put an address in the download log',
      () async {
        const address = '203.0.113.77';
        final archive = TestArchive(
          field == 'id' ? address : 'example.hosted',
          field == 'version' ? '1.0.0-peer.$address' : '1.0.0',
        );
        final host = await ServingHost.start([archive]);
        final join = await HostJoin.create(host);
        await join.hearHost();
        host.answerPackages = (socket, _) =>
            LoopbackDescriptorHost.raw(socket, 'TFD1 503\n'.codeUnits);
        final ended = await join.drive(await join.downloading());
        expect(ended.outcome!.failure!.code, JoinFailureCode.downloadFailed);
        expect(host.packageRequests, 1);
        expect(join.launcherLog(), contains('Join download'));
        expect(join.launcherLog(), isNot(contains(address)));
        expect(join.cacheFiles(), isEmpty);
      },
    );
  }

  for (final address in [
    '203.0.113.77',
    'peer:2001:db8::77',
    'peer:2001:db8::',
    'fe80::1%4',
    '[2001:db8::77]:27250',
    '::ffff:203.0.113.77',
    '::ffff:cb00:714d',
    '64:ff9b::cb00:714d',
    '203.0.113.77.example.net',
    '2001:db8::77.example.net',
    'dead2001:db8::77',
    '203.0.113.77.1',
  ]) {
    test(
      'a host archive cannot put $address in a validation error log',
      () async {
        final archive = TestArchive(
          'example.hosted',
          '1.0.0',
          manifestChanges: {'entryAssembly': 'peer.$address.dll'},
        );
        final host = await ServingHost.start([archive]);
        final join = await HostJoin.create(host);
        await join.hearHost();
        final ended = await join.drive(await join.downloading());
        expect(ended.outcome!.failure!.code, JoinFailureCode.manifestMismatch);
        expect(join.launcherLog(), contains('Join download'));
        expect(join.launcherLog(), isNot(contains(address)));
        expect(join.cacheFiles(), isEmpty);
      },
    );
  }

  test(
    'a megabyte of archive error text is logged cut, without a long wait',
    () async {
      // Read whole, this much host-written text would hold the launcher's
      // only isolate for over a minute.
      final archive = TestArchive(
        'example.hosted',
        '1.0.0',
        manifestChanges: {'entryAssembly': '${'2:' * 2000} ' * 250},
      );
      final host = await ServingHost.start([archive]);
      final join = await HostJoin.create(host);
      await join.hearHost();
      final downloading = await join.downloading();
      final watch = Stopwatch()..start();
      final ended = await join.drive(downloading);
      expect(watch.elapsed, lessThan(const Duration(seconds: 20)));
      expect(ended.outcome!.failure!.code, JoinFailureCode.manifestMismatch);
      final line = join
          .launcherLog()
          .split('\n')
          .firstWhere((line) => line.contains('Join download'));
      expect(line.length, lessThan(4200));
      expect(line, endsWith('…'));
      expect(join.cacheFiles(), isEmpty);
    },
    timeout: const Timeout(Duration(minutes: 3)),
  );

  test(
    'the log never cuts a word into what reads as an address',
    () async {
      // Each word is no address, but cut after its eleventh or twelfth
      // letter it would read 203.0.113.7 or 203.0.113.77. Fifteen shifts
      // move the log's limit across a whole word.
      for (var shift = 0; shift < 15; shift++) {
        final archive = TestArchive(
          'example.hosted',
          '1.0.0',
          manifestChanges: {
            'entryAssembly': '${'x' * shift} ${'203.0.113.7777 ' * 300}',
          },
        );
        final host = await ServingHost.start([archive]);
        final join = await HostJoin.create(host);
        await join.hearHost();
        final ended = await join.drive(await join.downloading());
        expect(ended.outcome!.failure!.code, JoinFailureCode.manifestMismatch);
        expect(
          join.launcherLog(),
          isNot(matches(RegExp(r'203\.0\.113\.77?(?![0-9])'))),
          reason: 'shift $shift',
        );
      }
    },
    // Fifteen joins, each with its own host.
    timeout: const Timeout(Duration(minutes: 2)),
  );
}
