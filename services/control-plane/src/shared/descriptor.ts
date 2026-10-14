/**
 * Signed server descriptors (docs/internal/MultiplayerLiveSessions.md, section 3). The control plane verifies them
 * exactly as the game and the launcher do: canonical JSON of at most 256 KiB, exactly the version 1 members, a server
 * id equal to the SHA-256 of the signing key, a package digest over the canonical package list, and an Ed25519
 * signature over a context label followed by the descriptor's canonical bytes. The shared fixtures in
 * tests/fixtures/multiplayer/descriptor pin every rule.
 */
import { isJsonObject, parse, serialize, type JsonObject, type JsonValue } from "./canonical-json";
import { concatBytes, isDisplayText, isHex, parseBase64Url, sha256Hex, utf8, type Bytes } from "./wire-text";

export const DESCRIPTOR_SCHEMA = "topiaforge.server-descriptor/1";
export const MAXIMUM_DESCRIPTOR_BYTES = 256 * 1024;
export const MAXIMUM_PACKAGES = 512;
export const MINIMUM_PLAYERS = 2;
export const MAXIMUM_PLAYERS = 16;
const MAXIMUM_NAME_LENGTH = 48;
const MAXIMUM_VERSION_LENGTH = 64;
const CONTEXT = utf8("TopiaForge server descriptor v1\n");

const DESCRIPTOR_MEMBERS = [
  "schema",
  "serverId",
  "signingKey",
  "transportKey",
  "issuedAt",
  "name",
  "gameBuild",
  "runtimeVersion",
  "protocolVersion",
  "maxPlayers",
  "passwordRequired",
  "voice",
  "visibility",
  "hostServesPackages",
  "gamemode",
  "world",
  "packages",
  "packageDigest",
] as const;
const PACKAGE_MEMBERS = ["id", "version", "sha256", "role"] as const;

const MOD_ID = /^[A-Za-z0-9][A-Za-z0-9_.-]{1,63}$/;
const GAME_BUILD = /^[0-9A-Za-z._-]{1,16}$/;
const SEMANTIC_VERSION =
  /^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$/;

export type Visibility = "public" | "invite" | "lan";
export type PackageRole = "session-required" | "session-optional" | "implicit";

export interface DescriptorPackage {
  readonly id: string;
  readonly version: string;
  readonly sha256: string;
  readonly role: PackageRole;
}

export interface ServerDescriptor {
  readonly serverId: string;
  readonly signingKey: Bytes;
  readonly transportKey: Bytes;
  readonly issuedAt: number;
  readonly name: string;
  readonly gameBuild: string;
  readonly runtimeVersion: string;
  readonly protocolVersion: string;
  readonly maxPlayers: number;
  readonly passwordRequired: boolean;
  readonly voice: boolean;
  readonly visibility: Visibility;
  readonly hostServesPackages: boolean;
  readonly gamemode: string | null;
  readonly world: string | null;
  readonly packages: readonly DescriptorPackage[];
  readonly packageDigest: string;
}

export interface SignedDescriptor {
  readonly descriptor: ServerDescriptor;
  readonly signature: Bytes;
  /** The exact canonical bytes that are served and pinned. */
  readonly bytes: Bytes;
  /** The lowercase hexadecimal SHA-256 of `bytes`, which a join plan pins. */
  readonly sha256: string;
}

export type VerifyResult = { readonly ok: true; readonly signed: SignedDescriptor } | { readonly ok: false; readonly error: string };

class DescriptorFormatError extends Error {}

export function isSemanticVersion(value: unknown): value is string {
  return typeof value === "string" && value.length <= MAXIMUM_VERSION_LENGTH && SEMANTIC_VERSION.test(value);
}

export function isGameBuild(value: unknown): value is string {
  return typeof value === "string" && GAME_BUILD.test(value);
}

export function isModId(value: unknown): value is string {
  return typeof value === "string" && MOD_ID.test(value);
}

/** The major component of a semantic version, as decimal text. */
export function protocolMajor(protocolVersion: string): string {
  return protocolVersion.slice(0, protocolVersion.indexOf("."));
}

/**
 * Checks a served descriptor: canonical JSON within the size bound, every member valid, the signature good for the
 * signing key inside it, and, when `expectedServerId` is given, that key's hash equal to it. The error text is for
 * tests; callers answer every failure with one uniform error.
 */
export async function verifySignedDescriptor(bytes: Uint8Array, expectedServerId: string | null): Promise<VerifyResult> {
  const parsed = parse(bytes, MAXIMUM_DESCRIPTOR_BYTES);
  if (!parsed.ok) return parsed;
  const envelope = parsed.value;
  if (!isJsonObject(envelope) || !hasExactly(envelope, ["descriptor", "signature"])) {
    return { ok: false, error: "A signed descriptor has exactly a descriptor and a signature." };
  }
  const signature = parseBase64Url(envelope["signature"], 64);
  if (signature === null) return { ok: false, error: "The signature is malformed." };
  let descriptor: ServerDescriptor;
  try {
    descriptor = await readDescriptor(envelope["descriptor"]);
  } catch (error) {
    if (error instanceof DescriptorFormatError) return { ok: false, error: error.message };
    throw error;
  }
  if (expectedServerId !== null && descriptor.serverId !== expectedServerId) {
    return { ok: false, error: "The descriptor belongs to another server." };
  }
  const message = concatBytes(CONTEXT, serialize(envelope["descriptor"]!));
  if (!(await verifyEd25519(descriptor.signingKey, signature, message))) {
    return { ok: false, error: "The descriptor signature does not verify." };
  }
  const copy = new Uint8Array(bytes);
  return { ok: true, signed: { descriptor, signature, bytes: copy, sha256: await sha256Hex(copy) } };
}

