import 'dart:io';

import 'package:launcher_data/src/multiplayer/host_package_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

import 'multiplayer_network_support.dart';

/// Whom the host-served package client may contact, through a recording
/// connector or with every connection refused: under relay-only nothing
/// beyond the local network, and nothing at all for a cancelled download, an
/// impossible port or a file that cannot be written. Its failures name no
/// address.
void main() {
  late Directory root;
  late File target;

  setUp(() {
    root = Directory.systemTemp.createTempSync('tf-host-pkg-');
    target = File(p.join(root.path, 'download.tmp'));
  });
  tearDown(() => root.deleteSync(recursive: true));

  final archiveSha = 'ab' * 32;

  JoinHostPackageProblem problemOf(JoinHostPackageResult result) =>
      (result as JoinHostPackageFailed).problem;

  test('a cancel while the file is created contacts nothing', () async {
    final connects = <InternetAddress>[];
    final client = HostPackageClient(
      connect: (address, _) async {
        connects.add(address);
        throw const SocketException('refused');
      },
    );
    final cancellation = CancellationToken();
    // The download is opening its file when the cancel comes.
    final download = client.fetchFrom(
      lanHost,
      27250,
      archiveSha,
      target,
      allowDirect: false,
      cancellation: cancellation,
    );
    cancellation.cancel();
    expect(problemOf(await download), JoinHostPackageProblem.cancelled);
    expect(connects, isEmpty);
    expect(target.existsSync(), isFalse);
  });

  test('a cancelled token contacts nothing and writes nothing', () async {
    final connects = <InternetAddress>[];
    final client = HostPackageClient(
      connect: (address, _) async {
        connects.add(address);
        throw const SocketException('refused');
      },
    );
    final result = await client.fetchFrom(
      lanHost,
      27250,
      archiveSha,
      target,
      allowDirect: false,
      cancellation: CancellationToken()..cancel(),
    );
    expect(problemOf(result), JoinHostPackageProblem.cancelled);
    expect(connects, isEmpty);
    expect(target.existsSync(), isFalse);
  });

  group('relay-only', () {
    final beyond = [
      publicHost,
      publicHostV6,
      InternetAddress('100.64.0.1'),
      InternetAddress('::ffff:203.0.113.7'),
      InternetAddress('172.32.0.1'),
    ];

    test('never contacts an address beyond the local network', () async {
      var attempts = 0;
      await IOOverrides.runZoned(
        () async {
          for (final address in beyond) {
            final result = await HostPackageClient().fetchFrom(
              address,
              27250,
              archiveSha,
              target,
              allowDirect: false,
            );
            expect(
              problemOf(result),
              JoinHostPackageProblem.directNotAllowed,
              reason: address.address,
            );
            expect('$result', isNot(contains(address.address)));
          }
        },
        socketStartConnect: (host, port, {sourceAddress, sourcePort = 0}) {
          attempts++;
          throw const SocketException('no socket may open');
        },
        socketConnect: (host, port, {sourceAddress, sourcePort = 0, timeout}) {
          attempts++;
          throw const SocketException('no socket may open');
        },
      );
      expect(attempts, 0);
      expect(target.existsSync(), isFalse);
    });

    test(
      'contacts the local network, and beyond it when direct is allowed',
      () async {
        final connects = <InternetAddress>[];
        final client = HostPackageClient(
          connect: (address, _) async {
            connects.add(address);
            throw const SocketException('refused');
          },
        );
        final local = [
          '127.0.0.1',
          '10.0.0.1',
          '192.168.1.1',
          '169.254.1.1',
          '::1',
          'fe80::1',
          'fd00::1',
        ].map(InternetAddress.new).toList();
        for (final address in local) {
          await client.fetchFrom(
            address,
            27250,
            archiveSha,
            target,
            allowDirect: false,
          );
        }
        for (final address in beyond) {
          await client.fetchFrom(
            address,
            27250,
            archiveSha,
            target,
            allowDirect: true,
          );
        }
        expect(connects, [...local, ...beyond]);
      },
    );
  });

  test('socket failures are unreachable and name no address', () async {
    for (final code in [10061, 111, 10051, 101, null]) {
      final client = HostPackageClient(
        connect: (address, port) async => throw SocketException(
          'Connection failed to $address:$port',
          osError: code == null ? null : OSError('failed', code),
          address: address,
          port: port,
        ),
      );
      final result = await client.fetchFrom(
        lanHost,
        27250,
        archiveSha,
        target,
        allowDirect: false,
      );
      expect(problemOf(result), JoinHostPackageProblem.unreachable);
      for (final address in addressTexts) {
        expect('$result', isNot(contains(address)));
      }
      expect(target.existsSync(), isFalse);
    }
  });

  test('a file that cannot be written contacts nobody', () async {
    final connects = <InternetAddress>[];
    final client = HostPackageClient(
      connect: (address, _) async {
        connects.add(address);
        throw const SocketException('refused');
      },
    );
    final blocked = File(p.join(root.path, 'missing', 'folder', 'x.tmp'));
    final result = await client.fetchFrom(
      lanHost,
      27250,
      archiveSha,
      blocked,
      allowDirect: false,
    );
    expect(problemOf(result), JoinHostPackageProblem.notSaved);
    expect(connects, isEmpty);
  });

  test('an impossible port contacts nothing', () async {
    final connects = <int>[];
    final client = HostPackageClient(
      connect: (_, port) async {
        connects.add(port);
        throw const SocketException('refused');
      },
    );
    for (final port in [0, -1, 65536]) {
      final result = await client.fetchFrom(
        lanHost,
        port,
        archiveSha,
        target,
        allowDirect: false,
      );
      expect(problemOf(result), JoinHostPackageProblem.unreachable);
    }
    expect(connects, isEmpty);
  });
}
