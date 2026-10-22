import 'dart:io';
import 'dart:math';

import 'package:launcher_data/launcher_data.dart';
import 'package:launcher_data/src/multiplayer/control_plane_client.dart';
import 'package:launcher_domain/launcher_domain.dart';
import 'package:test/test.dart';

/// The variable that names the live service to check, such as a staging
/// deployment. Without it, the check is skipped.
const originVariable = 'TOPIAFORGE_CONTROL_PLANE_SMOKE_ORIGIN';

/// An opt-in check of the launcher's client against a live service. It makes
/// three requests and no more: `GET /v1/status`, one page of the server
/// list, and one invite resolution with a random code, which misses. It
/// never hosts, and never asks for a join ticket or relay credentials.
///
/// `TOPIAFORGE_CONTROL_PLANE_SMOKE_BUILD` and
/// `TOPIAFORGE_CONTROL_PLANE_SMOKE_PROTOCOL` choose the page's game build and
/// protocol major; they default to `2545` and `1`.
void main() {
  final environment = Platform.environment;
  final origin = environment[originVariable]?.trim() ?? '';

  test(
    'a live service answers its status, a page of servers and one invite '
    'miss',
    () async {
      final service = MultiplayerServiceConfiguration.parse(origin);
      expect(
        service.isConfigured,
        isTrue,
        reason: '$originVariable is not an origin a build could name',
      );
      final log = <String>[];
      final client = HttpsControlPlaneClient(service.origin!, log: log.add);

      final status = await client.readStatus();
      printOnFailure(log.join('\n'));
      // Nothing more is asked of a service whose status cannot be read.
      expect(status.isAvailable, isTrue, reason: status.message);
      final features = status.features!;
      final page = await client.browse(
        ServerBrowseFilter(
          gameBuild:
              environment['TOPIAFORGE_CONTROL_PLANE_SMOKE_BUILD'] ?? '2545',
          protocolMajor: int.parse(
            environment['TOPIAFORGE_CONTROL_PLANE_SMOKE_PROTOCOL'] ?? '1',
          ),
        ),
      );
      final random = Random.secure();
      final code = String.fromCharCodes([
        for (var index = 0; index < InviteCode.length; index++)
          InviteCode.alphabet.codeUnitAt(random.nextInt(32)),
      ]);
      final miss = await client.lookUpInvite(code);

      // The code is never printed; the counts are.
      print(
        'status: hosting ${features.hosting}, invites ${features.invites}, '
        'signaling ${features.signaling}, relay ${features.relay}, kill '
        'switch ${features.killSwitch}, heartbeat '
        '${features.heartbeat.inSeconds} s, listing TTL '
        '${features.listingTtl.inSeconds} s, relay credentials '
        '${features.relayCredentialTtl.inSeconds} s',
      );
      print(
        'server list: ${page.isAvailable ? '${page.listings.length} listings, ${page.next == null ? 'no further page' : 'a further page'}' : 'unavailable (${page.problem?.name})'}',
      );
      print('invite miss: ${miss.problem?.name}');
      print(
        'requests: ${log.where((line) => line.contains(' answered ')).length}',
      );

      expect(page.isAvailable, isTrue, reason: page.unavailableMessage);
      expect(
        miss.problem,
        anyOf(
          MultiplayerServiceProblem.notFound,
          MultiplayerServiceProblem.rateLimited,
        ),
      );
      for (final line in log) {
        expect(line, isNot(contains(code)));
      }
    },
    skip: origin.isEmpty ? 'Set $originVariable to run it.' : null,
    timeout: const Timeout(Duration(minutes: 2)),
  );
}
