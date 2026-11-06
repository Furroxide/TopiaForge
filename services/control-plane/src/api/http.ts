/**
 * Responses and request bodies. Every response is canonical JSON with the same small header set, and every error is
 * one of a fixed catalogue: a code, a status and a fixed message. Nothing a client sent, and nothing about the
 * service's internals, ever appears in an error.
 */
import { parse, stringify, type JsonValue } from "../shared/canonical-json";

export type ErrorCode =
  | "invalid-request"
  | "invalid-descriptor"
  | "unsupported-visibility"
  | "challenge-invalid"
  | "proof-invalid"
  | "unauthorized"
  | "server-blocked"
  | "ticket-limit"
  | "not-found"
  | "method-not-allowed"
  | "stale-descriptor"
  | "invite-limit"
  | "room-full"
  | "payload-too-large"
  | "unsupported-media-type"
  | "rate-limited"
  | "internal"
  | "upstream-failed"
  | "feature-disabled"
  | "relay-unavailable"
  | "service-unavailable";

export const ERRORS: Readonly<Record<ErrorCode, { readonly status: number; readonly message: string }>> = {
  "invalid-request": { status: 400, message: "The request is malformed." },
  "invalid-descriptor": { status: 400, message: "The server descriptor is invalid." },
  "unsupported-visibility": { status: 400, message: "LAN sessions are not registered with the control plane." },
  "challenge-invalid": { status: 401, message: "The challenge is invalid, used or expired." },
  "proof-invalid": { status: 401, message: "The proof does not verify." },
  unauthorized: { status: 401, message: "Valid credentials are required." },
  "server-blocked": { status: 403, message: "This server may not register at the moment." },
  "ticket-limit": { status: 403, message: "This ticket may not mint more relay credentials." },
  "not-found": { status: 404, message: "Not found." },
  "method-not-allowed": { status: 405, message: "The method is not allowed here." },
  "stale-descriptor": { status: 409, message: "A newer descriptor is already registered." },
  "invite-limit": { status: 409, message: "The server has as many invites as it may hold." },
  "room-full": { status: 409, message: "The signaling room is full." },
  "payload-too-large": { status: 413, message: "The request body is too large." },
  "unsupported-media-type": { status: 415, message: "The request body must be application/json." },
  "rate-limited": { status: 429, message: "Too many requests." },
  internal: { status: 500, message: "The service failed." },
  "upstream-failed": { status: 502, message: "The relay provider did not answer." },
  "feature-disabled": { status: 503, message: "This feature is turned off." },
  "relay-unavailable": { status: 503, message: "Relay credentials are unavailable." },
  "service-unavailable": { status: 503, message: "The service is not configured." },
};

export class ApiError extends Error {
  override readonly name = "ApiError";

  constructor(
    readonly code: ErrorCode,
    readonly retryAfterSeconds?: number,
  ) {
    super(code);
  }
}

export const JSON_CONTENT_TYPE = "application/json; charset=utf-8";
const BASE_HEADERS: Readonly<Record<string, string>> = {
  "cache-control": "no-store",
  "x-content-type-options": "nosniff",
};

export function jsonResponse(status: number, body: JsonValue, headers: Readonly<Record<string, string>> = {}): Response {
  return new Response(stringify(body), { status, headers: { ...BASE_HEADERS, "content-type": JSON_CONTENT_TYPE, ...headers } });
}

export function emptyResponse(): Response {
  return new Response(null, { status: 204, headers: BASE_HEADERS });
}

export function errorResponse(code: ErrorCode, retryAfterSeconds?: number, headers: Readonly<Record<string, string>> = {}): Response {
  const entry = ERRORS[code];
  const extra: Record<string, string> = { ...headers };
  if (retryAfterSeconds !== undefined) extra["retry-after"] = String(Math.max(1, Math.ceil(retryAfterSeconds)));
  return jsonResponse(entry.status, { error: { code, message: entry.message } }, extra);
}

/**
 * Reads a canonical JSON body of at most `maximumBytes`. The size bound holds even when the client sends no length or
 * lies about it, because the stream is cut off at the bound.
 */
export async function readJsonBody(request: Request, maximumBytes: number): Promise<JsonValue> {
  const contentType = request.headers.get("content-type");
  if (contentType === null || !/^application\/json(\s*;\s*charset=utf-8)?$/i.test(contentType)) {
    throw new ApiError("unsupported-media-type");
  }
  const declared = request.headers.get("content-length");
  if (declared !== null && (!/^[0-9]{1,10}$/.test(declared) || Number(declared) > maximumBytes)) {
    throw new ApiError("payload-too-large");
  }
  if (request.body === null) throw new ApiError("invalid-request");
  const reader = request.body.getReader();
  const chunks: Uint8Array[] = [];
  let total = 0;
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    total += value.byteLength;
    if (total > maximumBytes) {
      await reader.cancel();
      throw new ApiError("payload-too-large");
    }
    chunks.push(value);
  }
  const bytes = new Uint8Array(total);
  let offset = 0;
  for (const chunk of chunks) {
    bytes.set(chunk, offset);
    offset += chunk.byteLength;
  }
  const parsed = parse(bytes, maximumBytes);
  if (!parsed.ok) throw new ApiError("invalid-request");
  return parsed.value;
}

/** Refuses a body on a method that takes none, so nothing is silently ignored. */
export function requireNoBody(request: Request): void {
  const declared = request.headers.get("content-length");
  if ((declared !== null && declared !== "0") || request.headers.has("transfer-encoding")) throw new ApiError("invalid-request");
}
