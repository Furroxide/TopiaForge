import 'dart:convert';
import 'dart:io';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:path/path.dart' as p;
import 'package:test/test.dart';

void main() {
  late Directory root;
  late Directory dataRoot;
  late Directory repositoryRoot;
  late String releaseEditorPath;
  late String newestEditorPath;

  setUp(() {
    root = Directory.systemTemp.createTempSync('topiaforge-unity-environment-');
    dataRoot = Directory(p.join(root.path, 'data'))..createSync();
    repositoryRoot = Directory(p.join(root.path, 'repo'))..createSync();
    releaseEditorPath = p.join(root.path, '6000.0.23f1', 'Unity');
    newestEditorPath = p.join(root.path, '6000.2.10f1', 'Unity');
  });

  tearDown(() {
    if (root.existsSync()) {
      root.deleteSync(recursive: true);
    }
  });

  LocalDeveloperRepository createRepository({
    List<UnityEditor> editors = const [],
  }) => LocalDeveloperRepository(
    dataRoot: dataRoot.path,
    repositoryRoot: repositoryRoot.path,
    unityEditorScanner: () async => editors,
  );

  List<UnityEditor> installedEditors() => [
    UnityEditor(version: '6000.2.10f1', path: newestEditorPath),
    UnityEditor(version: '6000.0.23f1', path: releaseEditorPath),
  ];

  test('repository-mode doctor reports the exact release editor', () async {
    final report = await createRepository(
      editors: installedEditors(),
    ).runDoctor(projectPath: repositoryRoot.path);

    expect(report.hasProject, isFalse);
    expect(report.unityEditorPath, releaseEditorPath);
    expect(report.messages, contains('Unity Editor: $releaseEditorPath'));
    expect(report.messages, isNot(contains('Unity Editor: $newestEditorPath')));
  });

  test('environment check displays the compatible editor path', () async {
    final report = await createRepository(
      editors: installedEditors(),
    ).checkEnvironment();
    final unity = report.checks.singleWhere(
      (check) => check.name == 'Unity Editor',
    );

    expect(unity.status, ToolStatus.ok);
    expect(unity.detail, releaseEditorPath);
  });

  test('environment check warns instead of accepting a newer editor', () async {
    final report = await createRepository(
      editors: [UnityEditor(version: '6000.2.10f1', path: newestEditorPath)],
    ).checkEnvironment();
    final unity = report.checks.singleWhere(
      (check) => check.name == 'Unity Editor',
    );

    expect(unity.status, ToolStatus.warning);
    expect(unity.detail, contains('6000.2.10f1'));
    expect(
      unity.detail,
      contains(RobotopiaGameUnityCompatibility.requiredEditorDisplay),
    );
  });

  test('doctor honors an explicit project editor pin', () async {
    const configuredVersion = '6000.0.31f1';
    final configuredPath = p.join(root.path, configuredVersion, 'Unity');
    final project = Directory(p.join(root.path, 'project'))..createSync();
    File(p.join(project.path, 'topiaforge.project.json')).writeAsStringSync(
      jsonEncode({
        'schemaVersion': 2,
        'id': 'example.unity',
        'name': 'Unity Example',
        'unityCompanion': {'enabled': true, 'unityVersion': configuredVersion},
      }),
    );
    final report = await createRepository(
      editors: [
        ...installedEditors(),
        UnityEditor(version: configuredVersion, path: configuredPath),
      ],
    ).runDoctor(projectPath: project.path);

    expect(report.hasProject, isTrue);
    expect(report.unityEditorPath, configuredPath);
  });
}
