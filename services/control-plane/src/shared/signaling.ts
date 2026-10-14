/**
 * The signaling grammar (docs/internal/ControlPlaneApi.md, "Signaling"). Frames are canonical JSON text of at most
 * 8 KiB. A joining peer offers and the host answers; both trickle candidates, ask the other side to start punching,
 * and say goodbye. Every member is typed and bounded and there is no free text, so the only place an address can
 * travel is a candidate, which the service filters before forwarding.
 */
import { isJsonObject, parse, type JsonObject, type JsonValue } from "./canonical-json";
import { isLocalOnly, normalizeIp, parseIp } from "./ip";
import { utf8 } from "./wire-text";

export const MAXIMUM_FRAME_BYTES = 8192;
export const MAXIMUM_CANDIDATES = 16;
export const MAXIMUM_PUNCH_DELAY_MS = 10_000;
export const PEER_ID_PATTERN = /^[0-9a-f]{16}$/;

export type SignalRole = "host" | "peer";
export type CandidateKind = "host" | "srflx" | "relay";
export type ByeReason = "done" | "cancelled" | "failed" | "timeout";

export interface Candidate {
  readonly kind: CandidateKind;
  readonly address: string;
  readonly port: number;
  readonly priority: number;
}

export interface IceCredentials {
  readonly ufrag: string;
  readonly pwd: string;
}

export type SignalMessage =
  | { readonly type: "offer" | "answer"; readonly peer: string | null; readonly ice: IceCredentials; readonly candidates: readonly Candidate[] }
  | { readonly type: "candidate"; readonly peer: string | null; readonly candidate: Candidate }
  | { readonly type: "punch"; readonly peer: string | null; readonly delayMs: number }
  | { readonly type: "bye"; readonly peer: string | null; readonly reason: ByeReason };

const ICE_UFRAG = /^[A-Za-z0-9+/]{4,32}$/;
const ICE_PWD = /^[A-Za-z0-9+/]{22,64}$/;
const BYE_REASONS: readonly ByeReason[] = ["done", "cancelled", "failed", "timeout"];

/**
 * Reads one frame from `role`. A peer sends offers and never names a peer; the host sends answers and always names
 * the peer it addresses. Anything else, including a frame that is not canonical, gives null.
 */
export function readSignal(role: SignalRole, frame: string): SignalMessage | null {
  const bytes = utf8(frame);
  if (bytes.length > MAXIMUM_FRAME_BYTES) return null;
  const parsed = parse(bytes, MAXIMUM_FRAME_BYTES);
  if (!parsed.ok || !isJsonObject(parsed.value)) return null;
  const value = parsed.value;
  const addressed = role === "host";
  const peer = addressed ? value["peer"] : null;
  if (addressed && (typeof peer !== "string" || !PEER_ID_PATTERN.test(peer))) return null;
  const target = addressed ? (peer as string) : null;
  const extra = addressed ? ["peer"] : [];
  switch (value["type"]) {
    case "offer":
    case "answer": {
      if (value["type"] !== (addressed ? "answer" : "offer") || !exactly(value, ["type", "ice", "candidates", ...extra])) return null;
      const ice = readIce(value["ice"]);
      const candidates = readCandidates(value["candidates"]);
      return ice === null || candidates === null ? null : { type: value["type"], peer: target, ice, candidates };
    }
    case "candidate": {
      if (!exactly(value, ["type", "candidate", ...extra])) return null;
      const candidate = readCandidate(value["candidate"]);
      return candidate === null ? null : { type: "candidate", peer: target, candidate };
    }
    case "punch": {
      const delayMs = value["delayMs"];
      if (!exactly(value, ["type", "delayMs", ...extra]) || typeof delayMs !== "number" || delayMs < 0 || delayMs > MAXIMUM_PUNCH_DELAY_MS) {
        return null;
      }
      return { type: "punch", peer: target, delayMs };
    }
    case "bye": {
      const reason = value["reason"];
      if (!exactly(value, ["type", "reason", ...extra]) || !BYE_REASONS.includes(reason as ByeReason)) return null;
      return { type: "bye", peer: target, reason: reason as ByeReason };
    }
    default:
      return null;
  }
}

