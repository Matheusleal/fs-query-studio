import { readUnionTag, type Column, type ResultEnvelope } from "../protocol";

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
  | { kind: "error"; name: string; message: string };

const MAX_CELL_WIDTH = 60;

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
  const body = rows.map((row) => row.map(renderCell));
  const widths = headers.map((header, i) =>
    Math.min(
      MAX_CELL_WIDTH,
      Math.max(header.length, ...body.map((row) => (row[i] ?? "").length), 0),
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
  if (isTruncated) {
    out.push("(truncated — full grid and paging arrive in F3)");
  }
  return out.join("\n");
}

export function mapEnvelope(env: ResultEnvelope): OutputItemSpec[] {
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
    case "Table":
      specs.push({
        kind: "text",
        text: formatTextTable(env.schema ?? [], env.rows ?? [], env.elapsedMs, env.isTruncated),
      });
      break;
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
