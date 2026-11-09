import { describe, it, expect } from "vitest";
import { visibleRange, formatCell, computeColWidths, computeGridColumnsCss, MIN_COL_WIDTH_PX, MAX_COL_WIDTH_PX } from "./grid-core";

describe("visibleRange", () => {
  it("returns an empty range for no rows", () => {
    expect(visibleRange(0, 22, 400, 0)).toEqual({ start: 0, end: 0 });
  });

  it("covers the viewport plus overscan on both sides", () => {
    // scrollTop 220 / 22 = row 10 first visible; 400/22 ≈ 19 visible; overscan 6
    const range = visibleRange(220, 22, 400, 1000, 6);
    expect(range.start).toBe(4); // 10 - 6
    expect(range.end).toBe(10 + 19 + 6); // 35
  });

  it("clamps start at 0 and end at totalRows", () => {
    expect(visibleRange(0, 22, 400, 5, 6)).toEqual({ start: 0, end: 5 });
    const far = visibleRange(100000, 22, 400, 12, 6);
    expect(far.start).toBe(12);
    expect(far.end).toBe(12);
  });

  it("guards a zero row height", () => {
    expect(visibleRange(50, 0, 400, 100)).toEqual({ start: 0, end: 0 });
  });
});

describe("formatCell", () => {
  it("blanks null and undefined", () => {
    expect(formatCell(null)).toBe("");
    expect(formatCell(undefined)).toBe("");
  });

  it("stringifies primitives directly", () => {
    expect(formatCell(42)).toBe("42");
    expect(formatCell(true)).toBe("true");
    expect(formatCell("ada")).toBe("ada");
  });

  it("JSON-encodes objects and arrays", () => {
    expect(formatCell({ a: 1 })).toBe('{"a":1}');
    expect(formatCell([1, 2])).toBe("[1,2]");
  });
});

describe("computeColWidths", () => {
  it("returns empty array for empty columns", () => {
    expect(computeColWidths([], [])).toEqual([]);
  });

  it("assigns at least MIN_COL_WIDTH_PX for short column names and short data", () => {
    const cols = [{ name: "id" }, { name: "ok" }];
    const rows = [[1, true], [2, false]];
    const widths = computeColWidths(cols, rows);
    expect(widths).toHaveLength(2);
    expect(widths[0]).toBe(MIN_COL_WIDTH_PX);
    expect(widths[1]).toBe(MIN_COL_WIDTH_PX);
  });

  it("expands width proportionally for long column header names or long cell contents", () => {
    const cols = [{ name: "id" }, { name: "very_long_descriptive_column_header_name" }];
    const rows = [
      [1, "short"],
      [2, "another short string"],
    ];
    const widths = computeColWidths(cols, rows);
    expect(widths[0]).toBe(MIN_COL_WIDTH_PX);
    expect(widths[1]).toBeGreaterThan(MIN_COL_WIDTH_PX);
    // 40 chars * 8.5 + 24 = 364
    expect(widths[1]).toBeGreaterThanOrEqual(300);
  });

  it("expands width based on cell contents even if header is short", () => {
    const cols = [{ name: "url" }];
    const rows = [
      ["https://github.com/fsprojects/FSharpQueryStudio/blob/main/docs/architecture-overview.md"],
    ];
    const widths = computeColWidths(cols, rows);
    expect(widths[0]).toBeGreaterThan(300);
  });

  it("clamps width at MAX_COL_WIDTH_PX for extremely long text", () => {
    const cols = [{ name: "notes" }];
    const rows = [["a".repeat(2000)]];
    const widths = computeColWidths(cols, rows);
    expect(widths[0]).toBe(MAX_COL_WIDTH_PX);
  });
});

describe("computeGridColumnsCss", () => {
  it("produces fallback for empty widths", () => {
    expect(computeGridColumnsCss([])).toEqual({ template: "1fr", totalWidth: 0 });
  });

  it("formats grid template with minmax and calculates totalWidth", () => {
    const widths = [100, 200, 150];
    const res = computeGridColumnsCss(widths);
    expect(res.totalWidth).toBe(450);
    expect(res.template).toBe("minmax(100px, 1fr) minmax(200px, 1fr) minmax(150px, 1fr)");
  });
});
