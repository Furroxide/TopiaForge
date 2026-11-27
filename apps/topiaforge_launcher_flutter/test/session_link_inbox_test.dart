import 'package:flutter_test/flutter_test.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:topiaforge_launcher_flutter/src/session_link_inbox.dart';

void main() {
  const first = SessionLinkArrival(
    'topiaforge://join/ABCDE12345',
    source: SessionLinkSource.commandLine,
  );
  const second = SessionLinkArrival(
    'topiaforge://join/ZZZZZ99999',
    source: SessionLinkSource.forwarded,
  );

  test('holds only the newest link until something listens', () async {
    final inbox = SessionLinkInbox()
      ..deliver(first)
      ..deliver(second);
    final arrivals = <SessionLinkArrival>[];

    final subscription = inbox.arrivals.listen(arrivals.add);
    await pumpEventQueue();

    expect(arrivals, [second]);
    await subscription.cancel();
  });

  test('passes every link straight through once listened to', () async {
    final inbox = SessionLinkInbox();
    final arrivals = <SessionLinkArrival>[];
    final subscription = inbox.arrivals.listen(arrivals.add);

    inbox
      ..deliver(first)
      ..deliver(second);
    await pumpEventQueue();

    expect(arrivals, [first, second]);
    await subscription.cancel();
  });

  test('delivers nothing after it closes', () async {
    final inbox = SessionLinkInbox();
    await inbox.close();
    inbox.deliver(first);
  });

  test('an arrival is read by the domain and never prints its text', () {
    const direct = SessionLinkArrival(
      'topiaforge://join/direct/'
      'c5f39060271ebd2f22d9bf155a26bf459274e31b795145fd9d862dc206d98bb3/'
      '192.168.1.20/27250',
      source: SessionLinkSource.forwarded,
    );
    expect(direct.read().link?.kind, SessionLinkKind.direct);
    expect(direct.toString(), isNot(contains('192.168')));
    expect(
      const SessionLinkArrival.refused(
        SessionLinkRefusal.tooLong,
        source: SessionLinkSource.forwarded,
      ).read().refusal,
      SessionLinkRefusal.tooLong,
    );
  });
}
