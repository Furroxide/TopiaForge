import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'package:topiaforge/src/cli_join_receipt.dart';
import 'cli_join_fixture.dart';

void main() {
  late JoinFixture rig;
  late CliJoinReceipt receipt;
  setUp(() async {
    rig = JoinFixture();
    await rig.initialize();
    rig.intent = 'intent';
    receipt = CliJoinReceipt('intent', rig.plan.derivedProfile.id);
  });
  tearDown(() => rig.dispose());
  bool bind([LaunchActivity? activity]) => receipt.bind(
    JoinLaunchStarted(
      requestId: 'request-0',
      live: false,
      activity: activity ?? rig.base(),
    ),
  );
  test('fast matching records are held until the receipt binds', () {
    receipt.accept(rig.success());
    expect(receipt.current, isNull);
    expect(bind(), isTrue);
    expect(receipt.current!.launchOutcome!.status, 'succeeded');
  });
  final wrong = <String, LaunchActivity Function(JoinFixture)>{
    'request': (r) => r.base(request: 'old'),
    'profile': (r) => r.base(profile: 'old'),
    'revision': (r) => r.base(revision: 3),
    'install': (r) => r.base(installId: 'old'),
    'digest': (r) => r.base(digest: 'old'),
    'command': (r) => r.base(command: 'main-menu'),
    'pid': (r) => r.base(pid: 124),
    'start': (r) => r.base(seconds: 1),
    'executable': (r) => r.base(executable: 'fixture/other.exe'),
    'native-token': (r) => r.base(native: 'old'),
  };
  for (final entry in wrong.entries) {
    test('a foreign ${entry.key} cannot end this join', () {
      bind();
      receipt.accept(entry.value(rig).copyWith(processExited: true));
      expect(receipt.current!.processExited, isFalse);
    });
  }
  test('embedded and streamed records require this intent', () {
    bind(rig.success(forIntent: 'old'));
    expect(receipt.current!.launchOutcome, isNull);
    receipt.accept(rig.success(forIntent: 'old'));
    expect(receipt.current!.launchOutcome, isNull);
    receipt.accept(rig.success());
    expect(receipt.current!.launchOutcome, isNotNull);
  });
  test('an unverified process cannot bind', () {
    final item = rig.base();
    expect(
      bind(
        LaunchActivity(
          requestId: item.requestId,
          profileId: item.profileId,
          profileRevision: item.profileRevision,
          installIdentity: item.installIdentity,
          packageDigest: item.packageDigest,
          command: item.command,
        ),
      ),
      isFalse,
    );
  });
  test('a mismatched initial request cannot bind', () {
    expect(bind(rig.base(request: 'old')), isFalse);
  });
  test('exit is sticky even if late success arrives', () {
    bind();
    receipt.accept(rig.base().copyWith(processExited: true));
    receipt.accept(rig.success());
    expect(receipt.current!.processExited, isTrue);
  });
}
