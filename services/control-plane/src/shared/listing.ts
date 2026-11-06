/**
 * The public summary of a hosted session, as the server browser shows it (schema `topiaforge.server-listing/1`).
 * Every member comes from the signed descriptor or from the host's own heartbeat. A listing has no member that can
 * hold an address, a port or a candidate, and it never will.
 */
import type { JsonObject } from "./canonical-json";
import type { ServerDescriptor } from "./descriptor";

export const LISTING_SCHEMA = "topiaforge.server-listing/1";

/** The descriptor fields a listing repeats, kept by the host session beside the signed bytes. */
export interface DescriptorSummary {
  readonly name: string;
  readonly gameBuild: string;
  readonly runtimeVersion: string;
  readonly protocolVersion: string;
  readonly maxPlayers: number;
  readonly passwordRequired: boolean;
  readonly voice: boolean;
  readonly hostServesPackages: boolean;
  readonly gamemode: string | null;
  readonly world: string | null;
  readonly packageCount: number;
  readonly packageDigest: string;
}

export interface ListingState {
  readonly serverId: string;
  readonly descriptorSha256: string;
  readonly visibility: "public" | "invite";
  readonly players: number;
  readonly relayOnly: boolean;
  readonly updatedAt: number;
}

export function summarize(descriptor: ServerDescriptor): DescriptorSummary {
  return {
    name: descriptor.name,
    gameBuild: descriptor.gameBuild,
    runtimeVersion: descriptor.runtimeVersion,
    protocolVersion: descriptor.protocolVersion,
    maxPlayers: descriptor.maxPlayers,
    passwordRequired: descriptor.passwordRequired,
    voice: descriptor.voice,
    hostServesPackages: descriptor.hostServesPackages,
    gamemode: descriptor.gamemode,
    world: descriptor.world,
    packageCount: descriptor.packages.length,
    packageDigest: descriptor.packageDigest,
  };
}

export function listingJson(summary: DescriptorSummary, state: ListingState): JsonObject {
  return {
    schema: LISTING_SCHEMA,
    serverId: state.serverId,
    descriptorSha256: state.descriptorSha256,
    visibility: state.visibility,
    name: summary.name,
    gameBuild: summary.gameBuild,
    runtimeVersion: summary.runtimeVersion,
    protocolVersion: summary.protocolVersion,
    gamemode: summary.gamemode,
    world: summary.world,
    players: state.players,
    maxPlayers: summary.maxPlayers,
    passwordRequired: summary.passwordRequired,
    voice: summary.voice,
    hostServesPackages: summary.hostServesPackages,
    packageCount: summary.packageCount,
    packageDigest: summary.packageDigest,
    relayOnly: state.relayOnly,
    updatedAt: state.updatedAt,
  };
}
