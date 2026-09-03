import { readFrames, writeFrame } from "./ndjson";
import { newRequestId, type WorkerRequestFrame, type WorkerResponseFrame } from "../protocol";

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
  private current: WorkerProcess | undefined;
  private readonly pending = new Map<string, Pending>();
  private writeChain: Promise<void> = Promise.resolve();

  constructor(options: WorkerClientOptions) {
    this.spawn = options.spawn;
    this.onCrash = options.onCrash;
  }

  get currentPid(): number | undefined {
    return this.current?.pid;
  }

  send(request: WorkerRequestFrame): Promise<WorkerResponseFrame> {
    const proc = this.ensureProcess();
    return new Promise<WorkerResponseFrame>((resolve) => {
      this.pending.set(request.id, { resolve });
      this.writeChain = this.writeChain
        .then(() => writeFrame(proc.stdin, request))
        .catch(() => {
          /* a failed write surfaces as an exit via readFrames onEnd */
        });
    });
  }

  async cancel(requestId: string, gracePeriodMs: number): Promise<void> {
    // Fire the cooperative cancel and move on — the ack frame carries nothing
    // the grace-period race needs, and awaiting it would hang if the worker
    // never acks. Mirrors `App/WorkerClient.fs` minus the discarded `_ack`.
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
    this.current?.kill();
    this.current = undefined;
  }

  protected ensureProcess(): WorkerProcess {
    if (this.current) {
      return this.current;
    }
    const proc = this.spawn();
    this.current = proc;
    readFrames(proc.stdout, {
      onFrame: (frame) => this.onFrame(frame),
      onEnd: () => this.onExit(),
    });
    proc.onExit(() => this.onExit());
    return proc;
  }

  private onFrame(frame: unknown): void {
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

  protected onExit(): void {
    if (!this.current) {
      return;
    }
    this.current = undefined;
    for (const [id, waiter] of this.pending) {
      waiter.resolve({ id, error: { ...CRASHED_ERROR } });
    }
    this.pending.clear();
    this.writeChain = Promise.resolve();
    this.onCrash?.();
  }
}
