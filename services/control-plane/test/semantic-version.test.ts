/**
 * Version strings in descriptors: exactly the SemVer 2.0.0 forms, read in time that stays small however the string is
 * built. A pattern that can match one identifier several ways backtracks exponentially on a version of dot-separated
 * hyphen runs, and descriptors reach that check before their signature does.
 */
import { describe, expect, it } from "vitest";
import { isSemanticVersion } from "../src/shared/descriptor";
import { seededBytes } from "./support/runtime";

const MAXIMUM_VERSION_LENGTH = 64;
/** Every hostile check together, each made three times; a reader that backtracks spends seconds here. */
const HOSTILE_BUDGET_MILLISECONDS = 50;

const VALID = [
  "0.0.0",
  "1.2.3",
  "1.0.0-alpha",
  "1.0.0-alpha.1",
  "1.0.0-0.3.7",
  "1.0.0-x.7.z.92",
  "1.0.0-x-y-z.--",
  "1.0.0--",
  "1.0.0-0a",
  "1.0.0-00a",
  "1.0.0-a0",
  "1.0.0-0-",
  "1.0.0-123abc",
  "1.0.0-alpha+001",
  "1.0.0+20130313144700",
  "1.0.0-beta+exp.sha.5114f85",
  "1.0.0+21AF26D3----117B344092BD",
  "1.0.0-rc.1+build.1-2",
];

const INVALID = [
  "1.0.0-",
  "1.0.0-01",
  "1.0.0-00",
  "1.0.0-alpha..1",
  "1.0.0-alpha.",
  "1.0.0-.alpha",
  "1.0.0-alpha_beta",
  "1.0.0-alpha beta",
  "1.0.0-é",
  "1.0.0-a\n",
  "1.0.0+",
  "1.0.0+a..b",
  "1.0.0-a+",
  "1.0.0+a+b",
  "01.0.0",
  "1.00.0",
  "1.0",
  "1.0.0.0",
  "v1.0.0",
  " 1.0.0",
  "1.0.0 ",
];

const isDigit = (character: string): boolean => character >= "0" && character <= "9";
const isIdentifierCharacter = (character: string): boolean =>
  isDigit(character) || (character >= "A" && character <= "Z") || (character >= "a" && character <= "z") || character === "-";
const isNumber = (part: string): boolean => part === "0" || (part.length > 0 && part[0] !== "0" && [...part].every(isDigit));
const isIdentifier = (part: string): boolean => part.length > 0 && [...part].every(isIdentifierCharacter);

/** SemVer 2.0.0 read by hand, field by field: the reference the service's pattern must agree with. */
function reference(text: string): boolean {
  if (text.length > MAXIMUM_VERSION_LENGTH) return false;
  const plus = text.indexOf("+");
  const main = plus < 0 ? text : text.slice(0, plus);
  const build = plus < 0 ? null : text.slice(plus + 1);
  const dash = main.indexOf("-");
  const core = (dash < 0 ? main : main.slice(0, dash)).split(".");
  const pre = dash < 0 ? null : main.slice(dash + 1);
  if (core.length !== 3 || !core.every(isNumber)) return false;
  const preReleaseIdentifier = (id: string) => isIdentifier(id) && ([...id].some((character) => !isDigit(character)) || isNumber(id));
  if (pre !== null && !pre.split(".").every(preReleaseIdentifier)) return false;
  return build === null || build.split(".").every(isIdentifier);
}

/** "0.0.0-" and then runs of `run` hyphens between dots, cut to `length` characters, the last of them `end`. */
function hyphenRuns(run: number, length: number, end: string): string {
  const unit = `${"-".repeat(run)}.`;
  return `0.0.0-${unit.repeat(Math.ceil(length / unit.length))}`.slice(0, length - 1) + end;
}

describe("semantic versions", () => {
  it("accept exactly the SemVer 2.0.0 pre-release and build forms", () => {
    for (const text of VALID) expect(isSemanticVersion(text), text).toBe(true);
    for (const text of INVALID) expect(isSemanticVersion(text), JSON.stringify(text)).toBe(false);
    const longest = `1.0.0-${"a".repeat(MAXIMUM_VERSION_LENGTH - 6)}`;
    expect(isSemanticVersion(longest)).toBe(true);
    expect(isSemanticVersion(`${longest}a`)).toBe(false);
  });

  it("agree with a hand-written SemVer reader on every seeded case", () => {
    const random = seededBytes("semantic versions");
    const below = (count: number) => random(1)[0]! % count;
    const pick = (alphabet: string) => alphabet[below(alphabet.length)]!;
    // Identifiers of up to three characters, an empty one now and then, joined by dots after a "-" or a "+".
    const identifier = () => Array.from({ length: below(4) }, () => pick("0019aZ-")).join("");
    const part = (lead: string) => lead + Array.from({ length: 1 + below(3) }, identifier).join(".");
    const cores = ["0.0.0", "1.2.3", "10.20.30", "01.0.0", "1.0", "1.0.0.0"];
    const counts = { valid: 0, invalid: 0 };
    for (let index = 0; index < 4000; index++) {
      let text = cores[below(cores.length)]!;
      if (below(4) !== 0) text += part("-");
      if (below(3) === 0) text += part("+");
      if (below(8) === 0) text += pick(".+_ -");
      const expected = reference(text);
      expect(isSemanticVersion(text), text).toBe(expected);
      counts[expected ? "valid" : "invalid"]++;
    }
    expect(counts.valid).toBeGreaterThan(800);
    expect(counts.invalid).toBeGreaterThan(800);
  });

  it("refuse hostile versions at and beyond the length bound within a small time budget", () => {
    const hostile = [`0.0.0-${"-".repeat(57)}.`, `0.0.0-${"-".repeat(58)}.`];
    for (const run of [1, 2, 3, 4, 5, 6]) {
      for (const end of ["!", ".", "+"]) hostile.push(hyphenRuns(run, MAXIMUM_VERSION_LENGTH, end));
    }
    for (const length of [MAXIMUM_VERSION_LENGTH + 1, 200, 4096]) hostile.push(hyphenRuns(3, length, "!"), hyphenRuns(4, length, "."));
    expect(hostile.filter((text) => text.length === MAXIMUM_VERSION_LENGTH)).toHaveLength(19);
    isSemanticVersion("1.0.0-warm.up");
    const started = performance.now();
    const answers = hostile.flatMap((text) => [isSemanticVersion(text), isSemanticVersion(text), isSemanticVersion(text)]);
    const elapsed = performance.now() - started;
    expect(answers.every((answer) => answer === false)).toBe(true);
    expect(elapsed).toBeLessThan(HOSTILE_BUDGET_MILLISECONDS);
  });
});
