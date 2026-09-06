import { describe, it, expect } from "vitest";
import { DiagnosticsManager, type DiagnosticsManagerDeps } from "./diagnostics";
import type { AnalysisCell } from "./cell-context";
import type { WorkerResponseFrame } from "../protocol";

function codeCell(uri: string, text: string, executionOrder?: number): AnalysisCell {
  return {
    kind: 2,
    document: {
      uri: { toString: () => uri },
      languageId: "fsharp",
      getText: () => text,
      lineCount: text.split("\n").length,
      version: 1,
    },
    executionSummary: executionOrder === undefined ? undefined : { executionOrder },
  };
}

/** A manual clock: `setTimer` records the callback; `flush()` runs it. */
function manualTimers() {
  let pending: Array<{ id: number; fn: () => void }> = [];
  let nextId = 1;
  return {
    setTimer: (fn: () => void) => {
      const id = nextId++;
      pending.push({ id, fn });
      return id;
    },
    clearTimer: (handle: unknown) => {
      pending = pending.filter((p) => p.id !== handle);
    },
    flush: () => {
      const run = pending;
      pending = [];
      run.forEach((p) => p.fn());
    },
    get count() {
      return pending.length;
    },
  };
}

function baseDeps(over: Partial<DiagnosticsManagerDeps> = {}): {
  deps: DiagnosticsManagerDeps;
  sends: number;
  published: Array<{ uri: string; count: number }>;
  cleared: string[];
} {
  let sends = 0;
  const published: Array<{ uri: string; count: number }> = [];
  const cleared: string[] = [];
  const okResponse: WorkerResponseFrame = { id: "1", result: { diagnostics: [] } };
  const deps: DiagnosticsManagerDeps = {
    getWorker: async () => ({
      send: async () => {
        sends++;
        return okResponse;
      },
    }),
    findNotebookOf: (uri) => [codeCell(uri, "let x = 1")],
    publish: (uri, diags) => published.push({ uri, count: diags.length }),
    clear: (uri) => cleared.push(uri),
    log: () => {},
    debounceMs: 300,
    ...over,
  };
  return { deps, get sends() { return sends; }, published, cleared };
}

