import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

void main() {
  test('port-only edits cannot materialize unread game defaults', () {
    final fresh = MultiplayerPreferences(settings: MultiplayerSettings());
    expect(fresh.hasHostingDefaults, isFalse);
    final renamed = const MultiplayerPreferencesChange(
      displayName: 'Sam',
    ).applyTo(fresh);
    expect(renamed.hasHostingDefaults, isFalse);
    expect(
      () => const MultiplayerPreferencesChange(
        hostingPort: 12345,
      ).applyTo(renamed),
      throwsFormatException,
    );
  });
  test('a full review saves all defaults and later port edits retain them', () {
    final defaults = MultiplayerHostingDefaults(
      name: 'Evening game',
      maxPlayers: 12,
      visibility: ServerVisibility.invite,
      voice: true,
      port: 12345,
    );
    final saved = MultiplayerPreferencesChange(
      hostingDefaults: defaults,
    ).applyTo(MultiplayerPreferences(settings: MultiplayerSettings()));
    expect(saved.hasHostingDefaults, isTrue);
    final port = const MultiplayerPreferencesChange(
      hostingPort: 0,
    ).applyTo(saved);
    expect(port.hosting.toJson(), {...defaults.toJson(), 'port': 0});
  });
  test(
    'review compare-and-set refuses concurrent settings or hosting edits',
    () {
      final before = MultiplayerPreferences(
        settings: MultiplayerSettings(),
        hosting: MultiplayerHostingDefaults.forPlayer('Player'),
      );
      final change = MultiplayerPreferencesChange(
        expected: before,
        hostingDefaults: MultiplayerHostingDefaults(name: 'Reviewed'),
      );
      for (final next in [
        const MultiplayerPreferencesChange(displayName: 'Sam').applyTo(before),
        const MultiplayerPreferencesChange(hostingPort: 12345).applyTo(before),
      ]) {
        expect(() => change.applyTo(next), throwsFormatException);
      }
      expect(change.applyTo(before).hosting.name, 'Reviewed');
    },
  );
  test('host choices never persist direct play or passwords', () {
    final choices = HostingChoices(
      defaults: MultiplayerHostingDefaults(name: 'Evening'),
      allowDirect: true,
      passwordRequired: true,
    );
    expect(choices.defaults.toJson().keys.toSet(), {
      'name',
      'maxPlayers',
      'visibility',
      'port',
      'voice',
    });
    expect(choices.defaults.maxPlayers, 8);
    expect(choices.needsService, isFalse);
  });
}
