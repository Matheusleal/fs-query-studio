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
  | { kind: "stdout"; text: string }
  | { kind: "stderr"; text: string }
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

/**
 * Split a flat `mapEnvelope` spec list into one group per
 * `vscode.NotebookCellOutput`. VS Code renders exactly one representation
 * per output, chosen by mime priority — so stdout/stderr specs sharing an
 * output with the result's `table-json`/`text` specs make VS Code pick
 * the stdout mime and hide the result (it stays reachable only through
 * the output's "Change presentation" menu). Every stream run therefore
 * becomes its own group; the result representations stay together in one
 * trailing group where being alternatives of one value is correct.
 * Consecutive same-stream specs are merged into a single item so a
 * multi-line FSI echo shows as one block, not one output per line.
 */
export function groupOutputs(specs: OutputItemSpec[]): OutputItemSpec[][] {
  const streamGroups: OutputItemSpec[][] = [];
  const valueGroup: OutputItemSpec[] = [];

  for (const spec of specs) {
    if (spec.kind !== "stdout" && spec.kind !== "stderr") {
      valueGroup.push(spec);
      continue;
    }
    const prev = streamGroups.at(-1)?.[0];
    if (prev && (prev.kind === "stdout" || prev.kind === "stderr") && prev.kind === spec.kind) {
      prev.text = `${prev.text}\n${spec.text}`;
    } else {
      streamGroups.push([{ kind: spec.kind, text: spec.text }]);
    }
  }

  return valueGroup.length > 0 ? [...streamGroups, valueGroup] : streamGroups;
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

  const specs: OutputItemSpec[] = [];

  for (const line of env.output ?? []) {
    const stream = readUnionTag(line.stream) === "Stderr" ? "stderr" : "stdout";
    specs.push({ kind: stream, text: line.text });
  }

  switch (readUnionTag(env.kind)) {
    case "Error":
      specs.push({
        kind: "error",
        name: "FSharpEvaluationError",
        message: env.displayText ?? "Evaluation failed.",
      });
      break;
    case "Table": {
      if (opts?.documentUri) {
        specs.push({ kind: "table-json", payload: buildTablePayload(env, opts.documentUri) });
      }
      specs.push({
        kind: "text",
        text: formatTextTable(env.schema ?? [], env.rows ?? [], env.elapsedMs, env.isTruncated),
      });
      break;
    }
    case "NoValue":
      if (typeof env.displayText === "string" && env.displayText.length > 0) {
        specs.push({ kind: "text", text: env.displayText });
      }
      break;
    default:
      specs.push({ kind: "text", text: env.displayText ?? "" });
      break;
  }

  return specs;
}
