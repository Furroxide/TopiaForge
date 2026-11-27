import 'package:launcher_domain/launcher_domain.dart';

String cliJoinStage(JoinStage stage) => switch (stage) {
  JoinStage.resolving || JoinStage.fetchingDescriptor => 'TFJOIN110',
  JoinStage.checking => 'TFJOIN120',
  JoinStage.awaitingConsent => 'TFJOIN130',
  JoinStage.downloading => 'TFJOIN140',
  JoinStage.installing => 'TFJOIN150',
  JoinStage.preparingProfile => 'TFJOIN160',
  JoinStage.launching => 'TFJOIN170',
  JoinStage.connecting ||
  JoinStage.synchronizing ||
  JoinStage.inSession ||
  JoinStage.ended => 'TFJOIN180',
};

String cliJoinFailure(JoinFailure failure, {required bool started}) {
  final text = JoinFailureText.of(failure, startedGame: started);
  // Never repeat provider details: they can include private addresses or paths.
  return [
    '${text.sentence} ${text.nextStep ?? 'Review the server and Multiplayer settings, then try again.'}',
    for (final blocker in failure.blockers)
      'Mod ${blocker.modId.isEmpty ? '(session)' : withoutAddresses(blocker.modId)}: '
          '${withoutAddresses(blocker.message)}',
  ].join('\n');
}
