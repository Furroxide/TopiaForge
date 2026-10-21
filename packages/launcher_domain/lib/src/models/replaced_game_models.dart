part of '../models.dart';

/// How the running game a restart replaced ended.
enum ReplacedGameExit {
  /// It quit when the launcher asked, as its own Quit button would.
  quit,

  /// It did not quit when asked, so the launcher stopped it.
  stopped,

  /// It had already exited when the launcher came to ask it to quit, or to
  /// stop it.
  exited,
}

/// How the running game a launch replaced ended, and whether that leaves the
/// game's next start in safe mode.
final class ReplacedGame {
  const ReplacedGame(this.exit, {this.safeMode = false});

  final ReplacedGameExit exit;

  /// Whether the game's next start opens in safe mode, with every mod off,
  /// because the game it replaced recorded no clean exit: it was stopped, or
  /// it quit before its mods finished unloading, or it crashed. A quit that
  /// recorded its clean exit leaves the next start loading its mods.
  final bool safeMode;

  /// What happened, in the player's words, and what to do next. [started]
  /// says whether the launch that replaced the game started its own process,
  /// which is then the start that opens in safe mode.
  String message({bool started = true}) {
    final what = switch (exit) {
      ReplacedGameExit.quit when safeMode =>
        'The running game quit when asked, but did not close cleanly.',
      ReplacedGameExit.quit => 'The running game quit when asked.',
      ReplacedGameExit.stopped =>
        'The running game did not quit when asked, so it was stopped.',
      ReplacedGameExit.exited when safeMode =>
        'The running game had already exited without closing cleanly.',
      ReplacedGameExit.exited => 'The running game had already exited.',
    };
    if (!safeMode) return what;
    return started
        ? '$what This start opens in safe mode, with every mod off; restart '
              'it from the launcher to load them.'
        : '$what Its next start opens in safe mode, with every mod off.';
  }
}
