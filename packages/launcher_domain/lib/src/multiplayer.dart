/// Network-free models for joining live multiplayer sessions: canonical JSON,
/// signed server descriptors, session links, LAN formats, what LAN discovery
/// and the network checks found, settings, join planning and the join
/// reducer.
///
/// The formats match the game's provider byte for byte; the shared fixtures in
/// `tests/fixtures/multiplayer/` pin them (see
/// `docs/internal/MultiplayerLiveSessions.md`). Nothing here opens a socket or
/// performs cryptography: signature checks go through
/// [ServerDescriptorSignatureVerifier], which the data layer implements. No
/// type here holds a peer, candidate or observed network address, except a
/// direct [SessionLink], which only the network layer and the launcher see.
library;

import 'dart:convert';
import 'dart:typed_data';

import 'dependency_planner.dart';
import 'json_duplicate_properties.dart';
import 'models.dart';
import 'multiplayer_admission.dart';
import 'versioning.dart';

part 'multiplayer/wire_text.dart';
part 'multiplayer/pure_sha256.dart';
part 'multiplayer/canonical_json.dart';
part 'multiplayer/canonical_json_reader.dart';
part 'multiplayer/descriptor_package.dart';
part 'multiplayer/server_descriptor.dart';
part 'multiplayer/signed_server_descriptor.dart';
part 'multiplayer/invite_code.dart';
part 'multiplayer/network_host.dart';
part 'multiplayer/session_link.dart';
part 'multiplayer/lan_beacon.dart';
part 'multiplayer/lan_browse_state.dart';
part 'multiplayer/network_report.dart';
part 'multiplayer/session_info.dart';
part 'multiplayer/server_listing.dart';
part 'multiplayer/server_bookmarks.dart';
part 'multiplayer/multiplayer_settings.dart';
part 'multiplayer/join_package_resolution.dart';
part 'multiplayer/exact_package_resolution.dart';
part 'multiplayer/join_plan.dart';
part 'multiplayer/join_planner.dart';
part 'multiplayer/join_planner_checks.dart';
part 'multiplayer/join_session_failures.dart';
part 'multiplayer/join_targets.dart';
part 'multiplayer/known_hosts.dart';
part 'multiplayer/join_requests.dart';
part 'multiplayer/join_session_events.dart';
part 'multiplayer/join_session_state.dart';
part 'multiplayer/join_session.dart';
part 'multiplayer/server_profiles.dart';
part 'multiplayer/server_content_entries.dart';
part 'multiplayer/server_content.dart';
part 'multiplayer/server_content_cleanup.dart';
