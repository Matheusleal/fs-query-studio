import { spawn } from "node:child_process";
import type { WorkerProcess } from "./client";

export function spawnWorkerProcess(dotnetCommand: string, workerDllPath: string): WorkerProcess {
  const child = spawn(dotnetCommand, [workerDllPath], { stdio: ["pipe", "pipe", "pipe"] });
  return {
    stdin: child.stdin!,
    stdout: child.stdout!,
    stderr: child.stderr!,
    get pid() {
      return child.pid;
    },
    kill: () => {
      child.kill();
    },
    onExit: (cb) => {
      child.on("exit", cb);
      child.on("error", cb);
    },
  };
}
