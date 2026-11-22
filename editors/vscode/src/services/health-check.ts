import { randomUUID } from "node:crypto";
import { resolveDotnet, DotnetNotFoundError, DotnetTooOldError } from "../worker/dotnet";
import { resolveWorkerDll, WorkerDllNotFoundError } from "../worker/worker-path";
import { isWorkerVersionSupported, type HealthInfo, type WorkerResponseFrame } from "../protocol";

export interface HealthCheckDeps {
  extensionRoot: string;
  env: NodeJS.ProcessEnv;
  log: (line: string) => void;
  resolveDotnet: typeof resolveDotnet;
  resolveWorkerDll: typeof resolveWorkerDll;
  makeClient: (
    dotnetCommand: string,
    dll: string,
  ) => {
    send: (req: { id: string; method: string; params: unknown }) => Promise<WorkerResponseFrame>;
    dispose: () => void;
  };
}

export type HealthCheckResult =
  | { ok: true; version: string; capabilities: string[] }
  | {
      ok: false;
      kind: "dotnet-missing" | "dotnet-old" | "worker-missing" | "version-unsupported" | "error";
      message: string;
    };

export async function runHealthCheck(deps: HealthCheckDeps): Promise<HealthCheckResult> {
  let dotnet: { command: string; version: string };
  try {
    dotnet = await deps.resolveDotnet({ env: deps.env });
  } catch (err) {
    if (err instanceof DotnetNotFoundError) {
      deps.log(`dotnet not found: ${err.userMessage}`);
      return { ok: false, kind: "dotnet-missing", message: err.userMessage };
    }
    if (err instanceof DotnetTooOldError) {
      deps.log(`dotnet too old: ${err.userMessage}`);
      return { ok: false, kind: "dotnet-old", message: err.userMessage };
    }
    throw err;
  }
  deps.log(`Using dotnet ${dotnet.version} (${dotnet.command}).`);

  let dll: string;
  try {
    dll = deps.resolveWorkerDll(deps.extensionRoot, deps.env);
  } catch (err) {
    if (err instanceof WorkerDllNotFoundError) {
      deps.log(`worker dll not found: ${err.userMessage}`);
      return { ok: false, kind: "worker-missing", message: err.userMessage };
    }
    throw err;
  }
  deps.log(`Worker dll: ${dll}`);

  const client = deps.makeClient(dotnet.command, dll);
  try {
    const response = await client.send({ id: randomUUID(), method: "worker/health", params: {} });
    if (response.error) {
      const message = `Worker health failed: ${response.error.code} ${response.error.message}`;
      deps.log(message);
      return { ok: false, kind: "error", message };
    }
    const health = response.result as HealthInfo;
    if (!isWorkerVersionSupported(health.version)) {
      const message = `Worker protocol version ${health.version} is not supported by this extension.`;
      deps.log(message);
      return { ok: false, kind: "version-unsupported", message };
    }
    deps.log(
      `Worker healthy: version ${health.version}, capabilities [${health.capabilities.join(", ")}].`,
    );
    return { ok: true, version: health.version, capabilities: health.capabilities };
  } finally {
    client.dispose();
  }
}
