import { readFrames, writeFrame } from "./ndjson";
import type { WorkerRequestFrame, WorkerResponseFrame } from "../protocol";

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