describe("DiagnosticsManager", () => {
  it("coalesces rapid edits into one check after the debounce", async () => {
    const t = manualTimers();
    const b = baseDeps({ setTimer: t.setTimer, clearTimer: t.clearTimer });
    const mgr = new DiagnosticsManager(b.deps);

    mgr.onCellChanged("c1");
    mgr.onCellChanged("c1");
    mgr.onCellChanged("c1");
    expect(t.count).toBe(1); // only the latest timer is pending
    expect(b.sends).toBe(0);

    t.flush();
    await new Promise((r) => setImmediate(r)); // let the async check settle
    expect(b.sends).toBe(1);
    expect(b.published).toEqual([{ uri: "c1", count: 0 }]);
  });

  it("checks immediately on cell open, with no timer", async () => {
    const t = manualTimers();
    const b = baseDeps({ setTimer: t.setTimer, clearTimer: t.clearTimer });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellOpened("c1");
    await new Promise((r) => setImmediate(r));
    expect(t.count).toBe(0);
    expect(b.sends).toBe(1);
  });

  it("clears the cell and cancels a pending timer on close", () => {
    const t = manualTimers();
    const b = baseDeps({ setTimer: t.setTimer, clearTimer: t.clearTimer });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellChanged("c1");
    expect(t.count).toBe(1);
    mgr.onCellClosed("c1");
    expect(t.count).toBe(0);
    expect(b.cleared).toEqual(["c1"]);
  });

  it("publishes only diagnostics that map into the cell", async () => {
    const t = manualTimers();
    const b = baseDeps({
      setTimer: t.setTimer,
      clearTimer: t.clearTimer,
      findNotebookOf: (uri) => [
        codeCell("prefix", "let a = 1", 1),
        codeCell(uri, "a +\nlet b = 2"),
      ],
      getWorker: async () => ({
        send: async () => ({
          id: "1",
          result: {
            diagnostics: [
              // offset is 1 (prefix "let a = 1"); cell has 2 lines
              { message: "in cell", severity: { Case: "Error" }, startLine: 1, startColumn: 0, endLine: 1, endColumn: 3, code: null },
              { message: "in prefix", severity: { Case: "Error" }, startLine: 0, startColumn: 0, endLine: 0, endColumn: 1, code: null },
            ],
          },
        }),
      }),
    });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellChanged("c1");
    t.flush();
    await new Promise((r) => setImmediate(r));
    expect(b.published).toEqual([{ uri: "c1", count: 1 }]);
  });

  it("does not publish (and does not throw) when the worker is unavailable", async () => {
    const t = manualTimers();
    const logs: string[] = [];
    const b = baseDeps({
      setTimer: t.setTimer,
      clearTimer: t.clearTimer,
      log: (l) => logs.push(l),
      getWorker: async () => {
        throw new Error("boom");
      },
    });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellChanged("c1");
    t.flush();
    await new Promise((r) => setImmediate(r));
    expect(b.published).toEqual([]);
  });

  it("does nothing when findNotebookOf returns undefined", async () => {
    const t = manualTimers();
    const b = baseDeps({
      setTimer: t.setTimer,
      clearTimer: t.clearTimer,
      findNotebookOf: () => undefined,
    });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellChanged("c1");
    t.flush();
    await new Promise((r) => setImmediate(r));
    expect(b.published).toEqual([]);
  });

  it("cancels every pending timer on dispose", () => {
    const t = manualTimers();
    const b = baseDeps({ setTimer: t.setTimer, clearTimer: t.clearTimer });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellChanged("c1");
    mgr.onCellChanged("c2");
    expect(t.count).toBe(2);
    mgr.dispose();
    expect(t.count).toBe(0);
  });

  it("sends a .fsx-suffixed documentUri but keeps the raw cell URI for publish", async () => {
    const t = manualTimers();
    const requests: Array<{ method: string; params: { documentUri: string } }> = [];
    const b = baseDeps({
      setTimer: t.setTimer,
      clearTimer: t.clearTimer,
      getWorker: async () => ({
        send: async (req: { method: string; params: unknown }) => {
          requests.push(req as { method: string; params: { documentUri: string } });
          return { id: "1", result: { diagnostics: [] } } as WorkerResponseFrame;
        },
      }),
    });
    const mgr = new DiagnosticsManager(b.deps);
    mgr.onCellChanged("cell-uri");
    t.flush();
    await new Promise((r) => setImmediate(r));

    expect(requests).toHaveLength(1);
    expect(requests[0].method).toBe("analysis/check");
    expect(requests[0].params.documentUri).toBe("cell-uri.fsx");
    expect(requests[0].params.documentUri.endsWith(".fsx")).toBe(true);
    // publish / DiagnosticCollection key stays the real, un-suffixed cell URI
    expect(b.published).toEqual([{ uri: "cell-uri", count: 0 }]);
  });

  it("does not publish onto a cell closed while the check RPC is in flight", async () => {
    const t = manualTimers();
    let resolveSend: (frame: WorkerResponseFrame) => void = () => {};
    const b = baseDeps({
      setTimer: t.setTimer,
      clearTimer: t.clearTimer,
      getWorker: async () => ({
        send: () =>
          new Promise<WorkerResponseFrame>((resolve) => {
            resolveSend = resolve;
          }),
      }),
    });
    const mgr = new DiagnosticsManager(b.deps);

    mgr.onCellChanged("c1");
    t.flush();
    await new Promise((r) => setImmediate(r)); // reach the pending send()

    mgr.onCellClosed("c1"); // cell gone before the worker replies
    resolveSend({ id: "1", result: { diagnostics: [] } } as WorkerResponseFrame);
    await new Promise((r) => setImmediate(r));

    expect(b.published).toEqual([]);
    expect(b.cleared).toEqual(["c1"]);
  });
});
