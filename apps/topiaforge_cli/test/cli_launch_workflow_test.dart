import 'dart:async';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/cli_launch_options.dart';
import 'package:topiaforge/src/cli_launch_workflow.dart';

void main() {
  late _Repository repository;
  late List<String> messages;
  setUp(() {
    repository = _Repository();
    messages = [];
  });
  tearDown(() => repository.controller.close());
  Future<int> launch({
    CliLaunchOptions? options,
    Future<void>? cancelled,
    bool restart = false,
  }) => runCliLaunch(
    repository: repository,
    install: repository.install,
    profile: repository.profile,
    options:
        options ??
        const CliLaunchOptions(waitTimeout: Duration(milliseconds: 15)),
    write: messages.add,
    cancelled: cancelled,
    restart: restart,
  );

  test(
    'process start without acknowledgement is unconfirmed, not success',
    () async {
      expect(await launch(), 3);
      expect(messages.join(' '), contains('unconfirmed'));
      expect(repository.controller.hasListener, isFalse);
    },
  );
  test(
    'subscribes before spawn and accepts fast correlated acknowledgement',
    () async {
      repository.onLaunch = () =>
          repository.controller.add(repository.succeeded());
      expect(await launch(), 0);
      expect(messages.join(' '), contains('Main menu confirmed'));
    },
  );
  test('ignores foreign receipts and returns runtime failure', () async {
    repository.onLaunch = () {
      repository.controller.add(repository.succeeded(requestId: 'foreign'));
      repository.controller.add(repository.failed());
    };
    expect(await launch(), 1);
    expect(messages.join(' '), contains('missing native scene'));
  });
  test(
    'cancellation before preflight completion prevents process creation',
    () async {
      expect(await launch(cancelled: Future<void>.value()), 130);
      expect(repository.launches, 0);
    },
  );
  test('preview refusal prevents process creation', () async {
    repository.blocked = true;
    expect(await launch(), 1);
    expect(repository.launches, 0);
    expect(messages.join(' '), contains('Repair selection'));
  });
  test(
    'wrong profile revision and changed package digest cannot acknowledge',
    () async {
      repository.onLaunch = () {
        repository.controller.add(repository.succeeded(revision: 99));
        repository.controller.add(repository.succeeded(digest: 'changed'));
      };
      expect(await launch(), 3);
    },
  );
  test(
    'explicit one-shot main menu preserves saved target selection',
    () async {
      final saved = repository.profile.launchSelection;
      expect(
        await launch(
          options: const CliLaunchOptions(
            selectionOverride: LaunchSelection.mainMenu(),
            waitForAcknowledgement: false,
          ),
        ),
        0,
      );
      expect(repository.selectionReceived, const LaunchSelection.mainMenu());
      expect(repository.profile.launchSelection, saved);
      expect(messages.join(' '), contains('unconfirmed'));
    },
  );
  test('caller cancellation stops waiting and releases subscription', () async {
    final cancelled = Completer<void>();
    repository.onLaunch = cancelled.complete;
    expect(await launch(cancelled: cancelled.future), 130);
    expect(repository.controller.hasListener, isFalse);
  });
  test(
    'receipt latest activity retains acknowledgement before late subscription',
    () async {
      repository.latest = repository.succeeded();
      expect(await launch(), 0);
      expect(messages.join(' '), contains('Main menu confirmed'));
    },
  );
  test('a restart says the game it replaced quit when asked', () async {
    repository.replaced = const ReplacedGame(ReplacedGameExit.quit);
    repository.latest = repository.succeeded();
    expect(await launch(restart: true), 0);
    expect(repository.restarts, 1);
    expect(messages.first, 'The running game quit when asked.');
    expect(messages[1], startsWith('Game process started'));
  });
  for (final exit in ReplacedGameExit.values) {
    test('a restart whose replaced game ${exit.name} into safe mode says this '
        'start is in safe mode, and what to do', () async {
      repository.replaced = ReplacedGame(exit, safeMode: true);
      repository.latest = repository.succeeded();
      expect(await launch(restart: true), 0);
      expect(messages.first, repository.replaced!.message());
      expect(messages.first, contains('This start opens in safe mode'));
      expect(messages.first, contains('restart it from the launcher'));
    });
  }
  test('a failed restart says the next start opens in safe mode', () async {
    repository.replaced = const ReplacedGame(
      ReplacedGameExit.stopped,
      safeMode: true,
    );
    repository.replacementStarts = false;
    expect(await launch(restart: true), 1);
    expect(
      messages.single,
      startsWith(
        'The running game did not quit when asked, so it was stopped. Its '
        'next start opens in safe mode, with every mod off.',
      ),
    );
    expect(messages.single, isNot(contains('This start')));
  });
  test('a launch that replaced no game says nothing about one', () async {
    repository.latest = repository.succeeded();
    expect(await launch(), 0);
    expect(messages.first, startsWith('Game process started'));
  });
}

