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

export const MIN_COL_WIDTH_PX = 96;
export const MAX_COL_WIDTH_PX = 480;

/**
 * Estimates optimal column widths in pixels based on header labels and sampled
 * row cell contents. Clamped between `minWidth` (default 96px) and `maxWidth` (default 480px).
 */
export function computeColWidths(
  columns: { name: string }[],
  rows: unknown[][],
  minWidth = MIN_COL_WIDTH_PX,
  maxWidth = MAX_COL_WIDTH_PX,
): number[] {
  const rowMaxCols = rows.slice(0, 100).reduce((max, r) => (Array.isArray(r) ? Math.max(max, r.length) : max), 0);
  const colCount = Math.max(columns.length, rowMaxCols, 0);
  if (colCount === 0) {
    return [];
  }
  const widths: number[] = new Array(colCount).fill(minWidth);
  const sampleLimit = Math.min(rows.length, 100);

  for (let c = 0; c < colCount; c++) {
    const colName = columns[c]?.name ?? "";
    let maxChars = colName.length;
    for (let r = 0; r < sampleLimit; r++) {
      const v = rows[r]?.[c];
      if (v !== null && v !== undefined) {
        const text = formatCell(v);
        if (text.length > maxChars) {
          maxChars = text.length;
        }
      }
    }
    // Average proportional character width ~8.5px + 24px padding + border buffer
    const estimated = Math.ceil(maxChars * 8.5) + 24;
    widths[c] = Math.max(minWidth, Math.min(maxWidth, estimated));
  }

  return widths;
}

export interface GridColumnsLayout {
  template: string;
  totalWidth: number;
}

/**
 * Produces the CSS Grid `grid-template-columns` declaration and total min width.
 * Uses `minmax(W_c px, 1fr)` so columns proportionally stretch when the table
 * is narrower than the viewport, and lock to fixed pixel widths with horizontal
 * scrolling when the table exceeds the viewport.
 */
export function computeGridColumnsCss(widths: number[]): GridColumnsLayout {
  if (widths.length === 0) {
    return { template: "1fr", totalWidth: 0 };
  }
  const totalWidth = widths.reduce((sum, w) => sum + w, 0);
  const template = widths.map((w) => `minmax(${w}px, 1fr)`).join(" ");
  return { template, totalWidth };
}
