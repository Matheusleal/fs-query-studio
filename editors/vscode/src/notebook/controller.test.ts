import { describe, it, expect } from "vitest";
import * as vscode from "vscode";
import { createFsQueryController, restartKernel, type WorkerLike } from "./controller";
import type { ResultEnvelope, WorkerResponseFrame } from "../protocol";

function tableEnvelope(overrides: Partial<ResultEnvelope> = {}): ResultEnvelope {
  return {
    kind: { Case: "Table" },
    displayText: null,
    schema: [{ name: "n", displayType: "int" }],
    rows: [[1], [2]],
    isTruncated: false,
    continuationToken: null,
    elapsedMs: 5,
    output: [{ stream: { Case: "Stdout" }, text: "hi\n" }],
    ...overrides,
  };
}

const cell = (text: string) => ({ document: { getText: () => text } }) as never;
const notebook = (uri: string) => ({ uri: { toString: () => uri } }) as never;
const execs = (c: vscode.NotebookController) =>
  (c as unknown as { _executions: Array<Record<string, unknown>> })._executions;

describe("createFsQueryController — executeHandler", () => {
  it("sends execution/evaluate in Persistent mode with sessionId = documentUri = notebook uri", async () => {
    const sent: Array<{ method: string; params: Record<string, unknown> }> = [];
    const worker: WorkerLike = {
      send: async (req) => {
        sent.push(req as never);
        return { id: req.id, result: tableEnvelope() };
      },
      cancel: async () => {},
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });

    await controller.executeHandler!([cell("1 + 1")], notebook("file:///a.fsqnb"), controller);

    expect(sent).toHaveLength(1);
    expect(sent[0].method).toBe("execution/evaluate");
    expect(sent[0].params.mode).toEqual({ Case: "Persistent" });
    expect(sent[0].params.sessionId).toBe("file:///a.fsqnb");
    expect(sent[0].params.documentUri).toBe("file:///a.fsqnb");
    expect(sent[0].params.source).toBe("1 + 1");
  });

  it("writes the mapped outputs and ends the execution successfully for a non-error result", async () => {
    const worker: WorkerLike = {
      send: async (req) => ({ id: req.id, result: tableEnvelope() }),
      cancel: async () => {},
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });
    await controller.executeHandler!([cell("q")], notebook("file:///a.fsqnb"), controller);

    const exec = execs(controller)[0];
    expect(exec.ended).toBe(true);
    const outputs = exec.outputs as Array<{ items: Array<{ data: unknown; mime: string }> }>;
    expect(outputs[0].items[0].mime).toBe("application/vnd.code.notebook.stdout");
    expect(outputs[0].items[1].mime).toBe("text/plain");
  });

  it("runs cells sequentially and stops after the first cell whose result is an Error", async () => {
    const seen: string[] = [];
    const worker: WorkerLike = {
      send: async (req) => {
        const src = (req.params as { source: string }).source;
        seen.push(src);
        return src === "bad"
          ? {
              id: req.id,
              result: tableEnvelope({ kind: { Case: "Error" }, displayText: "boom", output: [] }),
            }
          : { id: req.id, result: tableEnvelope() };
      },
      cancel: async () => {},
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });

    await controller.executeHandler!(
      [cell("ok1"), cell("bad"), cell("ok2")],
      notebook("file:///a.fsqnb"),
      controller,
    );

    expect(seen).toEqual(["ok1", "bad"]);
    const last = execs(controller).at(-1)!;
    expect(last.ended).toBe(false);
  });

  it("maps a worker/crashed error frame to a cell error and stops", async () => {
    const worker: WorkerLike = {
      send: async (req) => ({
        id: req.id,
        error: { code: "worker/crashed", message: "The worker process exited before responding." },
      }),
      cancel: async () => {},
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });
    await controller.executeHandler!([cell("q")], notebook("file:///a.fsqnb"), controller);

    const exec = execs(controller)[0];
    expect(exec.ended).toBe(false);
    const item = (exec.outputs as Array<{ items: Array<{ data: { message: string }; mime: string }> }>)[0]
      .items[0];
    expect(item.mime).toBe("application/vnd.code.notebook.error");
    expect(item.data.message).toMatch(/session was lost/i);
  });

  it("surfaces a WorkerUnavailable rejection from getWorker as a cell error", async () => {
    const controller = createFsQueryController({
      getWorker: async () => {
        throw Object.assign(new Error("x"), { userMessage: "Install .NET 10 to run F# cells." });
      },
      log: () => {},
    });
    await controller.executeHandler!([cell("q")], notebook("file:///a.fsqnb"), controller);

    const exec = execs(controller)[0];
    expect(exec.ended).toBe(false);
    const item = (exec.outputs as Array<{ items: Array<{ data: { message: string } }> }>)[0].items[0];
    expect(item.data.message).toBe("Install .NET 10 to run F# cells.");
  });

  it("serializes executeHandler invocations across calls — the second send waits for the first to finish", async () => {
    const sent: string[] = [];
    let releaseFirst!: (frame: WorkerResponseFrame) => void;
    const worker: WorkerLike = {
      send: (req) => {
        const src = (req.params as { source: string }).source;
        sent.push(src);
        if (src === "first") {
          return new Promise<WorkerResponseFrame>((resolve) => {
            releaseFirst = resolve;
          });
        }
        return Promise.resolve({ id: req.id, result: tableEnvelope() });
      },
      cancel: async () => {},
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });

    const run1 = controller.executeHandler!(
      [cell("first")],
      notebook("file:///a.fsqnb"),
      controller,
    );
    const run2 = controller.executeHandler!(
      [cell("second")],
      notebook("file:///a.fsqnb"),
      controller,
    );

    await new Promise((r) => setTimeout(r, 10));
    expect(sent).toEqual(["first"]); // second batch is still queued

    releaseFirst({ id: "x", result: tableEnvelope() });
    await run1;
    await run2;
    expect(sent).toEqual(["first", "second"]);
  });

  it("interruptHandler during a queued run cancels the FIRST request id, not a later one", async () => {
    const ids: string[] = [];
    const cancelled: string[] = [];
    let releaseFirst!: (frame: WorkerResponseFrame) => void;
    const worker: WorkerLike = {
      send: (req) => {
        ids.push(req.id);
        if (ids.length === 1) {
          return new Promise<WorkerResponseFrame>((resolve) => {
            releaseFirst = resolve;
          });
        }
        return Promise.resolve({ id: req.id, result: tableEnvelope() });
      },
      cancel: async (id) => {
        cancelled.push(id);
        releaseFirst({ id, result: tableEnvelope() });
      },
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });

    const run1 = controller.executeHandler!([cell("a")], notebook("file:///a.fsqnb"), controller);
    const run2 = controller.executeHandler!([cell("b")], notebook("file:///a.fsqnb"), controller);
    await new Promise((r) => setTimeout(r, 10));
    await controller.interruptHandler!(notebook("file:///a.fsqnb"));
    await run1;
    await run2;

    expect(cancelled).toEqual([ids[0]]);
  });

  it("ends the cell (unsuccessfully) when the worker resolves with a null result", async () => {
    const worker: WorkerLike = {
      send: async (req) => ({ id: req.id, result: null }),
      cancel: async () => {},
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });
    await controller.executeHandler!([cell("q")], notebook("file:///a.fsqnb"), controller);

    const exec = execs(controller)[0];
    expect(exec.ended).toBe(false);
    const item = (exec.outputs as Array<{ items: Array<{ mime: string }> }>)[0].items[0];
    expect(item.mime).toBe("application/vnd.code.notebook.error");
  });

  it("ends the cell (unsuccessfully) when mapping the result throws", async () => {
    const worker: WorkerLike = {
      // `output` is not iterable → mapEnvelope throws inside the controller
      send: async (req) => ({ id: req.id, result: tableEnvelope({ output: 123 as never }) }),
      cancel: async () => {},
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });
    await controller.executeHandler!([cell("q")], notebook("file:///a.fsqnb"), controller);

    const exec = execs(controller)[0];
    expect(exec.ended).toBe(false);
    const item = (exec.outputs as Array<{ items: Array<{ data: { name: string }; mime: string }> }>)[0]
      .items[0];
    expect(item.mime).toBe("application/vnd.code.notebook.error");
    expect(item.data.name).toBe("InternalError");
  });

  it("interruptHandler cancels the in-flight request id", async () => {
    let release!: (frame: WorkerResponseFrame) => void;
    const cancelled: string[] = [];
    const worker: WorkerLike = {
      send: (req) =>
        new Promise((resolve) => {
          release = resolve;
          void req;
        }),
      cancel: async (id) => {
        cancelled.push(id);
        release({ id, result: tableEnvelope() });
      },
    };
    const controller = createFsQueryController({ getWorker: async () => worker, log: () => {} });

    const run = controller.executeHandler!([cell("slow")], notebook("file:///a.fsqnb"), controller);
    await new Promise((r) => setTimeout(r, 5));
    await controller.interruptHandler!(notebook("file:///a.fsqnb"));
    await run;

    expect(cancelled).toHaveLength(1);
  });
});

describe("restartKernel", () => {
  it("sends session/reset with the notebook uri as sessionId", async () => {
    const sent: Array<{ method: string; params: unknown }> = [];
    const worker: WorkerLike = {
      send: async (req) => {
        sent.push(req as never);
        return { id: req.id, result: null };
      },
      cancel: async () => {},
    };
    await restartKernel(async () => worker, "file:///a.fsqnb");
    expect(sent[0].method).toBe("session/reset");
    expect(sent[0].params).toEqual({ sessionId: "file:///a.fsqnb" });
  });

  it("rejects when the worker responds with an error frame", async () => {
    const worker: WorkerLike = {
      send: async (req) => ({
        id: req.id,
        error: { code: "worker/crashed", message: "The worker process exited before responding." },
      }),
      cancel: async () => {},
    };
    await expect(restartKernel(async () => worker, "file:///a.fsqnb")).rejects.toThrow(
      /worker\/crashed/,
    );
  });

  it("resolves on the happy path (result, no error)", async () => {
    const worker: WorkerLike = {
      send: async (req) => ({ id: req.id, result: { ok: true } }),
      cancel: async () => {},
    };
    await expect(restartKernel(async () => worker, "file:///a.fsqnb")).resolves.toBeUndefined();
  });
});