class _Repository implements LauncherRepository {
  final controller = StreamController<LaunchActivity>.broadcast(sync: true);
  final profile = LauncherProfile.defaultProfile().copyWith(
    revision: 2,
    launchSelection: LaunchSelection.target(
      LaunchRequest(targetId: 'test.target'),
    ),
  );
  final install = const GameInstall(
    path: 'fixture',
    executablePath: 'fixture/game.exe',
    bepInExStatus: ComponentState.ready,
    loaderStatus: ComponentState.ready,
  );
  bool blocked = false;
  int launches = 0;
  void Function()? onLaunch;
  LaunchSelection? selectionReceived;
  LaunchActivity? latest;
  ReplacedGame? replaced;
  bool replacementStarts = true;
  int restarts = 0;
  LaunchActivity base({
    String requestId = 'request-one',
    int revision = 2,
    String digest = 'digest',
  }) => LaunchActivity(
    requestId: requestId,
    profileId: profile.id,
    profileRevision: revision,
    installIdentity: 'install',
    packageDigest: digest,
    command: 'main-menu',
    unconfirmed: true,
  );
  LaunchActivity succeeded({
    String requestId = 'request-one',
    int revision = 2,
    String digest = 'digest',
  }) => base(requestId: requestId, revision: revision, digest: digest)
      .applyOutcome(
        LaunchOutcome(
          kind: 'launch',
          requestId: requestId,
          command: 'main-menu',
          sequence: 1,
          status: 'succeeded',
          phase: 'idle',
        ),
      );
  LaunchActivity failed() => base().applyOutcome(
    LaunchOutcome(
      kind: 'launch',
      requestId: 'request-one',
      command: 'main-menu',
      sequence: 1,
      status: 'failed',
      phase: 'idle',
      error: LaunchOperationError(
        code: 'notFound',
        message: 'missing native scene',
      ),
    ),
  );
  @override
  Stream<LaunchActivity> get launchActivities => controller.stream;
  @override
  Future<LaunchPreview> previewLaunch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async => LaunchPreview(
    profileId: profile.id,
    profileRevision: profile.revision,
    installIdentity: 'install',
    packageDigest: 'digest',
    requestedSelection: selectionOverride ?? profile.launchSelection,
    effectiveSelection: const LaunchSelection.mainMenu(),
    issues: blocked
        ? const [
            LauncherIssue(
              severity: IssueSeverity.error,
              message: 'Repair selection',
            ),
          ]
        : const [],
  );
  @override
  Future<LaunchResult> launch(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async {
    launches++;
    selectionReceived = selectionOverride;
    onLaunch?.call();
    return LaunchResult(
      started: true,
      message: 'Process created.',
      requestId: 'request-one',
      latestActivity: latest ?? base(),
    );
  }

  @override
  Future<LaunchResult> restart(
    GameInstall install,
    LauncherProfile profile, {
    LaunchSelection? selectionOverride,
  }) async {
    restarts++;
    if (!replacementStarts) {
      return const LaunchResult(
        started: false,
        message: 'TopiaForge could not be started.',
      ).replacing(replaced);
    }
    return (await launch(
      install,
      profile,
      selectionOverride: selectionOverride,
    )).replacing(replaced);
  }

  @override
  dynamic noSuchMethod(Invocation invocation) => super.noSuchMethod(invocation);
}
