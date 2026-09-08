import { readUnionTag, type Column, type ResultEnvelope } from "../protocol";
import { buildTablePayload } from "./table-payload";
import type { TablePayload } from "../renderer/types";

/**
 * A flat, VS Code-independent description of one cell output item. The
 * controller turns each spec into a `vscode.NotebookCellOutputItem`;
 * keeping this pure lets the mapping be unit-tested without the VS Code
 * runtime.
 */
export type OutputItemSpec =
  | { kind: "text"; text: string }
  | { kind: "error"; name: string; message: string }
  | { kind: "table-json"; payload: TablePayload };

const MAX_CELL_WIDTH = 60;
/** Cap on rows rendered into the text grid, independent of `isTruncated`. */
const MAX_RENDERED_ROWS = 500;

function renderCell(value: unknown): string {
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

function clip(text: string, width: number): string {
  return text.length > width ? text.slice(0, Math.max(0, width - 1)) + "…" : text;
}

export function formatTextTable(
  schema: Column[],
  rows: unknown[][],
  elapsedMs: number,
  isTruncated: boolean,
): string {
  const headers = schema.map((c) => c.name);
  // Only the rendered slice is materialised — a huge `rows` (Results.table
  // pushes the full set with `isTruncated: false`) must not be mapped in full.
  const body = rows.slice(0, MAX_RENDERED_ROWS).map((row) => row.map(renderCell));
  // `reduce`, not `Math.max(...spread)` — a spread of one argument per row
  // throws `RangeError: Maximum call stack size exceeded` around ~65k rows.
  const widths = headers.map((header, i) =>
    Math.min(
      MAX_CELL_WIDTH,
      body.reduce((max, row) => Math.max(max, (row[i] ?? "").length), header.length),
    ),
  );
  const line = (cells: string[]) =>
    cells.map((cell, i) => clip(cell, widths[i]).padEnd(widths[i])).join("  ").replace(/\s+$/, "");

  const out: string[] = [];
  out.push(line(headers));
  out.push(widths.map((w) => "─".repeat(w)).join("  "));
  for (const row of body) {
    out.push(line(row));
  }
  out.push("");
  out.push(`(${rows.length} row${rows.length === 1 ? "" : "s"} · ${elapsedMs}ms)`);
  if (rows.length > MAX_RENDERED_ROWS) {
    out.push(`(showing ${MAX_RENDERED_ROWS} of ${rows.length} rows)`);
  }
  if (isTruncated) {
    out.push("(truncated — more rows available in the grid view)");
  }
  return out.join("\n");
}

/** stdout/stderr lines joined in arrival order (stdout then stderr, per
 *  the worker's `Projection.toOutputLines`). This is FSI's declaration
 *  echo (`val result: … =`, `val rows: … = [| … |]`) plus any `printfn`
 *  output. */
function streamText(env: ResultEnvelope): string {
  return (env.output ?? []).map((line) => line.text).join("\n");
}

export function mapEnvelope(
  env: ResultEnvelope | null | undefined,
  opts?: { documentUri?: string },
): OutputItemSpec[] {
  if (env === null || env === undefined) {
    return [
      { kind: "error", name: "WorkerProtocolError", message: "The worker returned no result." },
    ];
  }

  // A failed cell surfaces only the error — echo/printf chatter must not
  // bury it.
  if (readUnionTag(env.kind) === "Error") {
    return [
      {
        kind: "error",
        name: "FSharpEvaluationError",
        message: env.displayText ?? "Evaluation failed.",
      },
    ];
  }

  const specs: OutputItemSpec[] = [];
  let bodyText: string;

  switch (readUnionTag(env.kind)) {
    case "Table":
      if (opts?.documentUri) {
        specs.push({ kind: "table-json", payload: buildTablePayload(env, opts.documentUri) });
      }
      bodyText = formatTextTable(env.schema ?? [], env.rows ?? [], env.elapsedMs, env.isTruncated);
      break;
    case "NoValue":
      bodyText = typeof env.displayText === "string" ? env.displayText : "";
      break;
    default:
      bodyText = env.displayText ?? "";
      break;
  }

  // FSI's declaration echo and any `printfn` output are folded into the
  // `text/plain` item, never emitted as `application/vnd.code.notebook.stdout`.
  // The stdout mime sits near the top of VS Code's default output display
  // order — above a custom renderer — so a stdout item would always be the
  // cell's default view and hide the grid. As `text/plain` this content
  // ranks *below* the `application/x-fsquery-table+json` renderer: the
  // grid shows by default, the raw echo is one click away in the output's
  // presentation picker.
  const text = [streamText(env), bodyText].filter((s) => s.length > 0).join("\n\n");
  if (text.length > 0) {
    specs.push({ kind: "text", text });
  }

  return specs;
}
