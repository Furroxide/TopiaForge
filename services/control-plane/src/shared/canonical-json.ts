/**
 * Canonical JSON for signed and shared documents (docs/internal/MultiplayerLiveSessions.md, section 2): a subset of
 * RFC 8785 that the game, the launcher and the control plane all produce byte for byte. Objects, arrays, strings,
 * booleans, null and integers within plus or minus 2^53 - 1; keys of 1 to 64 ASCII letters and digits in ordinal
 * order; no whitespace; strings escape only the quotation mark, the backslash and control characters.
 *
 * Every request body and every response body of the control plane is canonical JSON. A reader accepts a document only
 * if writing what it parsed gives back the same bytes, so a document has one meaning everywhere.
 */
import { bytesEqual, decodeUtf8Strict, utf8, type Bytes } from "./wire-text";

export type JsonValue = null | boolean | number | string | readonly JsonValue[] | JsonObject;
export interface JsonObject {
  readonly [key: string]: JsonValue;
}

export const MAXIMUM_SAFE_INTEGER = 9_007_199_254_740_991;
export const MAXIMUM_DEPTH = 16;
const MAXIMUM_KEY_LENGTH = 64;
const KEY_PATTERN = /^[A-Za-z0-9]+$/;

export class CanonicalJsonError extends Error {
  override readonly name = "CanonicalJsonError";
}

export type ParseResult = { readonly ok: true; readonly value: JsonValue } | { readonly ok: false; readonly error: string };

export function isKey(key: string): boolean {
  return key.length > 0 && key.length <= MAXIMUM_KEY_LENGTH && KEY_PATTERN.test(key);
}

export function isJsonObject(value: JsonValue | undefined): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

/** Writes `value` as canonical JSON text. Throws `CanonicalJsonError` for anything canonical JSON cannot represent. */
export function stringify(value: JsonValue): string {
  const parts: string[] = [];
  write(parts, value, 0);
  return parts.join("");
}

export function serialize(value: JsonValue): Bytes {
  return utf8(stringify(value));
}

/**
 * Parses `bytes` and accepts the document only when it is already canonical. Never throws for malformed input; the
 * error text is for tests and must never reach a response.
 */
export function parse(bytes: Uint8Array, maximumBytes: number): ParseResult {
  if (bytes.length === 0 || bytes.length > maximumBytes) {
    return { ok: false, error: `The document is empty or larger than ${maximumBytes} bytes.` };
  }
  const text = decodeUtf8Strict(bytes);
  if (text === null) return { ok: false, error: "The document is not valid UTF-8." };
  try {
    const reader = new Reader(text);
    const value = reader.readValue(0);
    if (!reader.atEnd) throw new CanonicalJsonError("Unexpected content after the document.");
    if (!bytesEqual(serialize(value), bytes)) throw new CanonicalJsonError("The document is not in canonical form.");
    return { ok: true, value };
  } catch (error) {
    if (error instanceof CanonicalJsonError) return { ok: false, error: error.message };
    throw error;
  }
}

function write(parts: string[], value: JsonValue, depth: number): void {
  if (depth > MAXIMUM_DEPTH) throw new CanonicalJsonError("The document nests too deeply.");
  if (value === null) {
    parts.push("null");
  } else if (typeof value === "boolean") {
    parts.push(value ? "true" : "false");
  } else if (typeof value === "number") {
    if (!Number.isSafeInteger(value)) throw new CanonicalJsonError("Only integers within the safe range are allowed.");
    parts.push(Object.is(value, -0) ? "0" : String(value));
  } else if (typeof value === "string") {
    writeString(parts, value);
  } else if (Array.isArray(value)) {
    parts.push("[");
    value.forEach((item, index) => {
      if (index > 0) parts.push(",");
      write(parts, item, depth + 1);
    });
    parts.push("]");
  } else {
    const object = value as JsonObject;
    const keys = Object.keys(object).sort();
    parts.push("{");
    keys.forEach((key, index) => {
      if (!isKey(key)) throw new CanonicalJsonError("A key is 1 to 64 ASCII letters and digits.");
      if (index > 0) parts.push(",");
      writeString(parts, key);
      parts.push(":");
      write(parts, object[key]!, depth + 1);
    });
    parts.push("}");
  }
}

function writeString(parts: string[], value: string): void {
  let text = '"';
  for (let index = 0; index < value.length; index++) {
    const code = value.charCodeAt(index);
    switch (code) {
      case 0x22:
        text += '\\"';
        break;
      case 0x5c:
        text += "\\\\";
        break;
      case 0x08:
        text += "\\b";
        break;
      case 0x0c:
        text += "\\f";
        break;
      case 0x0a:
        text += "\\n";
        break;
      case 0x0d:
        text += "\\r";
        break;
      case 0x09:
        text += "\\t";
        break;
      default:
        if (code < 0x20) {
          text += "\\u00" + code.toString(16).padStart(2, "0");
        } else if (code >= 0xd800 && code <= 0xdbff) {
          const next = value.charCodeAt(index + 1);
          if (!(next >= 0xdc00 && next <= 0xdfff)) throw new CanonicalJsonError("A string holds an unpaired surrogate.");
          text += value[index]! + value[index + 1]!;
          index++;
        } else if (code >= 0xdc00 && code <= 0xdfff) {
          throw new CanonicalJsonError("A string holds an unpaired surrogate.");
        } else {
          text += value[index]!;
        }
    }
  }
  parts.push(text + '"');
}

class Reader {
  private position = 0;

  constructor(private readonly text: string) {}

