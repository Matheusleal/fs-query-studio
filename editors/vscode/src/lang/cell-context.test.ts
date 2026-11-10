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

  it("when no cells have executed yet, includes preceding document code cells in prefix", () => {
    const c1 = cell("c1", "let store = 42");
    const c2 = cell("c2", "store + 1");
    const r = buildAnalysisInput([c1, c2], c2);
    expect(r.source).toBe("let store = 42\nstore + 1");
    expect(r.cellLineOffset).toBe(1);
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

  it("excludes cells that executed after the target cell from the prefix", () => {
    const first = cell("first", "let a = 1", { executionOrder: 1 });
    const t = cell("t", "let b = 2", { executionOrder: 2 });
    const later = cell("later", "let c = 3", { executionOrder: 3 });
    const r = buildAnalysisInput([first, t, later], t, { line: 0, character: 0 });
    expect(r.source).toBe("let a = 1\nlet b = 2");
    expect(r.cellLineOffset).toBe(1);
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

import {
  analysisDocumentUri,
  mapDiagnosticIntoCell,
  versionFor,
  forgetVersion,
} from "./cell-context";
import type { WireDiagnostic } from "../protocol";

describe("analysisDocumentUri", () => {
  it("appends .fsx so FSAC treats the cell as an F# script", () => {
    const cellUri = "vscode-notebook-cell:///d%3A/x.fsqnb#W3s";
    expect(analysisDocumentUri(cellUri)).toBe(`${cellUri}.fsx`);
    expect(analysisDocumentUri(cellUri)).toBe("vscode-notebook-cell:///d%3A/x.fsqnb#W3s.fsx");
  });
});

function wire(over: Partial<WireDiagnostic> = {}): WireDiagnostic {
  return {
    message: "oops",
    severity: { Case: "Error" },
    startLine: 0,
    startColumn: 0,
    endLine: 0,
    endColumn: 1,
    code: null,
    ...over,
  };
}

describe("mapDiagnosticIntoCell", () => {
  it("shifts an in-cell diagnostic back by the prefix offset", () => {
    const d = mapDiagnosticIntoCell(wire({ startLine: 5, endLine: 5 }), 3, 10);
    expect(d).not.toBeNull();
    expect(d!.startLine).toBe(2);
    expect(d!.endLine).toBe(2);
  });

  it("drops a diagnostic that lands entirely above the cell (in the prefix)", () => {
    expect(mapDiagnosticIntoCell(wire({ startLine: 1, endLine: 1 }), 3, 10)).toBeNull();
  });

  it("drops a diagnostic that starts past the end of the cell", () => {
    expect(mapDiagnosticIntoCell(wire({ startLine: 20, endLine: 20 }), 3, 5)).toBeNull();
  });

  it("clamps a diagnostic straddling the prefix/cell boundary into range", () => {
    const d = mapDiagnosticIntoCell(wire({ startLine: 2, endLine: 4 }), 3, 5);
    expect(d).not.toBeNull();
    expect(d!.startLine).toBe(0); // 2 - 3 = -1 -> clamped to 0
    expect(d!.endLine).toBe(1); // 4 - 3 = 1
  });

  it("decodes the severity tag and carries the code through", () => {
    const d = mapDiagnosticIntoCell(
      wire({ startLine: 3, endLine: 3, severity: { Case: "Warning" }, code: "FS0025" }),
      3,
      5,
    );
    expect(d!.severity).toBe("Warning");
    expect(d!.code).toBe("FS0025");
  });
});

describe("versionFor / forgetVersion", () => {
  it("returns a stable number for an unchanged source and bumps on change", () => {
    forgetVersion("v1");
    const a = versionFor("v1", "let x = 1");
    expect(versionFor("v1", "let x = 1")).toBe(a);
    expect(versionFor("v1", "let x = 2")).toBeGreaterThan(a);
  });

  it("tracks URIs independently", () => {
    forgetVersion("vA");
    forgetVersion("vB");
    const a = versionFor("vA", "same");
    const b = versionFor("vB", "same");
    versionFor("vA", "changed");
    expect(versionFor("vB", "same")).toBe(b); // vB untouched by vA's bump
    expect(a).toBeDefined();
  });

  it("forgetVersion drops the entry so the next source counts as new", () => {
    versionFor("vC", "one");
    forgetVersion("vC");
    // no assertion on the exact number; just that it does not throw and returns a number
    expect(typeof versionFor("vC", "one")).toBe("number");
  });
});
