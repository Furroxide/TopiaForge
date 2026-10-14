import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

import 'multiplayer_join_support.dart';

// A direct target's host and port exist only in the session's own launch
// request. Every other session document, and every message, leaves them out.
const _host = '203.0.113.77';
const _port = 47819;

void main() {
  final settings = MultiplayerSettings(
    displayName: 'Sam',
  ).acknowledgePrivacyDisclosure();
  final session = LaunchSessionRequest.join(
    intentId: 'intent-9',
    settings: settings,
    privacy: settings.privacyForSession(allowDirect: true),
    target: LaunchDirectTarget(
      host: _host,
      port: _port,
      serverId: hostAServerId,
      descriptorSha256: 'a' * 64,
      packageDigest: 'b' * 64,
    ),
  );
  final request = StagedLaunchRequest.forLaunch(
    ProfileLaunchConfigurationV4(
      profileId: 'mp-0123456789abcdef',
      profileRevision: 0,
      requestId: '0123456789abcdef0123456789abcdef',
      command: 'main-menu',
      safeMode: false,
      inheritManagerModState: false,
      enabledMods: const [],
      selectedVersions: const {},
      packages: const [],
    ),
    session: session,
  );

  void expectAddressless(String text, String label) {
    expect(text, isNot(contains(_host)), reason: label);
    expect(text, isNot(contains('$_port')), reason: label);
  }

  test('only the launch request holds the direct target', () {
    expect(request.encode(), contains(_host));
    expect(request.encode(), contains('$_port'));
  });

  test('no other document names the address', () {
    final multiplayer = LaunchOutcomeMultiplayer(
      role: LaunchSessionRole.join,
      intentId: session.intentId,
      serverId: session.target!.serverId,
      failure: LaunchMultiplayerFailure.timeout,
      detail: 'The host did not answer.',
    );
    final documents = <String, String>{
      'progress': LaunchTransport.writeProgress(
        LaunchProgress(
          requestId: request.requestId,
          sequence: 2,
          phase: 'connecting',
          session: LaunchProgressSession(
            role: LaunchSessionRole.join,
            intentId: session.intentId,
            state: LaunchSessionState.connecting,
            serverId: session.target!.serverId,
          ),
        ),
      ),
      'outcome': LaunchTransport.writeOutcome(
        LaunchOutcome(
          kind: 'launch',
          requestId: request.requestId,
          command: request.command,
          sequence: 3,
          status: 'failed',
          phase: 'connecting',
          multiplayer: multiplayer,
        ),
      ),
      'handoff': LaunchTransport.writeSessionHandoff(
        SessionHandoff(
          requestId: request.requestId,
          handoffId: 'handoff-1',
          target: const LaunchHandoffCurrent(),
        ),
      ),
      'preferences': LaunchTransport.writePreferences(
        MultiplayerPreferencesProjection.fromSettings(
          settings,
          hosting: MultiplayerHostingDefaults.forPlayer('Sam'),
          revision: 1,
        ),
      ),
      'journal': LaunchTransport.writePreferencesJournal(
        MultiplayerPreferencesJournal([
          DisplayNameChange(sequence: 1, displayName: 'Sam'),
        ]),
      ),
    };
    for (final entry in documents.entries) {
      expectAddressless(entry.value, entry.key);
    }
  });

  test('no description names the address', () {
    for (final value in <Object>[
      session,
      session.target!,
      request,
      (request as StagedV5LaunchRequest).request,
      JoinAddressTarget(host: _host, port: _port),
    ]) {
      expectAddressless(value.toString(), value.runtimeType.toString());
    }
  });

  test('no refusal repeats a refused host', () {
    for (final host in ['203.0.113.077', 'Play.Example.ORG', '$_host:$_port']) {
      try {
        LaunchDirectTarget(
          host: host,
          port: _port,
          serverId: hostAServerId,
          descriptorSha256: 'a' * 64,
          packageDigest: 'b' * 64,
        );
        fail('$host was accepted');
      } on FormatException catch (error) {
        expect(error.toString(), isNot(contains(host)));
        expect(error.toString(), isNot(contains('$_port')));
      }
    }
    try {
      LaunchSessionRequest.join(
        intentId: 'intent-9',
        settings: settings,
        privacy: SessionPrivacy.relayOnly,
        target: session.target!,
      );
      fail('a public direct target was accepted under relay-only');
    } on FormatException catch (error) {
      expectAddressless(error.toString(), 'relay-only refusal');
    }
  });

  test('a live join never carries an address', () {
    expect(
      () => SessionJoinRequest(
        requestId: 'process-1',
        joinId: 'join-1',
        packageSetDigest: '0123456789abcdef',
        session: session,
      ),
      throwsFormatException,
    );
  });
}
