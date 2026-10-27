import * as vscode from "vscode";
import { randomUUID } from "node:crypto";
import { resolveDotnet, DotnetNotFoundError, DotnetTooOldError } from "./worker/dotnet";
import { resolveWorkerDll, WorkerDllNotFoundError } from "./worker/worker-path";
import { WorkerClient } from "./worker/client";
import { spawnWorkerProcess } from "./worker/spawn";
import { isWorkerVersionSupported, readUnionTag, type HealthInfo, type WorkerResponseFrame, type ConnectorKindName } from "./protocol";
import { FsqnbSerializer, FSQNB_NOTEBOOK_TYPE } from "./notebook/serializer";
import { openNewUntitledNotebook, createNotebookFileInFolder } from "./notebook/new-notebook";
import { createFsQueryController, restartKernel } from "./notebook/controller";
import { registerRendererHost } from "./notebook/renderer-host";
import { FSQUERY_RENDERER_ID } from "./renderer/types";
import { getWorkerSession, disposeWorkerSession } from "./worker/session";
import { registerLanguageFeatures } from "./lang/providers";
import { registerConnectionFeatures, type ConnectionsApi } from "./connections/commands";
import { registerSecretBridge } from "./secrets/bridge";

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

export interface FsQueryStudioApi {
  connections: ConnectionsApi;
}

let outputChannel: vscode.OutputChannel | undefined;

