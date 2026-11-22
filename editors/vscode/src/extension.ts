import * as vscode from "vscode";
import { randomUUID } from "node:crypto";
import { resolveDotnet } from "./worker/dotnet";
import { resolveWorkerDll } from "./worker/worker-path";
import { WorkerClient } from "./worker/client";
import { spawnWorkerProcess } from "./worker/spawn";
import { readUnionTag, type ConnectorKindName } from "./protocol";
import { FsqnbSerializer, FSQNB_NOTEBOOK_TYPE } from "./notebook/serializer";
import { openNewUntitledNotebook, createNotebookFileInFolder } from "./notebook/new-notebook";
import { createFsQueryController, restartKernel } from "./notebook/controller";
import { registerRendererHost } from "./notebook/renderer-host";
import { FSQUERY_RENDERER_ID } from "./renderer/types";
import { getWorkerSession, disposeWorkerSession } from "./worker/session";
import { registerLanguageFeatures } from "./lang/providers";
import { registerConnectionFeatures, type ConnectionsApi } from "./connections/commands";
import { registerSecretBridge } from "./secrets/bridge";
import { registerSampleDataCommands } from "./commands/sample-data";
import { registerWorkspaceCommands } from "./commands/workspace-commands";
import { DataSourcesWatcher } from "./services/data-sources-watcher";
import { BackgroundOrchestrator } from "./services/background-orchestrator";
import { runHealthCheck, type HealthCheckResult, type HealthCheckDeps } from "./services/health-check";
import { createStatusBarController } from "./ui/status-bar";

export { runHealthCheck, type HealthCheckResult, type HealthCheckDeps };

export interface FsQueryStudioApi {
  connections: ConnectionsApi;
  background?: {
    isPaused: () => boolean;
    isDataSourcesWatcherActive: () => boolean;
    pause: (silent?: boolean) => Promise<void>;
    resume: (silent?: boolean) => Promise<void>;
    toggle: () => Promise<void>;
    startDataSourcesWatcher: () => void;
    stopDataSourcesWatcher: () => void;
  };
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
          spawn: () => spawnWorkerProcess(dotnetCommand, dll, vscode.workspace.workspaceFolders?.[0]?.uri.fsPath),
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

  const statusBarCtrl = createStatusBarController(log);
  const { statusBar, updateEntityMappingStatus, updateStagingProgress } = statusBarCtrl;

  let lastRegisteredClient: unknown = undefined;

  const dataSourcesWatcher = new DataSourcesWatcher({
    getWorker: () => getWorker(),
    log,
    isPaused: () => backgroundOrchestrator.isPaused(),
    statusBar,
  });

  const backgroundOrchestrator = new BackgroundOrchestrator({
    log,
    statusBar,
    onPause: async () => {
      dataSourcesWatcher.stop();
      disposeWorkerSession();
      lastRegisteredClient = undefined;
    },
    onResume: async () => {
      if (dataSourcesWatcher.isActive()) {
        dataSourcesWatcher.start();
        for (const folder of vscode.workspace.workspaceFolders ?? []) {
          void dataSourcesWatcher.autoImportAndSyncDataSources(folder.uri.fsPath, true);
        }
      }
      await vscode.commands.executeCommand("fs-query-studio.connections.refresh");
    },
  });

  const getWorker = async () => {
    if (backgroundOrchestrator.isPaused()) {
      log("[Host] Resuming paused worker for incoming request.");
      await backgroundOrchestrator.resume(true);
    }
    const session = await getWorkerSession({
      extensionRoot: context.extensionPath,
      env: process.env,
      cwd: vscode.workspace.workspaceFolders?.[0]?.uri.fsPath,
      log,
    });
    if (lastRegisteredClient !== session.client) {
      lastRegisteredClient = session.client;
      registerSecretBridge({
        client: session.client,
        secrets: context.secrets,
        onEntityMappingStatus: updateEntityMappingStatus,
        onStagingProgress: updateStagingProgress,
      });
    }
    return session.client;
  };

  const notebookSerializer = vscode.workspace.registerNotebookSerializer(
    FSQNB_NOTEBOOK_TYPE,
    new FsqnbSerializer(),
  );

  const connections = registerConnectionFeatures({
    getWorker,
    secrets: context.secrets,
    log,
    isPaused: () => backgroundOrchestrator.isPaused(),
    extensionUri: context.extensionUri,
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

  const controller = createFsQueryController({
    getWorker,
    log,
    onCellExecuted: (cell) => languageFeatures.refreshNotebook(cell.notebook),
  });

  const rendererMessaging = vscode.notebooks.createRendererMessaging(FSQUERY_RENDERER_ID);
  const rendererHost = registerRendererHost({
    messaging: rendererMessaging,
    getWorker,
    pickSaveFile: async (defaultName, documentUri) => {
      let defaultUri: vscode.Uri | undefined;
      try {
        if (documentUri && documentUri.startsWith("file:")) {
          const parsed = vscode.Uri.parse(documentUri);
          const dir = vscode.Uri.joinPath(parsed, "..");
          defaultUri = vscode.Uri.joinPath(dir, defaultName);
        } else {
          const wsFolder = vscode.workspace.workspaceFolders?.[0]?.uri;
          if (wsFolder) {
            defaultUri = vscode.Uri.joinPath(wsFolder, defaultName);
          }
        }
      } catch {
        defaultUri = undefined;
      }

      const picked = await vscode.window.showSaveDialog({
        defaultUri,
        filters: { CSV: ["csv"] },
        saveLabel: "Export",
      });
      return picked?.fsPath;
    },
    log,
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

  const sampleDataCommands = registerSampleDataCommands(context, {
    getWorker,
    log,
    startDataSourcesWatcher: () => dataSourcesWatcher.start(),
    runSyncDataSources: (targetDir, silent) => dataSourcesWatcher.runSyncDataSources(targetDir, silent),
    outputChannel,
  });

  const workspaceCommands = registerWorkspaceCommands({
    getWorker,
    log,
    dataSourcesWatcher,
    outputChannel,
    extensionUri: context.extensionUri,
  });

  context.subscriptions.push(
    command,
    notebookSerializer,
    controller,
    rendererHost,
    showOutputCommand,
    newNotebookCommand,
    newNotebookHereCommand,
    restartCommand,
    ...sampleDataCommands,
    ...workspaceCommands,
    backgroundOrchestrator,
    dataSourcesWatcher,
    languageFeatures,
    connections.disposable,
    outputChannel,
    statusBarCtrl,
  );

  return {
    connections: connections.api,
    background: {
      isPaused: () => backgroundOrchestrator.isPaused(),
      isDataSourcesWatcherActive: () => dataSourcesWatcher.isActive(),
      pause: (silent) => backgroundOrchestrator.pause(silent),
      resume: (silent) => backgroundOrchestrator.resume(silent),
      toggle: () => backgroundOrchestrator.toggle(),
      startDataSourcesWatcher: () => dataSourcesWatcher.start(),
      stopDataSourcesWatcher: () => dataSourcesWatcher.stop(),
    },
  };
}

export async function deactivate(): Promise<void> {
  disposeWorkerSession();
  outputChannel?.dispose();
  outputChannel = undefined;
}
