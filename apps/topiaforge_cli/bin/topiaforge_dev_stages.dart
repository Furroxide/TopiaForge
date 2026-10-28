part of 'topiaforge.dart';

/// One stage of the `topiaforge dev` loop and its stable diagnostic code.
///
/// Split out of `topiaforge_dev_commands.dart`, with the stage runner and its
/// failure reporting, to stay inside the 500-line cap in AGENTS.md.
final class _DevStage {
  const _DevStage({
    required this.code,
    required this.label,
    required this.action,
    required this.remediation,
  });

  final String code;
  final String label;
  final String action;
  final String remediation;

  // The stages in run order. docs/Diagnostics.md documents every code.
  static const restore = _DevStage(
    code: 'TFDEV100',
    label: 'restore',
    action: 'Restore the exact SDK and dependency locks.',
    remediation:
        'Run `topiaforge restore --project <path>` and resolve every reported lock, feed, or SDK issue.',
  );

  static const toolchain = _DevStage(
    code: 'TFDEV105',
    label: 'toolchain',
    action: 'Activate the project-pinned .NET SDK.',
    remediation:
        'Install the exact SDK pinned by global.json, then rerun `topiaforge restore`.',
  );

  static const build = _DevStage(
    code: 'TFDEV110',
    label: 'build',
    action: 'Build the mod against the locked SDK.',
    remediation:
        'Fix the compiler diagnostics, then run `dotnet build --no-restore` from the mod project.',
  );

  static const test = _DevStage(
    code: 'TFDEV120',
    label: 'test',
    action: 'Run every generated or authored test project.',
    remediation:
        'Fix the failing test or test-host error, then run `dotnet test --no-restore`.',
  );

  static const contract = _DevStage(
    code: 'TFDEV115',
    label: 'contract',
    action: 'Synchronize the generated multiplayer contract lock.',
    remediation: 'Fix the multiplayer compiler diagnostics and rerun dev.',
  );

  static const overlays = _DevStage(
    code: 'TFDEV152',
    label: 'peers',
    action: 'Prepare private game overlays and launcher roots.',
    remediation:
        'Repair the game and runtime, and check development directory permissions.',
  );

  static const joinPeers = _DevStage(
    code: 'TFDEV165',
    label: 'join',
    action: 'Join the development peers to their local host.',
    remediation:
        'Check the peer logs and package selection, then restart the dev loop.',
  );

  static const pack = _DevStage(
    code: 'TFDEV130',
    label: 'pack',
    action: 'Create the installable .topiaforgemod archive.',
    remediation:
        'Check the manifest, entry assembly, package files, and Release build output; then run `topiaforge pack`.',
  );

  static const validate = _DevStage(
    code: 'TFDEV140',
    label: 'validate',
    action: 'Validate the exact archive that will be installed.',
    remediation:
        'Run `topiaforge check package <archive>` and correct every blocking manifest or archive issue.',
  );

  static const install = _DevStage(
    code: 'TFDEV150',
    label: 'install',
    action: 'Install the validated package into Robotopia.',
    remediation:
        'Pass `--game-dir <Robotopia folder>` or set ROBOTOPIA_GAME_DIR, then run `topiaforge doctor`.',
  );

  static const launch = _DevStage(
    code: 'TFDEV160',
    label: 'launch',
    action: 'Launch Robotopia with the selected profile.',
    remediation:
        'Run `topiaforge doctor`, repair the runtime in the launcher, and retry with `--no-tail` while diagnosing.',
  );

  static const tail = _DevStage(
    code: 'TFDEV170',
    label: 'tail',
    action: 'Tail launcher and manager logs with source labels.',
    remediation:
        'Inspect BepInEx/TopiaForge/logs and the launcher diagnostics bundle, or rerun with `--no-tail`.',
  );
}

/// Runs each `topiaforge dev` stage with progress output and reports a failed
/// stage by its diagnostic code.
extension _TopiaForgeDevStages on _TopiaForgeCli {
  Future<T> _runDevStage<T>(
    _DevStage stage,
    Future<T> Function() action,
  ) async {
    stdout.writeln('[${stage.label}] ${stage.action}');
    final elapsed = Stopwatch()..start();
    try {
      final value = await action();
      stdout.writeln('[${stage.label}] complete.');
      stdout.writeln('${stage.code}: ${elapsed.elapsedMilliseconds} ms.');
      return value;
    } on Object catch (error) {
      throw _DevStageFailure(stage, error);
    }
  }

  /// Prints the failed stage's code, cause, remediation, and docs link, and
  /// returns the exit code for `topiaforge dev`.
  int _reportDevStageFailure(_DevStageFailure failure) {
    stderr.writeln('${failure.stage.code}: ${failure.stage.label} failed.');
    stderr.writeln('Cause: ${_devCause(failure.cause)}');
    stderr.writeln('Remediation: ${failure.stage.remediation}');
    stderr.writeln(
      'Docs: https://docs.topiaforge.dev/diagnostics/${failure.stage.code}',
    );
    return failure.cause is _DevLaunchExit
        ? (failure.cause as _DevLaunchExit).code
        : 1;
  }
}

final class _DevStageFailure implements Exception {
  const _DevStageFailure(this.stage, this.cause);

  final _DevStage stage;
  final Object cause;
}

String _devCause(Object error) {
  final text = error is StateError ? error.message : error.toString();
  return _boundedDevText(text.replaceAll(RegExp(r'\s+'), ' ').trim());
}

String _boundedDevText(String text) {
  const limit = 4000;
  if (text.length <= limit) return text;
  return '…${text.substring(text.length - limit)}';
}

final class _DevLaunchExit implements Exception {
  const _DevLaunchExit(this.code);
  final int code;
  @override
  String toString() => code == 3
      ? 'Session startup is unconfirmed.'
      : code == 130
      ? 'Stopped waiting for session startup.'
      : 'Session startup failed.';
}