export async function activate(context: vscode.ExtensionContext): Promise<FsQueryStudioApi> {
  outputChannel = vscode.window.createOutputChannel("F# Query Studio");

  const log = (line: string) => {
    const time = new Date().toISOString().substring(11, 19);
    outputChannel?.appendLine(`[${time}] ${line}`);
  };

  log("F# Query Studio extension activating...");

  const runCheck = async (): Promise<HealthCheckResult> => {
    outputChannel!.show(true);
    const result = await runHealthCheck({
      extensionRoot: context.extensionPath,
      env: process.env,
      log,
      resolveDotnet,
      resolveWorkerDll,
      makeClient: (dotnetCommand, dll) => {
        const client = new WorkerClient({
          spawn: () => spawnWorkerProcess(dotnetCommand, dll),
          log,
        });
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

  let bridgeRegistered = false;
  const getWorker = async () => {
    const session = await getWorkerSession({
      extensionRoot: context.extensionPath,
      env: process.env,
      log,
    });
    if (!bridgeRegistered) {
      bridgeRegistered = true;
      registerSecretBridge({ client: session.client, secrets: context.secrets });
    }
    return session.client;
  };

  const notebookSerializer = vscode.workspace.registerNotebookSerializer(
    FSQNB_NOTEBOOK_TYPE,
    new FsqnbSerializer(),
  );

  const controller = createFsQueryController({
    getWorker,
    log,
  });

  const rendererMessaging = vscode.notebooks.createRendererMessaging(FSQUERY_RENDERER_ID);
  const rendererHost = registerRendererHost({
    messaging: rendererMessaging,
    getWorker,
    pickSaveFile: async (defaultName, documentUri) => {
      const picked = await vscode.window.showSaveDialog({
        defaultUri: vscode.Uri.joinPath(vscode.Uri.parse(documentUri), "..", defaultName),
        filters: { CSV: ["csv"] },
        saveLabel: "Export",
      });
      return picked?.fsPath;
    },
    log,
  });

  // Registered before language features so its `api.list` can feed
  // `Connections.sql "…"` name completion.
  const connections = registerConnectionFeatures({
    getWorker,
    secrets: context.secrets,
    log,
  });

  const languageFeatures = registerLanguageFeatures({
    getWorker,
    log,
    listConnections: async () => {
      const list = await connections.api.list();
      return list.map((c) => ({
        name: c.name,
        kind: readUnionTag(c.kind) as ConnectorKindName | undefined,
      }));
    },
    listConnectionNames: async () =>
      (await connections.api.list()).map((c) => c.name),
  });

  const showOutputCommand = vscode.commands.registerCommand(
    "fs-query-studio.showOutput",
    () => {
      outputChannel?.show(true);
    },
  );

  const newNotebookCommand = vscode.commands.registerCommand(
    "fs-query-studio.newNotebook",
    () =>
      openNewUntitledNotebook({
        openNotebookDocument: (type, data) =>
          Promise.resolve(vscode.workspace.openNotebookDocument(type, data)),
        showNotebookDocument: (doc) =>
          Promise.resolve(
            vscode.window.showNotebookDocument(doc as vscode.NotebookDocument),
          ),
      }),
  );

  const newNotebookHereCommand = vscode.commands.registerCommand(
    "fs-query-studio.newNotebookHere",
    async (resource?: vscode.Uri) => {
      const folder = resource ?? vscode.workspace.workspaceFolders?.[0]?.uri;
      if (!folder) {
        void vscode.window.showErrorMessage(
          "Open a folder to create an F# Query Notebook file.",
        );
        return;
      }
      try {
        await createNotebookFileInFolder(folder.fsPath, {
          showInputBox: (opts) => Promise.resolve(vscode.window.showInputBox(opts)),
          fileExists: async (fsPath) => {
            try {
              await vscode.workspace.fs.stat(vscode.Uri.file(fsPath));
              return true;
            } catch {
              return false;
            }
          },
          writeFile: (fsPath, bytes) =>
            Promise.resolve(vscode.workspace.fs.writeFile(vscode.Uri.file(fsPath), bytes)),
          openNotebook: async (fsPath) => {
            const doc = await vscode.workspace.openNotebookDocument(vscode.Uri.file(fsPath));
            await vscode.window.showNotebookDocument(doc);
          },
          joinPath: (dir, name) => vscode.Uri.joinPath(vscode.Uri.file(dir), name).fsPath,
        });
      } catch (err) {
        void vscode.window.showErrorMessage(
          (err as { userMessage?: string }).userMessage ?? (err as Error).message,
        );
      }
    },
  );

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

  const createSampleDataCommand = vscode.commands.registerCommand(
    "fs-query-studio.createSampleData",
    async () => {
      let targetDir: string | undefined;
      const wsFolders = vscode.workspace.workspaceFolders;
      if (wsFolders && wsFolders.length === 1) {
        targetDir = wsFolders[0].uri.fsPath;
      } else if (wsFolders && wsFolders.length > 1) {
        const pick = await vscode.window.showQuickPick(
          wsFolders.map((f) => ({ label: f.name, description: f.uri.fsPath, uri: f.uri })),
          { placeHolder: "Select a workspace folder for sample data" },
        );
        if (!pick) return;
        targetDir = pick.uri.fsPath;
      } else {
        const picked = await vscode.window.showOpenDialog({
          canSelectFiles: false,
          canSelectFolders: true,
          canSelectMany: false,
          openLabel: "Select Folder for Sample Data",
        });
        if (!picked || picked.length === 0) return;
        targetDir = picked[0].fsPath;
      }

      log(`[Workspace] Creating sample data in directory: ${targetDir}`);
      try {
        const worker = await getWorker();
        const res = await worker.send({
          id: randomUUID(),
          method: "workspace/createSampleData",
          params: { targetDir, targetDirectory: targetDir },
        });

        if (res.error) {
          log(`[Workspace] Failed to create sample data: ${res.error.message}`);
          outputChannel?.show(true);
          const choice = await vscode.window.showErrorMessage(
            `Failed to create sample data: ${res.error.message}`,
            "Show Logs",
          );
          if (choice === "Show Logs") {
            outputChannel?.show(true);
          }
          return;
        }

        const data = res.result as {
          sqlitePath: string;
          csvPath: string;
          notebookPath: string;
          notebookPtPath?: string;
        };

        log(
          `[Workspace] Sample data created successfully: SQLite=${data.sqlitePath}, CSV=${data.csvPath}, Notebook=${data.notebookPath}, NotebookPt=${data.notebookPtPath}`,
        );

        await vscode.commands.executeCommand("fs-query-studio.connections.refresh");

        const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
        const targetNotebook =
          isPt && data.notebookPtPath ? data.notebookPtPath : data.notebookPath;

        try {
          const doc = await vscode.workspace.openNotebookDocument(
            vscode.Uri.file(targetNotebook),
          );
          await vscode.window.showNotebookDocument(doc);
        } catch {
          // ignore error if notebook viewer fails to open immediately
        }

        const successMsg = isPt
          ? "Banco de dados SQLite ('sample_store.db'), CSV ('sales_feed.csv') e notebooks interativos ('sample_demo_pt.fsqnb', 'sample_demo.fsqnb') criados com sucesso!"
          : "Sample SQLite database ('sample_store.db'), CSV ('sales_feed.csv'), and interactive notebooks ('sample_demo.fsqnb', 'sample_demo_pt.fsqnb') created successfully!";

        void vscode.window.showInformationMessage(successMsg);
      } catch (err) {
        const msg = (err as Error).message;
        log(`[Workspace] Error creating sample data: ${msg}`);
        outputChannel?.show(true);
        const choice = await vscode.window.showErrorMessage(
          `Error creating sample data: ${msg}`,
          "Show Logs",
        );
        if (choice === "Show Logs") {
          outputChannel?.show(true);
        }
      }
    },
  );

  const openDocsCommand = vscode.commands.registerCommand(
    "fs-query-studio.openDocs",
    async () => {
      const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
      const primaryDoc = isPt ? "USER_GUIDE_PT.md" : "USER_GUIDE.md";
      const secondaryDoc = isPt ? "USER_GUIDE.md" : "USER_GUIDE_PT.md";
      const candidates = [
        vscode.Uri.joinPath(context.extensionUri, primaryDoc),
        vscode.Uri.joinPath(context.extensionUri, secondaryDoc),
        vscode.Uri.joinPath(context.extensionUri, "README.md"),
        vscode.Uri.joinPath(context.extensionUri, "readme.md"),
      ];
      let docUri = candidates[0];
      for (const candidate of candidates) {
        try {
          await vscode.workspace.fs.stat(candidate);
          docUri = candidate;
          break;
        } catch {
          // continue
        }
      }
      try {
        await vscode.commands.executeCommand("markdown.showPreview", docUri);
      } catch {
        const doc = await vscode.workspace.openTextDocument(docUri);
        await vscode.window.showTextDocument(doc, { preview: true });
      }
    },
  );

  const statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  statusBar.command = "fs-query-studio.showOutput";
  statusBar.text = "$(database) F# Query Studio";
  statusBar.tooltip = "F# Query Studio: Show Output Logs";
  statusBar.show();

  context.subscriptions.push(
    command,
    notebookSerializer,
    controller,
    rendererHost,
    showOutputCommand,
    newNotebookCommand,
    newNotebookHereCommand,
    restartCommand,
    createSampleDataCommand,
    openDocsCommand,
    languageFeatures,
    connections.disposable,
    outputChannel,
    statusBar,
  );

  return { connections: connections.api };
}

export async function deactivate(): Promise<void> {
  disposeWorkerSession();
  outputChannel?.dispose();
  outputChannel = undefined;
}
