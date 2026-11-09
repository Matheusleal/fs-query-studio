import { spawn } from "node:child_process";
import type { WorkerProcess } from "./client";

export function spawnWorkerProcess(dotnetCommand: string, workerDllPath: string): WorkerProcess {
  const env = {
    ...process.env,
    DOTNET_GCServer: "0",
    DOTNET_gcConcurrent: "1",
    DOTNET_TieredPGO: "1",
  };
  const child = spawn(dotnetCommand, [workerDllPath], {
    stdio: ["pipe", "pipe", "pipe"],
    env,
  });
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
