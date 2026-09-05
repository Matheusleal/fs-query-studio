import * as fs from "node:fs";
import * as path from "node:path";

export class WorkerDllNotFoundError extends Error {
  readonly userMessage: string;
  constructor(readonly checked: string[]) {
    super(`worker dll not found; checked: ${checked.join(", ")}`);
    this.userMessage =
      "The F# Query Studio worker was not found. Run `dotnet publish` into editors/vscode/dist/worker, " +
      "or set FSQS_WORKER_DLL to the built FSharpQueryStudio.Worker.dll.";
  }
}

export function resolveWorkerDll(extensionRoot: string, env: NodeJS.ProcessEnv = process.env): string {
  const checked: string[] = [];

  // An explicit override is authoritative: if it's set but points nowhere,
  // that's a configuration error worth surfacing, not a reason to fall back
  // to the bundled worker.
  const override = env.FSQS_WORKER_DLL;
  if (override) {
    checked.push(override);
    if (fs.existsSync(override)) {
      return override;
    }
    throw new WorkerDllNotFoundError(checked);
  }

  const bundled = path.join(extensionRoot, "dist", "worker", "FSharpQueryStudio.Worker.dll");
  checked.push(bundled);
  if (fs.existsSync(bundled)) {
    return bundled;
  }

  throw new WorkerDllNotFoundError(checked);
}