/** What a side may reveal about its own network position. */
export interface SenderPrivacy {
  readonly relayOnly: boolean;
  /** The keyed fingerprint of the sender's own public address, when the service saw one. */
  readonly selfFingerprint: string | null;
}

/**
 * The candidates a sender's message may carry onward. A relay-only side keeps only relay candidates, and of those only
 * ones that are not local-only addresses and not the sender's own observed address: a client that mislabels its
 * address as a relay still never reveals it.
 */
export async function permittedCandidates(
  candidates: readonly Candidate[],
  privacy: SenderPrivacy,
  fingerprint: (address: string) => Promise<string>,
): Promise<readonly Candidate[]> {
  if (!privacy.relayOnly) return candidates;
  const kept: Candidate[] = [];
  for (const candidate of candidates) {
    if (candidate.kind !== "relay") continue;
    const address = parseIp(candidate.address);
    if (address === null || isLocalOnly(address)) continue;
    if (privacy.selfFingerprint !== null && (await fingerprint(candidate.address)) === privacy.selfFingerprint) continue;
    kept.push(candidate);
  }
  return kept;
}

/** The frame forwarded to the other side: the host learns which peer spoke, and a peer never sees a peer id. */
export function forwardedFrame(message: SignalMessage, fromPeer: string | null, candidates: readonly Candidate[]): JsonObject {
  const head: Record<string, JsonValue> = { type: message.type };
  if (fromPeer !== null) head["peer"] = fromPeer;
  switch (message.type) {
    case "offer":
    case "answer":
      return { ...head, ice: { ufrag: message.ice.ufrag, pwd: message.ice.pwd }, candidates: candidates.map(candidateJson) };
    case "candidate":
      return { ...head, candidate: candidateJson(candidates[0]!) };
    case "punch":
      return { ...head, delayMs: message.delayMs };
    case "bye":
      return { ...head, reason: message.reason };
  }
}

/** The candidates a message carries, in order. */
export function candidatesOf(message: SignalMessage): readonly Candidate[] {
  if (message.type === "offer" || message.type === "answer") return message.candidates;
  if (message.type === "candidate") return [message.candidate];
  return [];
}

function candidateJson(candidate: Candidate): JsonObject {
  return { kind: candidate.kind, address: candidate.address, port: candidate.port, priority: candidate.priority };
}

function readIce(value: JsonValue | undefined): IceCredentials | null {
  if (!isJsonObject(value) || !exactly(value, ["ufrag", "pwd"])) return null;
  const ufrag = value["ufrag"];
  const pwd = value["pwd"];
  if (typeof ufrag !== "string" || typeof pwd !== "string" || !ICE_UFRAG.test(ufrag) || !ICE_PWD.test(pwd)) return null;
  return { ufrag, pwd };
}

function readCandidates(value: JsonValue | undefined): readonly Candidate[] | null {
  if (!Array.isArray(value)) return null;
  const items = value as readonly JsonValue[];
  if (items.length > MAXIMUM_CANDIDATES) return null;
  const candidates: Candidate[] = [];
  for (const item of items) {
    const candidate = readCandidate(item);
    if (candidate === null) return null;
    candidates.push(candidate);
  }
  return candidates;
}

function readCandidate(value: JsonValue | undefined): Candidate | null {
  if (!isJsonObject(value) || !exactly(value, ["kind", "address", "port", "priority"])) return null;
  const { kind, address, port, priority } = value;
  if (kind !== "host" && kind !== "srflx" && kind !== "relay") return null;
  // One spelling per address: the canonical text form, so filtering and fingerprints see what the peer would.
  if (typeof address !== "string" || normalizeIp(address) !== address) return null;
  if (typeof port !== "number" || port < 1 || port > 65535) return null;
  if (typeof priority !== "number" || priority < 0 || priority > 0xffffffff) return null;
  return { kind, address, port, priority };
}

function exactly(value: JsonObject, keys: readonly string[]): boolean {
  const actual = Object.keys(value);
  return actual.length === keys.length && keys.every((key) => Object.hasOwn(value, key));
}
