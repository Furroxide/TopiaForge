import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  group('how a replaced game ended, in the player\'s words', () {
    for (final exit in ReplacedGameExit.values) {
      for (final safeMode in [false, true]) {
        for (final started in [true, false]) {
          test('${exit.name}, safe mode $safeMode, started $started', () {
            final message = ReplacedGame(
              exit,
              safeMode: safeMode,
            ).message(started: started);
            expect(
              message,
              startsWith(switch (exit) {
                ReplacedGameExit.quit => 'The running game quit when asked',
                ReplacedGameExit.stopped =>
                  'The running game did not quit when asked, so it was '
                      'stopped.',
                ReplacedGameExit.exited =>
                  'The running game had already exited',
              }),
            );
            // Safe mode is said exactly when it is so, and never for a start
            // that did not happen.
            expect(message.contains('safe mode'), safeMode);
            expect(message.contains('This start'), safeMode && started);
            expect(message.contains('Its next start'), safeMode && !started);
            expect(
              message.contains('restart it from the launcher'),
              safeMode && started,
            );
            expect(message, endsWith('.'));
          });
        }
      }
    }

    test('a clean quit is said plainly', () {
      expect(
        const ReplacedGame(ReplacedGameExit.quit).message(),
        'The running game quit when asked.',
      );
      expect(
        const ReplacedGame(ReplacedGameExit.quit, safeMode: true).message(),
        'The running game quit when asked, but did not close cleanly. This '
        'start opens in safe mode, with every mod off; restart it from the '
        'launcher to load them.',
      );
    });

    test('a launch result leads with it, and keeps everything else', () {
      final process = LaunchProcessIdentity(
        pid: 7,
        startTimeUtc: DateTime.utc(2026),
        executablePath: 'game.exe',
        nativeStartToken: 'token',
      );
      final original = LaunchResult(
        started: false,
        message: 'TopiaForge could not be started.',
        processId: 7,
        requestId: 'request-one',
        process: process,
      );
      const replaced = ReplacedGame(ReplacedGameExit.stopped, safeMode: true);
      final result = original.replacing(replaced);
      expect(result.replacedGame, same(replaced));
      expect(
        result.message,
        '${replaced.message(started: false)} TopiaForge could not be started.',
      );
      expect(result.started, isFalse);
      expect(result.processId, 7);
      expect(result.requestId, 'request-one');
      expect(result.process, same(process));
      expect(original.replacing(null).message, original.message);
      expect(original.replacing(null).replacedGame, isNull);
    });
  });
}