/** Verifies an Ed25519 signature; a key that is not a valid point simply fails. */
export async function verifyEd25519(publicKey: Bytes, signature: Bytes, message: Bytes): Promise<boolean> {
  try {
    const key = await crypto.subtle.importKey("raw", publicKey, { name: "Ed25519" }, false, ["verify"]);
    return await crypto.subtle.verify({ name: "Ed25519" }, key, signature, message);
  } catch {
    return false;
  }
}

async function readDescriptor(node: JsonValue | undefined): Promise<ServerDescriptor> {
  if (!isJsonObject(node) || !hasExactly(node, DESCRIPTOR_MEMBERS)) {
    throw new DescriptorFormatError("A server descriptor has exactly the version 1 members.");
  }
  if (node["schema"] !== DESCRIPTOR_SCHEMA) throw new DescriptorFormatError(`The descriptor schema is not ${DESCRIPTOR_SCHEMA}.`);
  const signingKey = parseBase64Url(node["signingKey"], 32);
  if (signingKey === null) throw new DescriptorFormatError("The signing key is malformed.");
  const transportKey = parseBase64Url(node["transportKey"], 32);
  if (transportKey === null) throw new DescriptorFormatError("The transport key is malformed.");
  const packages = readPackages(node["packages"]);
  const issuedAt = node["issuedAt"];
  if (typeof issuedAt !== "number" || issuedAt < 0) throw new DescriptorFormatError("The issue time is invalid.");
  const name = node["name"];
  if (!isDisplayText(name, MAXIMUM_NAME_LENGTH)) throw new DescriptorFormatError("A server name is 1 to 48 printable characters.");
  if (!isGameBuild(node["gameBuild"])) throw new DescriptorFormatError("The game build is invalid.");
  if (!isSemanticVersion(node["runtimeVersion"])) throw new DescriptorFormatError("The runtime version is not a semantic version.");
  if (!isSemanticVersion(node["protocolVersion"])) throw new DescriptorFormatError("The protocol version is not a semantic version.");
  const maxPlayers = node["maxPlayers"];
  if (typeof maxPlayers !== "number" || maxPlayers < MINIMUM_PLAYERS || maxPlayers > MAXIMUM_PLAYERS) {
    throw new DescriptorFormatError("Sessions hold 2 to 16 players.");
  }
  const passwordRequired = node["passwordRequired"];
  const voice = node["voice"];
  const hostServesPackages = node["hostServesPackages"];
  if (typeof passwordRequired !== "boolean" || typeof voice !== "boolean" || typeof hostServesPackages !== "boolean") {
    throw new DescriptorFormatError("Session settings are true or false.");
  }
  const visibility = node["visibility"];
  if (visibility !== "public" && visibility !== "invite" && visibility !== "lan") {
    throw new DescriptorFormatError("Visibility is public, invite or lan.");
  }
  const gamemode = readTargetName(node["gamemode"]);
  const world = readTargetName(node["world"]);
  for (let index = 1; index < packages.length; index++) {
    if (!(packages[index - 1]!.id < packages[index]!.id)) {
      throw new DescriptorFormatError("Packages are listed once each, in ordinal order of their ids.");
    }
  }
  const serverId = await sha256Hex(signingKey);
  if (node["serverId"] !== serverId) throw new DescriptorFormatError("The server id does not match the signing key.");
  const packageDigest = await sha256Hex(serialize(node["packages"]!));
  if (node["packageDigest"] !== packageDigest) throw new DescriptorFormatError("The package digest does not match the packages.");
  return {
    serverId,
    signingKey,
    transportKey,
    issuedAt,
    name,
    gameBuild: node["gameBuild"] as string,
    runtimeVersion: node["runtimeVersion"] as string,
    protocolVersion: node["protocolVersion"] as string,
    maxPlayers,
    passwordRequired,
    voice,
    visibility,
    hostServesPackages,
    gamemode,
    world,
    packages,
    packageDigest,
  };
}

function readTargetName(value: JsonValue | undefined): string | null {
  if (value === null) return null;
  if (!isDisplayText(value, MAXIMUM_NAME_LENGTH)) throw new DescriptorFormatError("A target name is 1 to 48 printable characters.");
  return value;
}

function readPackages(value: JsonValue | undefined): DescriptorPackage[] {
  if (!Array.isArray(value)) throw new DescriptorFormatError("The packages member is a list.");
  const items = value as readonly JsonValue[];
  if (items.length > MAXIMUM_PACKAGES) throw new DescriptorFormatError("A descriptor lists at most 512 packages.");
  return items.map((item) => {
    if (!isJsonObject(item) || !hasExactly(item, PACKAGE_MEMBERS)) {
      throw new DescriptorFormatError("A package entry has exactly an id, version, sha256 and role.");
    }
    const role = item["role"];
    if (role !== "session-required" && role !== "session-optional" && role !== "implicit") {
      throw new DescriptorFormatError("A package role is session-required, session-optional or implicit.");
    }
    if (!isModId(item["id"])) throw new DescriptorFormatError("A package id is a manifest mod id.");
    if (!isSemanticVersion(item["version"])) throw new DescriptorFormatError("A package version is a semantic version.");
    if (!isHex(item["sha256"], 32)) throw new DescriptorFormatError("A package digest is 64 lowercase hexadecimal characters.");
    return { id: item["id"], version: item["version"], sha256: item["sha256"], role };
  });
}

export function hasExactly(value: JsonObject, keys: readonly string[]): boolean {
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => Object.hasOwn(value, key));
}
