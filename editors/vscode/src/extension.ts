import * as vscode from "vscode";
import { randomUUID } from "node:crypto";
import { resolveDotnet, DotnetNotFoundError, DotnetTooOldError } from "./worker/dotnet";
import { resolveWorkerDll, WorkerDllNotFoundError } from "./worker/worker-path";
import { WorkerClient } from "./worker/client";
import { spawnWorkerProcess } from "./worker/spawn";
import { isWorkerVersionSupported, type HealthInfo, type WorkerResponseFrame } from "./protocol";
import { FsqnbSerializer, FSQNB_NOTEBOOK_TYPE } from "./notebook/serializer";
import { createFsQueryController, restartKernel } from "./notebook/controller";
import { getWorkerSession, disposeWorkerSession } from "./worker/session";

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

let outputChannel: vscode.OutputChannel | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<void> {
  outputChannel = vscode.window.createOutputChannel("F# Query Studio");

  const runCheck = async (): Promise<HealthCheckResult> => {
    outputChannel!.show(true);
    const result = await runHealthCheck({
      extensionRoot: context.extensionPath,
      env: process.env,
      log: (line) => outputChannel!.appendLine(line),
      resolveDotnet,
      resolveWorkerDll,
      makeClient: (dotnetCommand, dll) => {
        const client = new WorkerClient({ spawn: () => spawnWorkerProcess(dotnetCommand, dll) });
        return { send: (req) => client.send(req), dispose: () => client.dispose() };
      },
    });

    if (result.ok) {
      void vscode.window.showInformationMessage(
        `F# Query Studio worker OK (version ${result.version}).`,
      );
    } else if (result.kind === "dotnet-missing" || result.kind === "dotnet-old") {
      const pick = await vscode.window.showErrorMessage(result.message, "Install .NET");
      if (pick === "Install .NET") {
        void vscode.env.openExternal(vscode.Uri.parse("https://dotnet.microsoft.com/download"));
      }
    } else {
      void vscode.window.showErrorMessage(result.message);
    }

    return result;
  };

  const command = vscode.commands.registerCommand("fs-query-studio.checkWorker", runCheck);

  const getWorker = async () => {
    const session = await getWorkerSession({
      extensionRoot: context.extensionPath,
      env: process.env,
      log: (line) => outputChannel!.appendLine(line),
    });
    return session.client;
  };

  const notebookSerializer = vscode.workspace.registerNotebookSerializer(
    FSQNB_NOTEBOOK_TYPE,
    new FsqnbSerializer(),
  );

  const controller = createFsQueryController({
    getWorker,
    log: (line) => outputChannel!.appendLine(line),
  });

  const restartCommand = vscode.commands.registerCommand(
    "fs-query-studio.restartKernel",
    async () => {
      const active = vscode.window.activeNotebookEditor;
      if (!active || active.notebook.notebookType !== FSQNB_NOTEBOOK_TYPE) {
        void vscode.window.showInformationMessage(
          "Open an .fsqnb notebook to restart its F# session.",
        );
        return;
      }
      try {
        await restartKernel(getWorker, active.notebook.uri.toString());
        void vscode.window.showInformationMessage("F# Query Studio session restarted.");
      } catch (err) {
        void vscode.window.showErrorMessage(
          `Restart failed: ${(err as { userMessage?: string }).userMessage ?? (err as Error).message}`,
        );
      }
    },
  );

  context.subscriptions.push(
    command,
    notebookSerializer,
    controller,
    restartCommand,
    outputChannel,
  );
}

export async function deactivate(): Promise<void> {
  disposeWorkerSession();
  outputChannel?.dispose();
  outputChannel = undefined;
}