  get atEnd(): boolean {
    return this.position === this.text.length;
  }

  readValue(depth: number): JsonValue {
    if (depth > MAXIMUM_DEPTH) throw new CanonicalJsonError("The document nests too deeply.");
    if (this.atEnd) throw new CanonicalJsonError("The document ended early.");
    switch (this.text[this.position]) {
      case "{":
        return this.readObject(depth);
      case "[":
        return this.readArray(depth);
      case '"':
        return this.readString();
      case "t":
        this.expect("true");
        return true;
      case "f":
        this.expect("false");
        return false;
      case "n":
        this.expect("null");
        return null;
      default:
        return this.readInteger();
    }
  }

  private readObject(depth: number): JsonObject {
    this.position++;
    // A null prototype keeps member names from ever meaning anything to the runtime.
    const result: Record<string, JsonValue> = Object.create(null) as Record<string, JsonValue>;
    if (this.tryConsume("}")) return result;
    for (;;) {
      if (this.atEnd || this.text[this.position] !== '"') throw new CanonicalJsonError("An object key must be a string.");
      const key = this.readString();
      if (!isKey(key)) throw new CanonicalJsonError("An object key must be 1 to 64 ASCII letters and digits.");
      if (Object.hasOwn(result, key)) throw new CanonicalJsonError("An object repeats a key.");
      if (!this.tryConsume(":")) throw new CanonicalJsonError("An object key must be followed by a colon.");
      result[key] = this.readValue(depth + 1);
      if (this.tryConsume("}")) return result;
      if (!this.tryConsume(",")) throw new CanonicalJsonError("Object members must be separated by commas.");
    }
  }

  private readArray(depth: number): JsonValue[] {
    this.position++;
    const items: JsonValue[] = [];
    if (this.tryConsume("]")) return items;
    for (;;) {
      items.push(this.readValue(depth + 1));
      if (this.tryConsume("]")) return items;
      if (!this.tryConsume(",")) throw new CanonicalJsonError("Array items must be separated by commas.");
    }
  }

  private readString(): string {
    this.position++;
    let value = "";
    for (;;) {
      if (this.atEnd) throw new CanonicalJsonError("A string is not terminated.");
      const character = this.text[this.position++]!;
      if (character === '"') break;
      if (character.charCodeAt(0) < 0x20) throw new CanonicalJsonError("A string holds a raw control character.");
      if (character !== "\\") {
        value += character;
        continue;
      }
      if (this.atEnd) throw new CanonicalJsonError("A string ends inside an escape.");
      const escape = this.text[this.position++]!;
      switch (escape) {
        case '"':
          value += '"';
          break;
        case "\\":
          value += "\\";
          break;
        case "/":
          value += "/";
          break;
        case "b":
          value += "\b";
          break;
        case "f":
          value += "\f";
          break;
        case "n":
          value += "\n";
          break;
        case "r":
          value += "\r";
          break;
        case "t":
          value += "\t";
          break;
        case "u":
          value += this.readUnicodeEscape();
          break;
        default:
          throw new CanonicalJsonError("A string holds an unknown escape.");
      }
    }
    for (let index = 0; index < value.length; index++) {
      const code = value.charCodeAt(index);
      if (code >= 0xd800 && code <= 0xdbff) {
        const next = value.charCodeAt(index + 1);
        if (!(next >= 0xdc00 && next <= 0xdfff)) throw new CanonicalJsonError("A string holds an unpaired surrogate.");
        index++;
      } else if (code >= 0xdc00 && code <= 0xdfff) {
        throw new CanonicalJsonError("A string holds an unpaired surrogate.");
      }
    }
    return value;
  }

  private readUnicodeEscape(): string {
    if (this.position + 4 > this.text.length) throw new CanonicalJsonError("A unicode escape is truncated.");
    const digits = this.text.slice(this.position, this.position + 4);
    if (!/^[0-9A-Fa-f]{4}$/.test(digits)) throw new CanonicalJsonError("A unicode escape holds a non-hexadecimal digit.");
    this.position += 4;
    return String.fromCharCode(parseInt(digits, 16));
  }

  private readInteger(): number {
    const start = this.position;
    if (this.tryConsume("-") && this.atEnd) throw new CanonicalJsonError("A number is truncated.");
    if (this.atEnd || !isDigit(this.text[this.position]!)) throw new CanonicalJsonError("Unexpected character in the document.");
    if (this.text[this.position] === "0" && this.position + 1 < this.text.length && isDigit(this.text[this.position + 1]!)) {
      throw new CanonicalJsonError("A number has a leading zero.");
    }
    while (!this.atEnd && isDigit(this.text[this.position]!)) this.position++;
    if (!this.atEnd && (this.text[this.position] === "." || this.text[this.position] === "e" || this.text[this.position] === "E")) {
      throw new CanonicalJsonError("Only integers are allowed.");
    }
    const digits = this.text.slice(start, this.position);
    const value = Number(digits);
    if (digits.length > 17 || !Number.isSafeInteger(value)) {
      throw new CanonicalJsonError("An integer is outside the exactly representable range.");
    }
    return value;
  }

  private expect(literal: string): void {
    if (!this.text.startsWith(literal, this.position)) throw new CanonicalJsonError("Unexpected literal in the document.");
    this.position += literal.length;
  }

  private tryConsume(character: string): boolean {
    if (this.atEnd || this.text[this.position] !== character) return false;
    this.position++;
    return true;
  }
}

function isDigit(character: string): boolean {
  return character >= "0" && character <= "9";
}
