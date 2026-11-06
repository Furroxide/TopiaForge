import { describe, expect, it } from "vitest";
import { MAXIMUM_DEPTH, parse, stringify, type JsonValue } from "../src/shared/canonical-json";
import { utf8 } from "../src/shared/wire-text";

function accepts(text: string): JsonValue {
  const result = parse(utf8(text), 1 << 20);
  if (!result.ok) throw new Error(`refused ${JSON.stringify(text)}: ${result.error}`);
  return result.value;
}

function refuses(bytes: string | Uint8Array): void {
  const result = parse(typeof bytes === "string" ? utf8(bytes) : bytes, 1 << 20);
  expect(result.ok, `accepted ${typeof bytes === "string" ? JSON.stringify(bytes) : "bytes"}`).toBe(false);
}

describe("canonical JSON writing", () => {
  it("sorts keys ordinally and writes no whitespace", () => {
    expect(stringify({ b: 1, a: [true, false, null], B: "x", a0: {} })).toBe('{"B":"x","a":[true,false,null],"a0":{},"b":1}');
  });

  it("escapes only the quote, the backslash and control characters", () => {
    expect(stringify("\"\\/\b\f\n\r\t\u0000\u001f\u007f é 東京 😀")).toBe('"\\"\\\\/\\b\\f\\n\\r\\t\\u0000\\u001f\u007f é 東京 😀"');
  });

  it("writes integers within the safe range and nothing else", () => {
    expect(stringify([0, -1, 9007199254740991, -9007199254740991])).toBe("[0,-1,9007199254740991,-9007199254740991]");
    expect(stringify(-0)).toBe("0");
    for (const value of [1.5, Number.NaN, Number.POSITIVE_INFINITY, 9007199254740992]) expect(() => stringify(value)).toThrow();
  });

  it("refuses what canonical JSON cannot hold", () => {
    expect(() => stringify("\ud800")).toThrow();
    expect(() => stringify("\udc00x")).toThrow();
    expect(() => stringify({ "": 1 })).toThrow();
    expect(() => stringify({ under_score: 1 })).toThrow();
    expect(() => stringify({ ["k".repeat(65)]: 1 })).toThrow();
    expect(() => stringify({ ["k".repeat(64)]: 1 })).not.toThrow();
  });

  it("limits nesting to the shared depth", () => {
    const nest = (depth: number): JsonValue => (depth === 0 ? [] : [nest(depth - 1)]);
    expect(() => stringify(nest(MAXIMUM_DEPTH))).not.toThrow();
    expect(() => stringify(nest(MAXIMUM_DEPTH + 1))).toThrow();
  });
});

describe("canonical JSON reading", () => {
  it("round-trips canonical documents", () => {
    const text = '{"a":[1,-2,"x\\n"],"b":{"c":null},"d":true}';
    expect(stringify(accepts(text))).toBe(text);
    expect(accepts('"\\u001f"')).toBe("\u001f");
  });

  it("accepts the deepest allowed nesting and refuses one level more", () => {
    accepts("[".repeat(MAXIMUM_DEPTH + 1) + "]".repeat(MAXIMUM_DEPTH + 1));
    refuses("[".repeat(MAXIMUM_DEPTH + 2) + "]".repeat(MAXIMUM_DEPTH + 2));
  });

  it("refuses anything that is not already canonical", () => {
    for (const text of [
      " {}",
      "{} ",
      '{"a": 1}',
      '{"b":1,"a":2}',
      '{"a":1,"a":1}',
      "01",
      "1.0",
      "1e3",
      "-0",
      "9007199254740992",
      "-9007199254740992",
      '"\\/"',
      '"\\u0041"',
      '"\\u001F"',
      '"\\u00e9"',
      '"\\ud800"',
      '"\\udc00"',
      '"a\u0001"',
      '"\\x"',
      '"unterminated',
      "tru",
      "nul",
      "[1,]",
      "[1 2]",
      '{"a":1,}',
      '{"a"}',
      '{"_":1}',
      '{"é":1}',
      "{}{}",
      "",
    ]) {
      refuses(text);
    }
  });

  it("refuses malformed UTF-8, a byte order mark and oversized documents", () => {
    refuses(Uint8Array.of(0x22, 0xff, 0x22));
    refuses(Uint8Array.of(0x22, 0xed, 0xa0, 0x80, 0x22));
    refuses(Uint8Array.of(0xef, 0xbb, 0xbf, 0x7b, 0x7d));
    expect(parse(utf8('"abc"'), 4).ok).toBe(false);
    expect(parse(utf8('"abc"'), 5).ok).toBe(true);
  });
});
