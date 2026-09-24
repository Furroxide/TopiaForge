import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/cli_launch_options.dart';

void main() {
  for (final length in [65, 96]) {
    test('CLI accepts $length character declaration IDs', () {
      final id = 'test.${'a' * (length - 5)}';
      final parsed = CliLaunchOptions.parse([
        '--target',
        id,
        '--world',
        id,
        '--transition',
        ModTransitions.sceneReplacement,
      ]);
      expect(parsed.selectionOverride!.request!.targetId, id);
      expect(parsed.selectionOverride!.request!.worldOverride, id);
    });
  }
  for (final arguments in [
    ['--target', 'test.${'a' * 92}'],
    ['--target', 'test.mode', '--world', 'test.${'a' * 92}'],
    ['--target', 'test.mode', '--transition', 'single'],
    ['--target', 'test.mode', '--target', 'test.other'],
    ['--wait-seconds', '0'],
    ['--wait-seconds', '301'],
    ['--wait-seconds', '1.5'],
    ['--wait-seconds', '1', '--no-wait'],
    ['--main-menu', '--main-menu'],
    ['--profile', ''],
  ]) {
    test('CLI refuses invalid launch option ${arguments.join(' ')}', () {
      expect(() => CliLaunchOptions.parse(arguments), throwsFormatException);
    });
  }
  test(
    'main-menu override remains explicit and does not invent target fields',
    () {
      final parsed = CliLaunchOptions.parse([
        '--main-menu',
        '--profile',
        'one',
      ]);
      expect(parsed.selectionOverride, const LaunchSelection.mainMenu());
      expect(parsed.profileId, 'one');
      expect(parsed.waitForAcknowledgement, isTrue);
    },
  );
}
