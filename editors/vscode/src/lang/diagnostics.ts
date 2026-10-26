import {
  analysisDocumentUri,
  buildAnalysisInput,
  forgetVersion,
  mapDiagnosticIntoCell,
  versionFor,
  type AnalysisCell,
  type CellDiagnostic,
} from "./cell-context";
import { checkAnalysis, type WorkerLike } from "./analysis-client";

export interface DiagnosticsManagerDeps {
  getWorker: () => Promise<WorkerLike>;
  /** The cells of the notebook that owns `cellUri`, or `undefined` if none. */
  findNotebookOf: (cellUri: string) => readonly AnalysisCell[] | undefined;
  /** Push the mapped diagnostics onto whatever collection the host owns. */
  publish: (cellUri: string, diags: CellDiagnostic[]) => void;
  /** Drop all diagnostics for a cell. */
  clear: (cellUri: string) => void;
  log: (line: string) => void;
  debounceMs?: number;
  setTimer?: (fn: () => void, ms: number) => unknown;
  clearTimer?: (handle: unknown) => void;
}

const DEFAULT_DEBOUNCE_MS = 300;

/**
 * Owns the per-cell debounce and the check → map → publish flow. Holds no
 * `vscode` reference: the host wires real events and a real
 * `DiagnosticCollection` in via `deps`.
 */
export class DiagnosticsManager {
  private readonly deps: DiagnosticsManagerDeps;
  private readonly debounceMs: number;
  private readonly setTimer: (fn: () => void, ms: number) => unknown;
  private readonly clearTimer: (handle: unknown) => void;
  private readonly timers = new Map<string, unknown>();
  private readonly generations = new Map<string, number>();

  constructor(deps: DiagnosticsManagerDeps) {
    this.deps = deps;
    this.debounceMs = deps.debounceMs ?? DEFAULT_DEBOUNCE_MS;
    this.setTimer = deps.setTimer ?? ((fn, ms) => setTimeout(fn, ms));
    this.clearTimer = deps.clearTimer ?? ((h) => clearTimeout(h as ReturnType<typeof setTimeout>));
  }

  onCellChanged(cellUri: string): void {
    const existing = this.timers.get(cellUri);
    if (existing !== undefined) {
      this.clearTimer(existing);
    }
    const handle = this.setTimer(() => {
      this.timers.delete(cellUri);
      void this.runCheck(cellUri);
    }, this.debounceMs);
    this.timers.set(cellUri, handle);
  }

  onCellOpened(cellUri: string): void {
    void this.runCheck(cellUri);
  }

  onCellClosed(cellUri: string): void {
    const existing = this.timers.get(cellUri);
    if (existing !== undefined) {
      this.clearTimer(existing);
      this.timers.delete(cellUri);
    }
    this.generations.set(cellUri, (this.generations.get(cellUri) ?? 0) + 1);
    forgetVersion(cellUri);
    this.deps.clear(cellUri);
  }

  dispose(): void {
    for (const handle of this.timers.values()) {
      this.clearTimer(handle);
    }
    this.timers.clear();
  }

  private async runCheck(cellUri: string): Promise<void> {
    const cells = this.deps.findNotebookOf(cellUri);
    if (!cells) {
      return;
    }
    const target = cells.find((c) => c.document.uri.toString() === cellUri);
    if (!target) {
      return;
    }

    const { source, cellLineOffset } = buildAnalysisInput(cells, target);
    const version = versionFor(cellUri, source);
    const generation = this.generations.get(cellUri) ?? 0;

    let wire;
    try {
      const worker = await this.deps.getWorker();
      wire = await checkAnalysis(worker, {
        documentUri: analysisDocumentUri(cellUri),
        version,
        source,
      });
    } catch (err) {
      this.deps.log(`diagnostics: check for ${cellUri} failed: ${(err as Error).message}`);
      return;
    }

    // The cell was closed or deleted while the RPC was in flight — a late
    // publish would re-create a diagnostic entry that nothing ever clears.
    if ((this.generations.get(cellUri) ?? 0) !== generation) {
      return;
    }

    const mapped = wire
      .map((d) => mapDiagnosticIntoCell(d, cellLineOffset, target.document.lineCount))
      .filter((d): d is CellDiagnostic => d !== null);
    this.deps.publish(cellUri, mapped);
  }
}
