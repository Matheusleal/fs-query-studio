/**
 * Pure helpers for in-cell F# analysis. No `vscode` import — the shapes
 * below are the minimal slice of `vscode.NotebookCell` the logic needs,
 * so this module unit-tests with plain objects.
 */

import { readSeverity, type WireDiagnostic, type WireSeverity } from "../protocol";

/** Equals `vscode.NotebookCellKind.Code`. */
export const CODE_CELL_KIND = 2;

export interface AnalysisCellDocument {
  uri: { toString(): string };
  languageId: string;
  getText(): string;
  lineCount: number;
  version: number;
}

export interface AnalysisCell {
  kind: number;
  document: AnalysisCellDocument;
  executionSummary?: { executionOrder?: number };
}

export interface AnalysisPosition {
  line: number;
  character: number;
}

export interface AnalysisInput {
  /** Concatenated prefix + target text, sent verbatim as `source`. */
  source: string;
  /** 1-based line for an interactive request; 0 when no position given. */
  line: number;
  /** 0-based column; 0 when no position given. */
  column: number;
  /** Lines the prefix occupies — subtract from returned diagnostic lines. */
  cellLineOffset: number;
}

function hasExecuted(cell: AnalysisCell): boolean {
  return cell.executionSummary?.executionOrder != null;
}

export function buildAnalysisInput(
  cells: readonly AnalysisCell[],
  target: AnalysisCell,
  position?: AnalysisPosition,
): AnalysisInput {
  const targetUri = target.document.uri.toString();
  const targetIndex = cells.findIndex((c) => c.document.uri.toString() === targetUri);

  const anyExecuted = cells.some((c) => c.kind === CODE_CELL_KIND && hasExecuted(c));

  const prefixCells = anyExecuted
    ? cells
        .filter(
          (c) =>
            c.kind === CODE_CELL_KIND &&
            c.document.languageId === "fsharp" &&
            c.document.uri.toString() !== targetUri &&
            hasExecuted(c),
        )
        .sort(
          (a, b) =>
            (a.executionSummary!.executionOrder ?? 0) - (b.executionSummary!.executionOrder ?? 0),
        )
    : cells.filter(
        (c, idx) =>
          c.kind === CODE_CELL_KIND &&
          c.document.languageId === "fsharp" &&
          c.document.uri.toString() !== targetUri &&
          (targetIndex === -1 || idx < targetIndex),
      );

  const prefixText = prefixCells.map((c) => c.document.getText()).join("\n");
  const targetText = target.document.getText();
  const source = prefixText === "" ? targetText : `${prefixText}\n${targetText}`;
  const cellLineOffset = prefixText === "" ? 0 : prefixText.split("\n").length;

  return {
    source,
    line: position ? position.line + 1 + cellLineOffset : 0,
    column: position ? position.character : 0,
    cellLineOffset,
  };
}

export interface CellDiagnostic {
  message: string;
  severity: WireSeverity;
  startLine: number;
  startColumn: number;
  endLine: number;
  endColumn: number;
  code: string | null;
}

function clamp(n: number, lo: number, hi: number): number {
  return Math.max(lo, Math.min(hi, n));
}

/**
 * Convert a `WireDiagnostic` (0-based against the concatenated source)
 * into a cell-local diagnostic. Returns `null` when the diagnostic
 * belongs to a prefix cell, not the target. A diagnostic straddling the
 * boundary is clamped into `[0, cellLineCount - 1]`.
 */
export function mapDiagnosticIntoCell(
  wire: WireDiagnostic,
  cellLineOffset: number,
  cellLineCount: number,
): CellDiagnostic | null {
  const startLine = wire.startLine - cellLineOffset;
  const endLine = wire.endLine - cellLineOffset;
  if (endLine < 0 || startLine >= cellLineCount) {
    return null;
  }
  const lastLine = Math.max(0, cellLineCount - 1);
  return {
    message: wire.message,
    severity: readSeverity(wire.severity),
    startLine: clamp(startLine, 0, lastLine),
    startColumn: wire.startColumn,
    endLine: clamp(endLine, 0, lastLine),
    endColumn: wire.endColumn,
    code: wire.code,
  };
}

const versions = new Map<string, { source: string; version: number }>();

/**
 * A monotonic per-cell version that changes whenever `source` differs
 * from the last value seen for `cellUri`. The worker keys FSAC's
 * in-memory document by `(documentUri, version)`, and the concatenated
 * source can change without the cell's own `document.version` moving, so
 * we cannot reuse that.
 */
export function versionFor(cellUri: string, source: string): number {
  const prev = versions.get(cellUri);
  if (prev && prev.source === source) {
    return prev.version;
  }
  const version = (prev?.version ?? 0) + 1;
  versions.set(cellUri, { source, version });
  return version;
}

export function forgetVersion(cellUri: string): void {
  versions.delete(cellUri);
}

/**
 * The `documentUri` to send to the worker's `analysis/*` methods for a
 * notebook cell. FSAC decides whether to run F# script analysis on a
 * document by its extension; a bare `vscode-notebook-cell:` URI yields
 * an extension-less synthetic path inside the worker (`FsacBridge.fsacUriFor`)
 * that FSAC will not parse. The worker treats `documentUri` purely as an
 * opaque identity, so a `.fsx` suffix is safe here and never surfaces to
 * VS Code — the real cell `Uri` stays the DiagnosticCollection key.
 */
export function analysisDocumentUri(cellUri: string): string {
  return `${cellUri}.fsx`;
}
