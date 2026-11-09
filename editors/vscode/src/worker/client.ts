import { readFrames, writeFrame } from "./ndjson";
import {
  isHostRequestFrame,
  newRequestId,
  type WorkerErrorInfo,
  type WorkerRequestFrame,
  type WorkerResponseFrame,
} from "../protocol";

export type HostRequestHandler = (
  method: string,
  params: unknown,
) => Promise<{ result?: unknown; error?: WorkerErrorInfo }>;

export interface WorkerProcess {
  stdin: NodeJS.WritableStream;
  stdout: NodeJS.ReadableStream;
  stderr: NodeJS.ReadableStream;
  pid: number | undefined;
  kill(): void;
  onExit(cb: () => void): void;
}

export type SpawnWorker = () => WorkerProcess;

export interface WorkerClientOptions {
  spawn: SpawnWorker;
  onCrash?: () => void;
  log?: (line: string) => void;
}

const CRASHED_ERROR = {
  code: "worker/crashed",
  message: "The worker process exited before responding.",
} as const;

interface Pending {
  resolve: (frame: WorkerResponseFrame) => void;
}

export class WorkerClient {
  private readonly spawn: SpawnWorker;
  private readonly onCrash?: () => void;
  private readonly log?: (line: string) => void;
  private current: WorkerProcess | undefined;
  private readonly pending = new Map<string, Pending>();
  private writeChain: Promise<void> = Promise.resolve();
  private hostRequestHandler?: HostRequestHandler;

  constructor(options: WorkerClientOptions) {
    this.spawn = options.spawn;
    this.onCrash = options.onCrash;
    this.log = options.log;
  }

  setHostRequestHandler(handler: HostRequestHandler): void {
    this.hostRequestHandler = handler;
  }

  get currentPid(): number | undefined {
    return this.current?.pid;
  }

  send(request: WorkerRequestFrame): Promise<WorkerResponseFrame> {
    const proc = this.ensureProcess();
    this.log?.(`[Host -> Worker] ${request.method} (${request.id})`);
    return new Promise<WorkerResponseFrame>((resolve) => {
      this.pending.set(request.id, {
        resolve: (frame) => {
          if (frame.error) {
            this.log?.(
              `[Worker -> Host] Error on ${request.method} (${request.id}): [${frame.error.code}] ${frame.error.message}`,
            );
          } else {
            this.log?.(`[Worker -> Host] Result on ${request.method} (${request.id})`);
          }
          resolve(frame);
        },
      });
      this.writeChain = this.writeChain
        .then(() => writeFrame(proc.stdin, request))
        .catch((err) => {
          this.log?.(
            `[Host -> Worker] Failed to send ${request.method} (${request.id}): ${(err as Error)?.message ?? String(err)}`,
          );
        });
    });
  }

  async cancel(requestId: string, gracePeriodMs: number): Promise<void> {
    // Fire the cooperative cancel and move on — the ack frame carries nothing
    // the grace-period race needs, and awaiting it would hang if the worker
    // never acks. Mirrors `App/WorkerClient.fs` minus the discarded `_ack`.
    this.log?.(`[Host -> Worker] Cancelling request ${requestId} (gracePeriodMs: ${gracePeriodMs})`);
    void this.send({ id: newRequestId(), method: "execution/cancel", params: {} });

    const waiter = this.pending.get(requestId);
    if (!waiter) {
      return;
    }

    const settled = new Promise<"settled">((resolve) => {
      const original = waiter.resolve;
      waiter.resolve = (frame) => {
        original(frame);
        resolve("settled");
      };
    });
    const timedOut = new Promise<"timeout">((resolve) =>
      setTimeout(() => resolve("timeout"), gracePeriodMs),
    );

    const outcome = await Promise.race([settled, timedOut]);
    if (outcome === "timeout" && this.pending.has(requestId)) {
      this.log?.(`[Host] Worker timed out on cancel (${requestId}); terminating process.`);
      const stillPending = this.pending.get(requestId)!;
      this.pending.delete(requestId);
      this.current?.kill();
      stillPending.resolve({
        id: requestId,
        error: {
          code: "execution/forciblyStopped",
          message:
            "The worker did not stop cooperatively within the grace period and was terminated.",
        },
      });
    }
  }

  dispose(): void {
    if (this.current) {
      this.log?.("[Host] Disposing worker process.");
      try {
        if ("end" in this.current.stdin && typeof (this.current.stdin as { end?: unknown }).end === "function") {
          (this.current.stdin as { end: () => void }).end();
        }
      } catch {
        // ignore
      }
      this.current.kill();
      this.current = undefined;
    }
  }

