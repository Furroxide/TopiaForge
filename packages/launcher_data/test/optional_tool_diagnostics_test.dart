import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

const _tools = ['Unity Hub', 'Unity Editor', 'Git'];

void main() {
  for (final tool in _tools) {
    for (final failure in ['timeout', 'spawn']) {
      for (final doctor in [false, true]) {
        if (doctor && tool == 'Git') continue;
        test('${doctor ? 'doctor' : 'environment'} preserves other checks '
            'when $tool discovery fails ($failure)', () async {
          final fixture = _Fixture({tool}, failure);
          addTearDown(fixture.dispose);

          if (doctor) {
            final report = await fixture.repository.runDoctor(
              projectPath: fixture.root.path,
            );
            expect(report.ok, isFalse);
            expect(
              report.issues.where((issue) => issue.isBlocking).single.message,
              contains('10.0.301'),
            );
            final warning = report.issues.singleWhere(
              (issue) =>
                  issue.severity == IssueSeverity.warning &&
                  issue.message.startsWith('$tool availability is unknown'),
            );
            expect(warning.message, contains(_failureDetail(failure)));
            expect(warning.message, isNot(contains('private')));
            expect(
              report.messages.join(' '),
              isNot(contains('$tool not detected')),
            );
            if (tool != 'Unity Editor') {
              expect(report.unityEditorPath, fixture.editorPath);
            }
          } else {
            final report = await fixture.repository.checkEnvironment();
            expect(report.developerReady, isFalse);
            expect(report.blockers.single.name, '.NET SDK');
            expect(report.blockers.single.detail, contains('10.0.301'));
            final warning = report.checks.singleWhere(
              (check) => check.name == tool,
            );
            expect(warning.status, ToolStatus.warning);
            expect(warning.detail, contains('availability is unknown'));
            expect(warning.detail, contains(_failureDetail(failure)));
            expect(warning.detail, isNot(contains('private')));
            expect(warning.detail, isNot(contains('not detected')));
            expect(warning.remediation, isNot(contains('Install')));
            if (tool != 'Unity Editor') {
              final editor = report.checks.singleWhere(
                (check) => check.name == 'Unity Editor',
              );
              expect(editor.status, ToolStatus.ok);
              expect(editor.detail, fixture.editorPath);
              expect(report.customWorldUnityReady, isTrue);
            }
            if (tool != 'Git') {
              final git = report.checks.singleWhere(
                (check) => check.name == 'Git',
              );
              expect(git.status, ToolStatus.ok);
              expect(git.detail, fixture.gitPath);
            }
          }
          expect(
            fixture.probes,
            unorderedEquals(doctor ? _tools.take(2) : _tools),
          );
        });
      }
    }
  }

  for (final doctor in [false, true]) {
    test(
      '${doctor ? 'doctor' : 'environment'} accumulates all optional failures',
      () async {
        final fixture = _Fixture(_tools.toSet(), 'timeout');
        addTearDown(fixture.dispose);
        if (doctor) {
          final report = await fixture.repository.runDoctor(
            projectPath: fixture.root.path,
          );
          expect(
            report.issues.where((issue) => issue.isBlocking),
            hasLength(1),
          );
          expect(
            report.issues.where(
              (issue) => issue.message.contains('availability is unknown'),
            ),
            hasLength(2),
          );
          expect(report.unityHubPath, isEmpty);
          expect(report.unityEditorPath, isEmpty);
        } else {
          final report = await fixture.repository.checkEnvironment();
          expect(report.blockers, hasLength(1));
          expect(
            report.checks.where(
              (check) => check.detail.contains('availability is unknown'),
            ),
            hasLength(3),
          );
        }
        expect(
          fixture.probes,
          unorderedEquals(doctor ? _tools.take(2) : _tools),
        );
      },
    );
  }

  test(
    'optional failures do not make a verified required SDK blocking',
    () async {
      final fixture = _Fixture(_tools.toSet(), 'spawn', sdkAvailable: true);
      addTearDown(fixture.dispose);
      final environment = await fixture.repository.checkEnvironment();
      expect(environment.developerReady, isTrue);
      expect(environment.blockers, isEmpty);
      final doctor = await fixture.repository.runDoctor(
        projectPath: fixture.root.path,
      );
      expect(doctor.ok, isTrue);
      expect(doctor.issues.where((issue) => issue.isBlocking), isEmpty);
    },
  );

  test(
    'a completed empty lookup still reports the tool as not detected',
    () async {
      final fixture = _Fixture({}, 'timeout', toolsAvailable: false);
      addTearDown(fixture.dispose);
      final environment = await fixture.repository.checkEnvironment();
      final editor = environment.checks.singleWhere(
        (check) => check.name == 'Unity Editor',
      );
      expect(editor.status, ToolStatus.missing);
      expect(editor.detail, contains('not detected'));
      final git = environment.checks.singleWhere(
        (check) => check.name == 'Git',
      );
      expect(git.detail, 'Not found (recommended).');
      final doctor = await fixture.repository.runDoctor(
        projectPath: fixture.root.path,
      );
      expect(doctor.messages, contains('Unity Hub not detected.'));
      expect(doctor.messages, contains('Unity Editor not detected.'));
      expect(
        doctor.issues.where((issue) => issue.message.contains('unknown')),
        isEmpty,
      );
    },
  );

  test('diagnostics do not hide a programming error in a tool probe', () async {
    final root = Directory.systemTemp.createTempSync('optional-tool-error-');
    addTearDown(() => root.deleteSync(recursive: true));
    final failure = StateError('programming failure');
    final repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
      dotnetSdkResolver: (_) async => const DotnetSdkSelection(
        executable: 'verified-dotnet',
        version: '10.0.301',
        requiredVersion: '10.0.301',
      ),
      executableLookup: (_, {configuredPath}) async => throw failure,
      unityEditorScanner: () async => const [],
    );
    await expectLater(repository.checkEnvironment(), throwsA(same(failure)));
    await expectLater(
      repository.runDoctor(projectPath: root.path),
      throwsA(same(failure)),
    );
  });

  test(
    'required editor discovery still propagates its process failure',
    () async {
      final fixture = _Fixture({'Unity Editor'}, 'timeout');
      addTearDown(fixture.dispose);
      await expectLater(
        fixture.repository.listUnityEditors(),
        throwsA(isA<BoundedProcessException>()),
      );
    },
  );
}

