import 'package:launcher_data/src/launch_process_control.dart';
import 'package:test/test.dart';

void main() {
  const firstBoot = 'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';
  const secondBoot = 'bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
  late LinuxProcessStartEpochCache cache;
  setUp(() => cache = LinuxProcessStartEpochCache());
  DateTime read(
    int bootSeconds, {
    String boot = firstBoot,
    int ticks = 1234,
    int hertz = 100,
  }) => cache.startTimeUtc(
    bootId: boot,
    bootSeconds: bootSeconds,
    ticks: ticks,
    hertz: hertz,
  );

  for (final correction in [-60, 60]) {
    test(
      'same native generation keeps UTC after $correction second clock adjustment',
      () {
        final first = read(1700000000);
        expect(read(1700000000 + correction), first);
        expect(first.isUtc, isTrue);
        expect(first.microsecondsSinceEpoch, 1700000012340000);
      },
    );
  }
  test(
    'new processes use the same captured boot epoch after clock correction',
    () {
      final first = read(1700000000, ticks: 100);
      final later = read(1700000060, ticks: 200);
      expect(later.difference(first), const Duration(seconds: 1));
    },
  );
  test('different boot IDs retain independent UTC epochs', () {
    final first = read(1700000000);
    final next = read(1800000000, boot: secondBoot);
    expect(next.difference(first), const Duration(seconds: 100000000));
    expect(read(1700000030), first);
  });
}