  protected ensureProcess(): WorkerProcess {
    if (this.current) {
      return this.current;
    }
    const proc = this.spawn();
    this.current = proc;
    this.log?.(`[Worker] Started process with PID ${proc.pid ?? "unknown"}.`);

    if (proc.stdin && typeof (proc.stdin as unknown as { on?: unknown }).on === "function") {
      (proc.stdin as unknown as { on: (event: string, cb: (err: unknown) => void) => void }).on("error", (err: unknown) => {
        this.log?.(`[Worker stdin error] ${(err as Error)?.message ?? String(err)}`);
      });
    }

    if (proc.stderr && typeof proc.stderr.on === "function") {
      let stderrBuffer = "";
      proc.stderr.on("data", (chunk: Buffer | string) => {
        stderrBuffer += chunk.toString();
        const lines = stderrBuffer.split(/\r?\n/);
        stderrBuffer = lines.pop() ?? "";
        for (const line of lines) {
          if (line.trim().length > 0) {
            this.log?.(`[Worker stderr] ${line}`);
          }
        }
      });
      proc.stderr.on("end", () => {
        if (stderrBuffer.trim().length > 0) {
          this.log?.(`[Worker stderr] ${stderrBuffer}`);
          stderrBuffer = "";
        }
      });
    }

    readFrames(proc.stdout, {
      onFrame: (frame) => this.onFrame(frame),
      onEnd: () => this.onExit(),
    });
    proc.onExit((codeOrErr?: unknown) => {
      this.log?.(`[Worker] Process exited${codeOrErr !== undefined ? `: ${String(codeOrErr)}` : ""}.`);
      this.onExit();
    });
    return proc;
  }

  private onFrame(frame: unknown): void {
    if (isHostRequestFrame(frame)) {
      void this.answerHostRequest(frame.id, frame.method, frame.params);
      return;
    }
    const response = frame as WorkerResponseFrame;
    if (!response || typeof response.id !== "string") {
      return;
    }
    const waiter = this.pending.get(response.id);
    if (waiter) {
      this.pending.delete(response.id);
      waiter.resolve(response);
    }
  }

  private async answerHostRequest(id: string, method: string, params: unknown): Promise<void> {
    const proc = this.current;
    if (!proc) {
      return;
    }
    this.log?.(`[Worker -> Host] Request ${method} (${id})`);
    // Every reply frame MUST carry BOTH `result` and `error` keys, even when
    // one is null. `JSON.stringify` omits `undefined` properties, and the
    // worker's `WorkerResponseFrame` (FSharp.SystemTextJson) does NOT default
    // a missing `option` record field to `None` — a frame with only one of
    // the two keys throws `JsonException` in `routeResponse` and kills the
    // worker process. A non-`Error` throw likewise leaves `message`
    // `undefined` → dropped key → non-optional `WorkerErrorInfo.Message`
    // throw; `String(err)` keeps it a string.
    let reply: WorkerResponseFrame;
    if (!this.hostRequestHandler) {
      reply = {
        id,
        result: null,
        error: { code: "host/unhandled", message: `No host handler for ${method}` },
      };
    } else {
      try {
        const outcome = await this.hostRequestHandler(method, params);
        reply = outcome.error
          ? { id, result: null, error: outcome.error }
          : { id, result: outcome.result ?? null, error: null };
      } catch (err) {
        reply = {
          id,
          result: null,
          error: {
            code: "host/threw",
            message: err instanceof Error ? err.message : String(err),
          },
        };
      }
    }
    if (reply.error) {
      this.log?.(
        `[Host -> Worker] Reply error on ${method} (${id}): [${reply.error.code}] ${reply.error.message}`,
      );
    } else {
      this.log?.(`[Host -> Worker] Reply result on ${method} (${id})`);
    }
    this.writeChain = this.writeChain
      .then(() => writeFrame(proc.stdin, reply))
      .catch((err) => {
        this.log?.(
          `[Host -> Worker] Failed to send reply frame for ${method} (${id}): ${(err as Error)?.message ?? String(err)}`,
        );
      });
  }

  protected onExit(): void {
    if (!this.current) {
      return;
    }
    this.log?.("[Host] Worker process disconnected; aborting pending requests.");
    this.current = undefined;
    for (const [id, waiter] of this.pending) {
      waiter.resolve({ id, error: { ...CRASHED_ERROR } });
    }
    this.pending.clear();
    this.writeChain = Promise.resolve();
    this.onCrash?.();
  }
}
