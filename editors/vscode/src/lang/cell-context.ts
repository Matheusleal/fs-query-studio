/**
 * Pure helpers for in-cell F# analysis. No `vscode` import — the shapes
 * below are the minimal slice of `vscode.NotebookCell` the logic needs,
 * so this module unit-tests with plain objects.
 */

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

  const prefixCells = cells
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
