import { execFile } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";

export const MINIMUM_DOTNET_MAJOR = 10;

export interface DotnetInfo {
  command: string;
  version: string;
}

export type RunCommand = (
  command: string,
  args: string[],
) => Promise<{ stdout: string; code: number }>;

const defaultRun: RunCommand = (command, args) =>
  new Promise((resolve, reject) => {
    execFile(command, args, { timeout: 10_000 }, (err, stdout) => {
      if (err && (err as NodeJS.ErrnoException).code === "ENOENT") {
        reject(err);
        return;
      }
      resolve({ stdout: stdout ?? "", code: err ? ((err.code as number) ?? 1) : 0 });
    });
  });

export class DotnetNotFoundError extends Error {
  readonly kind = "not-found";
  readonly userMessage =
    "The .NET SDK was not found on PATH. F# Query Studio needs .NET 10 or newer to run its worker.";
  constructor(cause?: unknown) {
    super("dotnet not found");
    this.cause = cause;
  }
}

export class DotnetTooOldError extends Error {
  readonly kind = "too-old";
  readonly userMessage: string;
  constructor(readonly foundVersion: string) {
    super(`dotnet ${foundVersion} is older than ${MINIMUM_DOTNET_MAJOR}`);
    this.userMessage = `F# Query Studio needs .NET ${MINIMUM_DOTNET_MAJOR} or newer; found ${foundVersion}.`;
  }
}

function pickCommand(env: NodeJS.ProcessEnv): string {
  const root = env.DOTNET_ROOT;
  if (root) {
    const candidate = path.join(root, process.platform === "win32" ? "dotnet.exe" : "dotnet");
    if (fs.existsSync(candidate)) {
      return candidate;
    }
  }
  return "dotnet";
}

export async function resolveDotnet(opts?: {
  run?: RunCommand;
  env?: NodeJS.ProcessEnv;
}): Promise<DotnetInfo> {
  const run = opts?.run ?? defaultRun;
  const env = opts?.env ?? process.env;
  const command = pickCommand(env);

  let result: { stdout: string; code: number };
  try {
    result = await run(command, ["--version"]);
  } catch (cause) {
    throw new DotnetNotFoundError(cause);
  }
  if (result.code !== 0) {
    throw new DotnetNotFoundError();
  }

  const version = result.stdout.trim();
  const match = /^(\d+)/.exec(version);
  const major = match ? Number(match[1]) : NaN;
  if (!Number.isFinite(major) || major < MINIMUM_DOTNET_MAJOR) {
    throw new DotnetTooOldError(version || "unknown");
  }

  return { command, version };
}
