import * as vscode from "vscode";
import { FSQNB_NOTEBOOK_TYPE } from "./serializer";
import { mapEnvelope, type OutputItemSpec } from "./result";
import {
  newRequestId,
  readUnionTag,
  SESSION_MODE_PERSISTENT,
  type EvaluateParams,
  type ResultEnvelope,
  type SessionResetParams,
  type WorkerResponseFrame,
} from "../protocol";

const CONTROLLER_ID = "fs-query-studio-kernel";
const CANCEL_GRACE_MS = 5000;

/** The subset of `WorkerClient` the controller needs — injectable for tests. */
export interface WorkerLike {
  send(req: { id: string; method: string; params: unknown }): Promise<WorkerResponseFrame>;
  cancel(requestId: string, gracePeriodMs: number): Promise<void>;
}

export interface ControllerDeps {
  /**
   * Resolves the shared, window-lifetime worker client. Rejects with a
   * `WorkerUnavailableError` (carries `userMessage`) when the worker can't
   * be started.
   */
  getWorker: () => Promise<WorkerLike>;
  log: (line: string) => void;
}

function toOutputItem(spec: OutputItemSpec): vscode.NotebookCellOutputItem {
  switch (spec.kind) {
    case "stdout":
      return vscode.NotebookCellOutputItem.stdout(spec.text);
    case "stderr":
      return vscode.NotebookCellOutputItem.stderr(spec.text);
    case "text":
      return vscode.NotebookCellOutputItem.text(spec.text, "text/plain");
    case "error":
      return vscode.NotebookCellOutputItem.error(
        Object.assign(new Error(spec.message), { name: spec.name }),
      );
  }
}

function errorOutput(name: string, message: string): vscode.NotebookCellOutput {
  return new vscode.NotebookCellOutput([
    vscode.NotebookCellOutputItem.error(Object.assign(new Error(message), { name })),
  ]);
}

export function createFsQueryController(deps: ControllerDeps): vscode.NotebookController {
  const controller = vscode.notebooks.createNotebookController(
    CONTROLLER_ID,
    FSQNB_NOTEBOOK_TYPE,
    "F# Query Studio",
  );
  controller.supportedLanguages = ["fsharp"];
  controller.supportsExecutionOrder = true;
  controller.description = "Runs F# query cells in a persistent FSI session.";

  let executionOrder = 0;
  let inFlightRequestId: string | undefined;
  let worker: WorkerLike | undefined;
  // Serializes `executeHandler` invocations. VS Code calls `executeHandler`
  // again while a prior batch is still awaiting `client.send`, so without a
  // queue two `execution/evaluate`s race (the worker's `Interlocked` guard
  // then returns `execution/busy`) and the shared `inFlightRequestId` gets
  // overwritten so Interrupt cancels the wrong request. Chaining every batch
  // onto this promise keeps the worker strictly one-eval-at-a-time.
  let runQueue: Promise<void> = Promise.resolve();

  controller.interruptHandler = async () => {
    if (worker && inFlightRequestId) {
      await worker.cancel(inFlightRequestId, CANCEL_GRACE_MS);
    }
  };

  async function runBatch(
    cells: vscode.NotebookCell[],
    notebook: vscode.NotebookDocument,
  ): Promise<void> {
    const sessionId = notebook.uri.toString();

    let client: WorkerLike;
    try {
      client = await deps.getWorker();
      worker = client;
    } catch (err) {
      const message =
        (err as { userMessage?: string }).userMessage ?? (err as Error).message;
      deps.log(`Worker unavailable: ${message}`);
      for (const cell of cells) {
        const exec = controller.createNotebookCellExecution(cell);
        exec.executionOrder = ++executionOrder;
        exec.start(Date.now());
        exec.replaceOutput(errorOutput("WorkerUnavailable", message));
        exec.end(false, Date.now());
      }
      return;
    }

    for (const cell of cells) {
      const exec = controller.createNotebookCellExecution(cell);
      exec.executionOrder = ++executionOrder;
      exec.start(Date.now());

      const requestId = newRequestId();
      const params: EvaluateParams = {
        source: cell.document.getText(),
        sessionId,
        mode: SESSION_MODE_PERSISTENT,
        documentUri: sessionId,
      };

      let response: WorkerResponseFrame;
      // The id belongs to the batch that is actually running now; a
      // queued-but-not-started batch never sets it, so Interrupt can only
      // cancel the request that is in flight at this moment.
      inFlightRequestId = requestId;
      try {
        response = await client.send({ id: requestId, method: "execution/evaluate", params });
      } catch (err) {
        inFlightRequestId = undefined;
        exec.replaceOutput(errorOutput("WorkerError", (err as Error).message));
        exec.end(false, Date.now());
        break;
      }
      inFlightRequestId = undefined;

      // Everything from here on — envelope mapping, `toOutputItem`,
      // `replaceOutput` — can throw (e.g. a `null`/absent `result`). Wrap it
      // so no path leaves the execution "running" forever.
      try {
        if (response.error) {
          const message =
            response.error.code === "worker/crashed"
              ? "The worker exited before responding. The FSI session was lost; it restarts on the next run."
              : response.error.message;
          exec.replaceOutput(errorOutput(response.error.code, message));
          exec.end(false, Date.now());
          break;
        }

        const envelope = response.result as ResultEnvelope | null | undefined;
        const items = mapEnvelope(envelope).map(toOutputItem);
        exec.replaceOutput(new vscode.NotebookCellOutput(items));

        const isError = !envelope || readUnionTag(envelope.kind) === "Error";
        exec.end(!isError, Date.now());
        if (isError) {
          break;
        }
      } catch (err) {
        exec.replaceOutput(errorOutput("InternalError", (err as Error).message));
        exec.end(false, Date.now());
        break;
      }
    }
  }

  controller.executeHandler = (cells, notebook) => {
    runQueue = runQueue.then(() => runBatch(cells, notebook)).catch(() => {});
    return runQueue;
  };

  return controller;
}

export async function restartKernel(
  getWorker: () => Promise<WorkerLike>,
  notebookUri: string,
): Promise<void> {
  const client = await getWorker();
  const params: SessionResetParams = { sessionId: notebookUri };
  const response = await client.send({ id: newRequestId(), method: "session/reset", params });
  if (response.error) {
    throw new Error(`${response.error.code}: ${response.error.message}`);
  }
}
