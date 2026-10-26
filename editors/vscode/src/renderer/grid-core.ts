/** Inclusive-start, exclusive-end row indices to mount for a scroll offset. */
export interface VisibleRange {
  start: number;
  end: number;
}

/**
 * Windowed-rendering math: which row indices need to be in the DOM for a
 * given scroll offset and geometry. `overscan` rows are kept on each side
 * so a fast scroll does not flash blank.
 */
export function visibleRange(
  scrollTop: number,
  rowHeight: number,
  viewportHeight: number,
  totalRows: number,
  overscan = 6,
): VisibleRange {
  if (totalRows <= 0 || rowHeight <= 0) {
    return { start: 0, end: 0 };
  }
  const first = Math.floor(scrollTop / rowHeight);
  const visibleCount = Math.ceil(viewportHeight / rowHeight);
  const start = Math.min(Math.max(0, first - overscan), totalRows);
  const end = Math.min(totalRows, first + visibleCount + overscan);
  return { start, end };
}

/**
 * Renders one cell value as text — the same rule as
 * `notebook/result.ts::renderCell`, duplicated because that module sits
 * behind a Node-only import and cannot enter the browser bundle.
 */
export function formatCell(value: unknown): string {
  if (value === null || value === undefined) {
    return "";
  }
  if (
    typeof value === "string" ||
    typeof value === "number" ||
    typeof value === "boolean" ||
    typeof value === "bigint"
  ) {
    return String(value);
  }
  return JSON.stringify(value);
}
