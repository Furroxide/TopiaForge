import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:test/test.dart';

import 'acceptance_isolation_fixture.dart';

void main() {
  for (final fault in ['none', 'server', 'signature', 'framing', 'isolation']) {
    test(
      'acceptance descriptor uses a real loopback exchange: $fault',
      () async {
        final fixture = IsolationFixture();
        addTearDown(fixture.dispose);
        final context = fixture.admit();
        final cases =
            jsonDecode(
                  File(
                    '../../tests/fixtures/multiplayer/descriptor/cases.json',
                  ).readAsStringSync(),
                )
                as Map;
        final valid = (cases['valid'] as List).first as Map;
        var body = utf8.encode(valid['signedBytes'] as String);
        if (fault == 'signature') {
          final document = jsonDecode(utf8.decode(body)) as Map;
          final signature = document['signature'] as String;
          document['signature'] =
              '${signature[0] == 'A' ? 'B' : 'A'}${signature.substring(1)}';
          body = utf8.encode(jsonEncode(document));
        }
        final listener = await ServerSocket.bind(
          InternetAddress.loopbackIPv4,
          0,
        );
        final sockets = <Socket>[];
        var requests = 0;
        final subscription = listener.listen((socket) {
          sockets.add(socket);
          var answered = false;
          final requestBytes = <int>[];
          socket.listen((bytes) {
            if (answered) return;
            requestBytes.addAll(bytes);
            if (!requestBytes.contains(10)) return;
            answered = true;
            requests++;
            expect(utf8.decode(requestBytes), 'TFD1 descriptor\n');
            if (fault == 'isolation') {
              fixture.change(
                (raw) => raw['reviewerEvidence'] = 'Changed provisioning.',
              );
            }
            socket.add(
              utf8.encode(
                fault == 'framing'
                    ? 'TFD1 200 999999999\n'
                    : 'TFD1 200 ${body.length}\n',
              ),
            );
            socket.add(body);
          });
        });
        try {
          final pending = verifyAcceptanceHostTarget(
            context,
            port: listener.port,
            serverId: fault == 'server'
                ? 'f' * 64
                : valid['serverId'] as String,
          );
          if (fault == 'none') {
            final target = await pending;
            expect(target.serverId, valid['serverId']);
            expect(target.descriptorSha256, valid['descriptorSha256']);
            expect(target.packageDigest, valid['packageDigest']);
            expect(target.host, '127.0.0.1');
            expect(target.port, listener.port);
          } else {
            await expectLater(pending, throwsStateError);
          }
          expect(requests, 1);
        } finally {
          for (final socket in sockets) {
            socket.destroy();
          }
          await subscription.cancel();
          await listener.close();
        }
      },
    );
  }
}
