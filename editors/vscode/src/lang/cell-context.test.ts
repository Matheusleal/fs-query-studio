import { describe, it, expect } from "vitest";
import { buildAnalysisInput, type AnalysisCell } from "./cell-context";

function cell(
  uri: string,
  text: string,
  opts: { kind?: number; lang?: string; executionOrder?: number } = {},
): AnalysisCell {
  return {
    kind: opts.kind ?? 2,
    document: {
      uri: { toString: () => uri },
      languageId: opts.lang ?? "fsharp",
      getText: () => text,
      lineCount: text.split("\n").length,
      version: 1,
    },
    executionSummary:
      opts.executionOrder === undefined ? undefined : { executionOrder: opts.executionOrder },
  };
}

describe("buildAnalysisInput", () => {
  it("with no executed prefix, source is just the target text and offset is 0", () => {
    const t = cell("c1", "let x = 1");
    const r = buildAnalysisInput([t], t, { line: 0, character: 4 });
    expect(r.source).toBe("let x = 1");
    expect(r.cellLineOffset).toBe(0);
    expect(r.line).toBe(1); // 0-based 0 -> 1-based 1, + 0 prefix
    expect(r.column).toBe(4);
  });

  it("prepends executed fsharp code cells, newline-joined, and offsets the position", () => {
    const a = cell("a", "let a = 1\nlet b = 2", { executionOrder: 1 });
    const t = cell("t", "a + b");
    const r = buildAnalysisInput([a, t], t, { line: 0, character: 5 });
    expect(r.source).toBe("let a = 1\nlet b = 2\na + b");
    expect(r.cellLineOffset).toBe(2); // "let a = 1\nlet b = 2".split("\n").length === 2
    expect(r.line).toBe(3); // 0 + 1 + 2
    expect(r.column).toBe(5);
  });

  it("excludes markdown cells, non-fsharp cells, and never-executed cells", () => {
    const md = cell("md", "# title", { kind: 1, executionOrder: 1 });
    const csharp = cell("cs", "int x;", { lang: "csharp", executionOrder: 2 });
    const notRun = cell("nr", "let unused = 0");
    const ran = cell("r", "let y = 9", { executionOrder: 3 });
    const t = cell("t", "y");
    const r = buildAnalysisInput([md, csharp, notRun, ran, t], t, { line: 0, character: 1 });
    expect(r.source).toBe("let y = 9\ny");
    expect(r.cellLineOffset).toBe(1);
  });

  it("orders the prefix by executionOrder, not document order", () => {
    const first = cell("first", "let p = 1", { executionOrder: 2 }); // physically first, ran second
    const second = cell("second", "let q = 2", { executionOrder: 1 }); // physically second, ran first
    const t = cell("t", "p + q");
    const r = buildAnalysisInput([first, second, t], t, { line: 0, character: 0 });
    expect(r.source).toBe("let q = 2\nlet p = 1\np + q");
  });

  it("excludes the target cell from its own prefix even when it has executed", () => {
    const t = cell("t", "let z = 5", { executionOrder: 1 });
    const r = buildAnalysisInput([t], t, { line: 0, character: 0 });
    expect(r.source).toBe("let z = 5");
    expect(r.cellLineOffset).toBe(0);
  });

  it("returns line 0 / column 0 when no position is given (the check path)", () => {
    const a = cell("a", "let a = 1", { executionOrder: 1 });
    const t = cell("t", "a");
    const r = buildAnalysisInput([a, t], t);
    expect(r.line).toBe(0);
    expect(r.column).toBe(0);
    expect(r.source).toBe("let a = 1\na");
    expect(r.cellLineOffset).toBe(1);
  });

  it("offset is unaffected by a prefix cell whose text ends in a newline", () => {
    const a = cell("a", "let a = 1\n", { executionOrder: 1 }); // trailing newline
    const t = cell("t", "a");
    const r = buildAnalysisInput([a, t], t, { line: 0, character: 1 });
    // prefix text is "let a = 1\n" -> split -> ["let a = 1", ""] -> length 2
    expect(r.cellLineOffset).toBe(2);
    expect(r.source).toBe("let a = 1\n\na");
    expect(r.line).toBe(3); // 0 + 1 + 2
  });
});
