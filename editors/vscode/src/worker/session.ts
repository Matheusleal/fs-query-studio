import { resolveDotnet as realResolveDotnet, DotnetNotFoundError, DotnetTooOldError } from "./dotnet";
import { resolveWorkerDll as realResolveWorkerDll, WorkerDllNotFoundError } from "./worker-path";
import { WorkerClient } from "./client";
import { spawnWorkerProcess } from "./spawn";
import { newRequestId, isWorkerVersionSupported, type HealthInfo } from "../protocol";

export type WorkerUnavailableKind =
  | "dotnet-missing"
  | "dotnet-old"
  | "worker-missing"
  | "version-unsupported"
  | "error";

export class WorkerUnavailableError extends Error {
  constructor(
    readonly kind: WorkerUnavailableKind,
    readonly userMessage: string,
  ) {
    super(userMessage);
    this.name = "WorkerUnavailableError";
  }
}

export interface WorkerSession {
  client: WorkerClient;
  health: HealthInfo;
  dispose(): void;
}

export interface ResolveWorkerSessionDeps {
  extensionRoot: string;
  env: NodeJS.ProcessEnv;
  log: (line: string) => void;
  resolveDotnet?: typeof realResolveDotnet;
  resolveWorkerDll?: typeof realResolveWorkerDll;
  makeClient?: (dotnetCommand: string, dll: string) => WorkerClient;
}

export async function resolveWorkerSession(deps: ResolveWorkerSessionDeps): Promise<WorkerSession> {
  const resolveDotnet = deps.resolveDotnet ?? realResolveDotnet;
  const resolveWorkerDll = deps.resolveWorkerDll ?? realResolveWorkerDll;
  const makeClient =
    deps.makeClient ??
    ((command, dll) =>
      new WorkerClient({
        spawn: () => spawnWorkerProcess(command, dll),
        log: deps.log,
      }));

  let dotnet: { command: string; version: string };
  try {
    dotnet = await resolveDotnet({ env: deps.env });
  } catch (err) {
    if (err instanceof DotnetNotFoundError) {
      throw new WorkerUnavailableError("dotnet-missing", err.userMessage);
    }
    if (err instanceof DotnetTooOldError) {
      throw new WorkerUnavailableError("dotnet-old", err.userMessage);
    }
    throw new WorkerUnavailableError("error", (err as Error).message);
  }
  deps.log(`Using dotnet ${dotnet.version} (${dotnet.command}).`);

  let dll: string;
  try {
    dll = resolveWorkerDll(deps.extensionRoot, deps.env);
  } catch (err) {
    if (err instanceof WorkerDllNotFoundError) {
      throw new WorkerUnavailableError("worker-missing", err.userMessage);
    }
    throw new WorkerUnavailableError("error", (err as Error).message);
  }
  deps.log(`Worker dll: ${dll}`);

  const client = makeClient(dotnet.command, dll);
  let health: HealthInfo;
  try {
    const response = await client.send({
      id: newRequestId(),
      method: "worker/health",
      params: {},
    });
    if (response.error) {
      client.dispose();
      throw new WorkerUnavailableError(
        "error",
        `Worker health check failed: ${response.error.code} ${response.error.message}`,
      );
    }
    health = response.result as HealthInfo;
  } catch (err) {
    if (err instanceof WorkerUnavailableError) {
      throw err;
    }
    client.dispose();
    throw new WorkerUnavailableError("error", (err as Error).message);
  }

  if (!isWorkerVersionSupported(health.version)) {
    client.dispose();
    throw new WorkerUnavailableError(
      "version-unsupported",
      `Worker protocol version ${health.version} is not supported by this build of the extension.`,
    );
  }
  deps.log(`Worker healthy: version ${health.version}.`);

  return { client, health, dispose: () => client.dispose() };
}

// --- window-lifetime singleton ---

let sessionPromise: Promise<WorkerSession> | undefined;

export function getWorkerSession(deps: ResolveWorkerSessionDeps): Promise<WorkerSession> {
  if (!sessionPromise) {
    sessionPromise = resolveWorkerSession(deps).catch((err) => {
      sessionPromise = undefined; // let the next run retry
      throw err;
    });
  }
  return sessionPromise;
}

export function disposeWorkerSession(): void {
  const pending = sessionPromise;
  sessionPromise = undefined;
  if (pending) {
    void pending.then((session) => session.dispose()).catch(() => undefined);
  }
}
