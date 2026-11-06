/** Exact-shape checks for request bodies. Any deviation is the same `invalid-request`, with no detail. */
import { isJsonObject, type JsonObject, type JsonValue } from "../shared/canonical-json";
import { ApiError } from "./http";

/** Requires an object whose members are exactly `required` plus any of `optional`. */
export function expectObject(value: JsonValue, required: readonly string[], optional: readonly string[] = []): JsonObject {
  if (!isJsonObject(value)) throw new ApiError("invalid-request");
  for (const key of required) if (!Object.hasOwn(value, key)) throw new ApiError("invalid-request");
  for (const key of Object.keys(value)) {
    if (!required.includes(key) && !optional.includes(key)) throw new ApiError("invalid-request");
  }
  return value;
}

export function expectString(value: JsonValue | undefined, pattern?: RegExp, maximumLength = 1024): string {
  if (typeof value !== "string" || value.length > maximumLength || (pattern !== undefined && !pattern.test(value))) {
    throw new ApiError("invalid-request");
  }
  return value;
}

export function expectBoolean(value: JsonValue | undefined): boolean {
  if (typeof value !== "boolean") throw new ApiError("invalid-request");
  return value;
}

export function expectInteger(value: JsonValue | undefined, minimum: number, maximum: number): number {
  if (typeof value !== "number" || !Number.isSafeInteger(value) || value < minimum || value > maximum) {
    throw new ApiError("invalid-request");
  }
  return value;
}

export const SERVER_ID_PATTERN = /^[0-9a-f]{64}$/;
