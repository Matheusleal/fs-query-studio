import { PassThrough } from "node:stream";
import { readFrames, writeFrame } from "../src/worker/ndjson";
import type { WorkerProcess } from "../src/worker/client";
import type { WorkerResponseFrame } from "../src/protocol";

export function createFakeChild() {
  const stdin = new PassThrough();
  const stdout = new PassThrough();
  const stderr = new PassThrough();
  const sentFrames: unknown[] = [];
  const rawStdinLines: string[] = [];
  let exitCb: (() => void) | undefined;

  readFrames(stdin, { onFrame: (f) => sentFrames.push(f), onEnd: () => {} });
  stdin.on("data", (chunk: Buffer | string) => {
    for (const line of chunk.toString("utf8").split("\n")) {
      if (line.trim().length > 0) {
        rawStdinLines.push(line);
      }
    }
  });

  const process: WorkerProcess = {
    stdin,
    stdout,
    stderr,
    pid: 4242,
    kill: () => exitCb?.(),
    onExit: (cb) => {
      exitCb = cb;
    },
  };

  return {
    process,
    sentFrames,
    rawStdinLines,
    reply: (frame: WorkerResponseFrame) => {
      void writeFrame(stdout, frame);
    },
    emitExit: () => {
      stdout.end();
      exitCb?.();
    },
  };
}
