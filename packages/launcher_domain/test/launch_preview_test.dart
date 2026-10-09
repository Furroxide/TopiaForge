import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';
import 'launch_preview_test_helpers.dart';

void main() {
  test(
    'preview uses installed targets and deterministic resolver defaults',
    () {
      final preview = buildPreview(previewProfile([previewPackage()]));
      expect(preview.canLaunch, isTrue);
      expect(preview.targets.single.id, targetId);
      expect(preview.worlds.single.id, worldId);
      expect(preview.transitions, ModTransitions.byPrecedence);
      expect(
        preview.resolution!.plan!.transition,
        ModTransitions.sceneReplacement,
      );
    },
  );
  test('no gamemode means no invented transition or world choices', () {
    final preview = buildPreview(previewProfile([previewPackage(modes: [])]));
    expect(preview.canLaunch, isFalse);
    expect(preview.transitions, isEmpty);
    expect(preview.worlds, isEmpty);
  });
  test('no gamemode means no invented world override choices', () {
    final preview = buildPreview(previewProfile([previewPackage(modes: [])]));
    expect(preview.worlds, isEmpty);
  });
  test('absent override permission offers no world overrides', () {
    final preview = buildPreview(
      previewProfile([
        previewPackage(targets: [previewTarget(override: null)]),
      ]),
    );
    expect(preview.canLaunch, isTrue);
    expect(preview.allowWorldOverride, isFalse);
    expect(preview.worlds, isEmpty);
  });
  test('open choices filter worlds without consent', () {
    final preview = buildPreview(
      previewProfile([
        previewPackage(
          worlds: [
            previewWorld(),
            previewWorld(id: '$ownerId.closed', consent: false),
          ],
        ),
      ]),
    );
    expect(preview.worlds.map((world) => world.id), [worldId]);
  });
  test('list policy admits only the declared worlds', () {
    final preview = buildPreview(
      previewProfile([
        previewPackage(
          worlds: [
            previewWorld(),
            previewWorld(id: '$ownerId.allowed'),
            previewWorld(id: '$ownerId.other'),
          ],
          targets: [
            previewTarget(policy: 'list', allow: ['$ownerId.allowed']),
          ],
        ),
      ]),
    );
    expect(preview.worlds.map((world) => world.id), [
      '$ownerId.allowed',
      worldId,
    ]);
  });
  test('disabled target stays visible and unavailable', () {
    final preview = buildPreview(
      previewProfile([], disabled: [previewPackage()]),
    );
    expect(preview.canLaunch, isFalse);
    expect(preview.targets.single.selectable, isFalse);
    expect(preview.blocks.single.code, LaunchBlockCode.targetPackageDisabled);
  });
  test('profile package order cannot change choices', () {
    final a = previewPackage();
    final b = previewPackage(
      id: 'other.mod',
      worlds: [],
      modes: [],
      targets: [],
    );
    final first = buildPreview(previewProfile([a, b]));
    final second = buildPreview(previewProfile([b, a]));
    expect(first.packageDigest, second.packageDigest);
    expect(first.targets.map((t) => t.id), second.targets.map((t) => t.id));
  });
  test('safe mode preserves requested unavailable selection', () {
    final selection = LaunchSelection.target(
      LaunchRequest(targetId: 'missing.target'),
    );
    final preview = buildPreview(
      previewProfile([]),
      selection: selection,
      safe: true,
    );
    expect(preview.canLaunch, isTrue);
    expect(preview.requestedSelection, selection);
    expect(preview.effectiveSelection.kind, LaunchSelectionKind.mainMenu);
  });
}
