import { describe, it, expect } from "vitest";
import { formatTextTable, mapEnvelope, type OutputItemSpec } from "./result";
import type { ResultEnvelope } from "../protocol";

function envelope(overrides: Partial<ResultEnvelope>): ResultEnvelope {
  return {
    kind: { Case: "NoValue" },
    displayText: null,
    schema: [],
    rows: [],
    isTruncated: false,
    continuationToken: null,
    elapsedMs: 3,
    output: [],
    ...overrides,
  };
}

describe("formatTextTable", () => {
  it("aligns headers and rows and reports the row count and elapsed time", () => {
    const text = formatTextTable(
      [
        { name: "id", displayType: "int" },
        { name: "name", displayType: "string" },
      ],
      [
        [1, "ada"],
        [2, "grace"],
      ],
      12,
      false,
    );
    const lines = text.split("\n");
    expect(lines[0]).toMatch(/^id\s+name\s*$/);
    expect(lines[2]).toMatch(/^1\s+ada\s*$/);
    expect(text).toContain("(2 rows · 12ms)");
    expect(text).not.toContain("truncated");
  });

  it("adds a truncation note when isTruncated", () => {
    const text = formatTextTable([{ name: "x", displayType: "int" }], [[1]], 4, true);
    expect(text).toContain("truncated");
    expect(text).toContain("grid view");
  });

  it("stringifies non-primitive cells and blanks nulls", () => {
    const text = formatTextTable(
      [{ name: "v", displayType: "obj" }],
      [[{ a: 1 }], [null]],
      1,
      false,
    );
    expect(text).toContain('{"a":1}');
  });

  it("uses '1 row' (singular) for a single row", () => {
    const text = formatTextTable([{ name: "x", displayType: "int" }], [[1]], 1, false);
    expect(text).toContain("(1 row · 1ms)");
  });

  it("caps the rendered rows at 500 and notes how many were hidden", () => {
    const rows = Array.from({ length: 1000 }, (_, i) => [i]);
    const text = formatTextTable([{ name: "n", displayType: "int" }], rows, 9, false);
    const lines = text.split("\n");
    expect(lines.length).toBeLessThanOrEqual(506);
    expect(text).toContain("(showing 500 of 1000 rows)");
    expect(text).toContain("(1000 rows · 9ms)");
  });

  it("does not blow the stack on a very large row set", () => {
    const rows = Array.from({ length: 100_000 }, (_, i) => [i]);
    expect(() =>
      formatTextTable([{ name: "n", displayType: "int" }], rows, 1, false),
    ).not.toThrow();
  });
});

const textOf = (spec: OutputItemSpec) => (spec as Extract<OutputItemSpec, { kind: "text" }>).text;

describe("mapEnvelope", () => {
  it("folds stdout/stderr into the single text spec, ahead of the value", () => {
    const specs = mapEnvelope(
      envelope({
        kind: { Case: "Scalar" },
        displayText: "42",
        output: [
          { stream: { Case: "Stdout" }, text: "working..." },
          { stream: { Case: "Stderr" }, text: "a warning" },
        ],
      }),
    );
    expect(specs.map((s) => s.kind)).toEqual(["text"]);
    expect(textOf(specs[0])).toBe("working...\na warning\n\n42");
  });

  it("never emits a stdout/stderr mime — echo lives in text/plain so it can't outrank the grid", () => {
    const specs = mapEnvelope(
      envelope({
        kind: { Case: "Table" },
        schema: [{ name: "n", displayType: "int" }],
        rows: [[1]],
        output: [{ stream: { Case: "Stdout" }, text: "val rows: obj array array = [| ... |]" }],
      }),
      { documentUri: "u" },
    );
    expect(specs.map((s) => s.kind)).toEqual(["table-json", "text"]);
    expect(textOf(specs[1])).toContain("val rows: obj array array");
    expect(textOf(specs[1])).toContain("(1 row · 3ms)");
  });

  it("maps an Error envelope to an error spec using displayText", () => {
    const specs = mapEnvelope(envelope({ kind: { Case: "Error" }, displayText: "boom at line 2" }));
    expect(specs).toEqual([
      { kind: "error", name: "FSharpEvaluationError", message: "boom at line 2" },
    ]);
  });

  it("maps an Error envelope with no displayText to a default message", () => {
    const specs = mapEnvelope(envelope({ kind: { Case: "Error" }, displayText: null }));
    expect((specs[0] as Extract<OutputItemSpec, { kind: "error" }>).message).toBe(
      "Evaluation failed.",
    );
  });

  it("renders a Table envelope as a text grid", () => {
    const specs = mapEnvelope(
      envelope({
        kind: { Case: "Table" },
        schema: [{ name: "n", displayType: "int" }],
        rows: [[1], [2], [3]],
        elapsedMs: 7,
      }),
    );
    expect(specs).toHaveLength(1);
    expect(specs[0].kind).toBe("text");
    expect((specs[0] as Extract<OutputItemSpec, { kind: "text" }>).text).toContain("(3 rows · 7ms)");
  });

  it("emits nothing for a NoValue envelope with no displayText", () => {
    expect(mapEnvelope(envelope({ kind: { Case: "NoValue" }, displayText: null }))).toEqual([]);
  });

  it("emits the displayText for a NoValue envelope that has one", () => {
    const specs = mapEnvelope(envelope({ kind: { Case: "NoValue" }, displayText: "(done)" }));
    expect(specs).toEqual([{ kind: "text", text: "(done)" }]);
  });

  it("falls back to a text spec for an unrecognised kind", () => {
    const specs = mapEnvelope(envelope({ kind: { Case: "Object" }, displayText: "{ X = 1 }" }));
    expect(specs).toEqual([{ kind: "text", text: "{ X = 1 }" }]);
  });

  it("emits a table-json spec before the text grid when a documentUri is given", () => {
    const specs = mapEnvelope(
      envelope({
        kind: { Case: "Table" },
        schema: [{ name: "n", displayType: "int" }],
        rows: [[1], [2], [3]],
        isTruncated: true,
        continuationToken: "tok",
        elapsedMs: 7,
      }),
      { documentUri: "file:///a.fsqnb" },
    );
    expect(specs.map((s) => s.kind)).toEqual(["table-json", "text"]);
    const json = specs[0] as Extract<OutputItemSpec, { kind: "table-json" }>;
    expect(json.payload.documentUri).toBe("file:///a.fsqnb");
    expect(json.payload.continuationToken).toBe("tok");
    expect(json.payload.rows).toHaveLength(3);
  });

  it("still emits only the text grid when no documentUri is given (F2 behavior)", () => {
    const specs = mapEnvelope(
      envelope({
        kind: { Case: "Table" },
        schema: [{ name: "n", displayType: "int" }],
        rows: [[1]],
        elapsedMs: 7,
      }),
    );
    expect(specs.map((s) => s.kind)).toEqual(["text"]);
  });

  it("emits only stdout as text when a printf cell has no value", () => {
    const specs = mapEnvelope(
      envelope({
        kind: { Case: "NoValue" },
        displayText: null,
        output: [{ stream: { Case: "Stdout" }, text: "hello" }],
      }),
    );
    expect(specs).toEqual([{ kind: "text", text: "hello" }]);
  });
});
