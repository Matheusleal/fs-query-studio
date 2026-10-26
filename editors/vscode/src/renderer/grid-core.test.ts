import { describe, it, expect } from "vitest";
import { visibleRange, formatCell } from "./grid-core";

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