String _failureDetail(String failure) =>
    failure == 'timeout' ? 'timed out' : 'could not be started';

class _Fixture {
  _Fixture(
    this.failingTools,
    this.failure, {
    bool sdkAvailable = false,
    bool toolsAvailable = true,
  }) {
    repository = LocalDeveloperRepository(
      dataRoot: p.join(root.path, 'data'),
      repositoryRoot: root.path,
      dotnetSdkResolver: (_) async {
        if (!sdkAvailable) {
          throw StateError('Required .NET SDK 10.0.301 was not found.');
        }
        return const DotnetSdkSelection(
          executable: 'verified-dotnet',
          version: '10.0.301',
          requiredVersion: '10.0.301',
        );
      },
      executableLookup: (executable, {configuredPath}) async {
        final tool = executable == 'git' ? 'Git' : 'Unity Hub';
        probe(tool);
        return toolsAvailable ? (tool == 'Git' ? gitPath : hubPath) : '';
      },
      unityEditorScanner: () async {
        probe('Unity Editor');
        return toolsAvailable
            ? [
                UnityEditor(
                  version:
                      RobotopiaGameUnityCompatibility.requiredEditorVersion,
                  path: editorPath,
                ),
              ]
            : [];
      },
    );
  }

  final Directory root = Directory.systemTemp.createTempSync('optional-tools-');
  final Set<String> failingTools;
  final String failure;
  final List<String> probes = [];
  late final LocalDeveloperRepository repository;
  String get editorPath => p.join(root.path, 'verified-editor');
  String get gitPath => p.join(root.path, 'git');
  String get hubPath => p.join(root.path, 'hub');

  void probe(String tool) {
    probes.add(tool);
    if (!failingTools.contains(tool)) return;
    if (failure == 'timeout') {
      throw const BoundedProcessException(
        failure: BoundedProcessFailure.timeout,
        stdout: 'private child output',
        stderr: 'private child error',
      );
    }
    throw const ProcessException(
      'private executable',
      [],
      'private spawn error',
    );
  }

  void dispose() => root.deleteSync(recursive: true);
}
