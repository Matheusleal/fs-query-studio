import * as vscode from "vscode";
import * as path from "node:path";
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
import { registerSecretBridge, type StagingProgressEvent } from "./secrets/bridge";

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

  const statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Right, 100);
  statusBar.command = "fs-query-studio.showOutput";
  statusBar.text = "$(database) F# Query Studio";
  statusBar.tooltip = "F# Query Studio: Show Output Logs";
  statusBar.show();

  let resetStatusBarTimer: NodeJS.Timeout | undefined;
  const updateEntityMappingStatus = (status: "started" | "completed" | "failed") => {
    if (resetStatusBarTimer) {
      clearTimeout(resetStatusBarTimer);
      resetStatusBarTimer = undefined;
    }
    if (status === "started") {
      statusBar.text = "$(sync~spin) Mapping entities...";
      statusBar.tooltip = "F# Query Studio: Mapping connection entities in background...";
      statusBar.show();
    } else if (status === "completed") {
      statusBar.text = "$(check) Entities mapped";
      statusBar.tooltip = "F# Query Studio: Strongly-typed entities mapped successfully";
      statusBar.show();
      resetStatusBarTimer = setTimeout(() => {
        statusBar.text = "$(database) F# Query Studio";
        statusBar.tooltip = "F# Query Studio: Show Output Logs";
      }, 3000);
    } else if (status === "failed") {
      statusBar.text = "$(error) Entity mapping failed";
      statusBar.tooltip = "F# Query Studio: Background entity generation failed";
      statusBar.show();
      resetStatusBarTimer = setTimeout(() => {
        statusBar.text = "$(database) F# Query Studio";
        statusBar.tooltip = "F# Query Studio: Show Output Logs";
      }, 5000);
    }
  };

  let resetStagingStatusTimer: NodeJS.Timeout | undefined;
  const updateStagingProgress = (event: StagingProgressEvent) => {
    const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
    const progressPrefix = event.currentIndex && event.totalCount
      ? `[${event.currentIndex}/${event.totalCount}] `
      : "";
    const name = event.tableName || (event.filePath ? path.basename(event.filePath) : "Data");

    if (resetStagingStatusTimer) {
      clearTimeout(resetStagingStatusTimer);
      resetStagingStatusTimer = undefined;
    }

    switch (event.phase) {
      case "Reading": {
        const text = isPt
          ? `$(sync~spin) ${progressPrefix}Lendo ${name}...`
          : `$(sync~spin) ${progressPrefix}Reading ${name}...`;
        statusBar.text = text;
        statusBar.tooltip = `F# Query Studio: ${event.message || text}`;
        statusBar.show();
        log(`[Cache] [Reading] ${progressPrefix}${name}: ${event.filePath || ""}`);
        break;
      }
      case "Writing": {
        const rowsText = typeof event.rowCount === "number" ? ` (${event.rowCount.toLocaleString()} rows)` : "";
        const text = isPt
          ? `$(sync~spin) ${progressPrefix}Gravando ${name}${rowsText}...`
          : `$(sync~spin) ${progressPrefix}Writing ${name}${rowsText}...`;
        statusBar.text = text;
        statusBar.tooltip = `F# Query Studio: ${event.message || text}`;
        statusBar.show();
        log(`[Cache] [Writing] ${progressPrefix}${name}: ${event.rowCount ?? 0} rows`);
        break;
      }
      case "Indexing": {
        const text = isPt
          ? `$(sync~spin) ${progressPrefix}Indexando ${name}...`
          : `$(sync~spin) ${progressPrefix}Indexing ${name}...`;
        statusBar.text = text;
        statusBar.tooltip = `F# Query Studio: ${event.message || text}`;
        statusBar.show();
        log(`[Cache] [Indexing] ${progressPrefix}${name}: creating B-tree indexes`);
        break;
      }
      case "Completed": {
        const rowsText = typeof event.rowCount === "number" ? ` (${event.rowCount.toLocaleString()} rows)` : "";
        const elapsedText = typeof event.elapsedMs === "number" ? ` in ${event.elapsedMs}ms` : "";
        const text = isPt
          ? `$(check) ${progressPrefix}Pronto: ${name}${rowsText}`
          : `$(check) ${progressPrefix}Ready: ${name}${rowsText}`;
        statusBar.text = text;
        statusBar.tooltip = `F# Query Studio: Staged ${name}${rowsText}${elapsedText}`;
        statusBar.show();
        log(`[Cache] [Completed] ${progressPrefix}${name}${rowsText}${elapsedText}`);
        resetStagingStatusTimer = setTimeout(() => {
          statusBar.text = "$(database) F# Query Studio";
          statusBar.tooltip = "F# Query Studio: Show Output Logs";
        }, 3500);
        break;
      }
      case "Skipped": {
        log(`[Cache] [Skipped] ${progressPrefix}${name}: up-to-date in cache`);
        break;
      }
      case "Failed": {
        const text = isPt
          ? `$(error) ${progressPrefix}Falha: ${name}`
          : `$(error) ${progressPrefix}Failed: ${name}`;
        statusBar.text = text;
        statusBar.tooltip = `F# Query Studio: ${event.message || text}`;
        statusBar.show();
        log(`[Cache] [Failed] ${progressPrefix}${name}: ${event.message}`);
        resetStagingStatusTimer = setTimeout(() => {
          statusBar.text = "$(database) F# Query Studio";
          statusBar.tooltip = "F# Query Studio: Show Output Logs";
        }, 5000);
        break;
      }
    }
  };

  let resetSyncStatusTimer: NodeJS.Timeout | undefined;
  const runSyncDataSources = async (targetDir?: string, silent = false): Promise<void> => {
    try {
      if (resetSyncStatusTimer) {
        clearTimeout(resetSyncStatusTimer);
        resetSyncStatusTimer = undefined;
      }
      statusBar.text = "$(sync~spin) Syncing data sources...";
      statusBar.tooltip = "F# Query Studio: Pre-staging CSV and Excel data into SQLite cache...";
      statusBar.show();

      const worker = await getWorker();
      const res = await worker.send({
        id: randomUUID(),
        method: "workspace/syncDataSources",
        params: { targetDir },
      });

      if (res.error) {
        log(`[Workspace] Sync data sources failed: ${res.error.message}`);
        statusBar.text = "$(error) Data sync failed";
        statusBar.tooltip = `F# Query Studio: Data sources sync failed: ${res.error.message}`;
        resetSyncStatusTimer = setTimeout(() => {
          statusBar.text = "$(database) F# Query Studio";
          statusBar.tooltip = "F# Query Studio: Show Output Logs";
        }, 5000);
        if (!silent) {
          void vscode.window.showErrorMessage(`Data sources sync failed: ${res.error.message}`);
        }
        return;
      }

      const summary = res.result as {
        targetDir: string;
        stagedCount: number;
        skippedCount: number;
        total: number;
        elapsedMs: number;
      };

      log(
        `[Workspace] Data sources synced: ${summary.stagedCount} staged, ${summary.skippedCount} up-to-date (total ${summary.total}) in ${summary.elapsedMs}ms`,
      );

      statusBar.text = "$(database) Data sources ready";
      statusBar.tooltip = `F# Query Studio: ${summary.total} sources synced (${summary.stagedCount} new/updated, ${summary.skippedCount} cached) in ${summary.elapsedMs}ms`;
      resetSyncStatusTimer = setTimeout(() => {
        statusBar.text = "$(database) F# Query Studio";
        statusBar.tooltip = "F# Query Studio: Show Output Logs";
      }, 4000);

      if (!silent) {
        const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
        const msg = isPt
          ? `Sincronização concluída: ${summary.stagedCount} arquivo(s) carregados, ${summary.skippedCount} já atualizados em cache (${summary.elapsedMs}ms).`
          : `Sync completed: ${summary.stagedCount} file(s) staged, ${summary.skippedCount} up-to-date in disk cache (${summary.elapsedMs}ms).`;
        void vscode.window.showInformationMessage(msg);
      }
    } catch (err) {
      const msg = (err as Error).message;
      log(`[Workspace] Error during sync: ${msg}`);
      statusBar.text = "$(error) Data sync error";
      resetSyncStatusTimer = setTimeout(() => {
        statusBar.text = "$(database) F# Query Studio";
        statusBar.tooltip = "F# Query Studio: Show Output Logs";
      }, 5000);
      if (!silent) {
        void vscode.window.showErrorMessage(`Error syncing data sources: ${msg}`);
      }
    }
  };

  const autoImportAndSyncDataSources = async (targetDir: string, silent = true): Promise<void> => {
    try {
      const worker = await getWorker();
      const res = await worker.send({
        id: randomUUID(),
        method: "workspace/autoImportDataSources",
        params: { targetDir },
      });

      if (res.error) {
        log(`[Watcher] Auto-import error: ${res.error.message}`);
        return;
      }

      const data = res.result as {
        targetDir: string;
        dataSourcesDir: string;
        folderCreated: boolean;
        foundFilesCount: number;
        importedCount: number;
        imported: Array<{ name: string; kind: string; filePath: string; relativePath: string }>;
        removedCount?: number;
        skippedCount: number;
      };

      const changesDetected = data.importedCount > 0 || (data.removedCount ?? 0) > 0;
      if (changesDetected) {
        log(
          `[Watcher] data_sources updated: ${data.importedCount} imported, ${data.removedCount ?? 0} removed in ${data.dataSourcesDir}`,
        );
        await vscode.commands.executeCommand("fs-query-studio.connections.refresh");

        if (!silent) {
          const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
          const msg = isPt
            ? `${data.importedCount} fonte(s) importada(s) automaticamente da pasta 'data_sources'.`
            : `Auto-imported ${data.importedCount} data source(s) from 'data_sources'.`;
          void vscode.window.showInformationMessage(msg);
        }
      }

      // Always trigger cache sync on file change/creation/deletion
      await runSyncDataSources(targetDir, true);
    } catch (err) {
      log(`[Watcher] Error auto-syncing data sources: ${(err as Error).message}`);
    }
  };

  let isPaused = false;
  let dataSourcesWatcherActive = false;
  let dataSourcesWatcher: vscode.Disposable | undefined;
  let lastRegisteredClient: unknown = undefined;

  void vscode.commands.executeCommand("setContext", "fs-query-studio.isPaused", false);

  const isDataSourcesCandidate = (filePath: string): boolean => {
    const baseName = path.basename(filePath);
    if (baseName.startsWith(".") || baseName.startsWith("~$") || baseName.toLowerCase() === "readme.txt") {
      return false;
    }
    const ext = path.extname(filePath).toLowerCase();
    return [".csv", ".xlsx", ".xls", ".sqlite", ".sqlite3", ".db"].includes(ext);
  };

  let dataSourcesDebounceTimer: NodeJS.Timeout | undefined;
  const onDataSourcesFileChanged = (uri: vscode.Uri) => {
    if (isPaused) {
      return;
    }
    if (!isDataSourcesCandidate(uri.fsPath)) {
      return;
    }
    if (dataSourcesDebounceTimer) {
      clearTimeout(dataSourcesDebounceTimer);
    }
    dataSourcesDebounceTimer = setTimeout(async () => {
      dataSourcesDebounceTimer = undefined;
      if (isPaused) {
        return;
      }
      const wsFolder = vscode.workspace.getWorkspaceFolder(uri);
      let targetDir = wsFolder?.uri.fsPath;
      if (!targetDir) {
        const parts = uri.fsPath.split(/[/\\]/);
        const dsIdx = parts.lastIndexOf("data_sources");
        if (dsIdx > 0) {
          targetDir = parts.slice(0, dsIdx).join(path.sep);
        } else {
          targetDir = path.dirname(uri.fsPath);
        }
      }
      log(`[Watcher] Change detected in data_sources (${path.basename(uri.fsPath)}). Auto-syncing: ${targetDir}`);
      await autoImportAndSyncDataSources(targetDir, true);
    }, 600);
  };

  const startDataSourcesWatcher = () => {
    if (dataSourcesWatcher) {
      return;
    }
    dataSourcesWatcherActive = true;
    const watcher = vscode.workspace.createFileSystemWatcher("**/data_sources/**");
    const d1 = watcher.onDidCreate(onDataSourcesFileChanged);
    const d2 = watcher.onDidChange(onDataSourcesFileChanged);
    const d3 = watcher.onDidDelete(onDataSourcesFileChanged);
    dataSourcesWatcher = {
      dispose() {
        d1.dispose();
        d2.dispose();
        d3.dispose();
        watcher.dispose();
      },
    };
    log("[Watcher] Automatic detection for 'data_sources' initialized and active.");
  };

  const stopDataSourcesWatcher = () => {
    if (dataSourcesDebounceTimer) {
      clearTimeout(dataSourcesDebounceTimer);
      dataSourcesDebounceTimer = undefined;
    }
    if (dataSourcesWatcher) {
      dataSourcesWatcher.dispose();
      dataSourcesWatcher = undefined;
      log("[Watcher] Automatic detection for 'data_sources' stopped.");
    }
  };

  const pauseWatchersAndWorkers = async (silent = false): Promise<void> => {
    if (isPaused) return;
    isPaused = true;
    await vscode.commands.executeCommand("setContext", "fs-query-studio.isPaused", true);
    stopDataSourcesWatcher();
    disposeWorkerSession();
    lastRegisteredClient = undefined;
    log("[Host] Watchers and workers paused; worker session disposed and RAM/CPU released.");

    statusBar.text = "$(play) F# Query: Paused";
    statusBar.tooltip = "F# Query Studio: Watchers and workers paused (RAM and CPU released). Click to resume.";
    statusBar.command = "fs-query-studio.resumeWatchersAndWorkers";
    statusBar.show();

    if (!silent) {
      const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
      const msg = isPt
        ? "F# Query Studio: Watchers e workers pausados. O processo em background foi encerrado e a memória liberada."
        : "F# Query Studio: Watchers and workers paused. Background process terminated and memory released.";
      void vscode.window.showInformationMessage(msg);
    }
  };

  const resumeWatchersAndWorkers = async (silent = false): Promise<void> => {
    if (!isPaused) return;
    isPaused = false;
    await vscode.commands.executeCommand("setContext", "fs-query-studio.isPaused", false);

    statusBar.text = "$(database) F# Query Studio";
    statusBar.tooltip = "F# Query Studio: Show Output Logs";
    statusBar.command = "fs-query-studio.showOutput";
    statusBar.show();

    log("[Host] Watchers and workers resumed.");

    if (dataSourcesWatcherActive) {
      startDataSourcesWatcher();
      for (const folder of vscode.workspace.workspaceFolders ?? []) {
        void autoImportAndSyncDataSources(folder.uri.fsPath, true);
      }
    }

    await vscode.commands.executeCommand("fs-query-studio.connections.refresh");

    if (!silent) {
      const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
      const msg = isPt
        ? "F# Query Studio: Watchers e workers retomados com sucesso."
        : "F# Query Studio: Watchers and workers resumed successfully.";
      void vscode.window.showInformationMessage(msg);
    }
  };

  const togglePauseResume = async (): Promise<void> => {
    if (isPaused) {
      await resumeWatchersAndWorkers();
    } else {
      await pauseWatchersAndWorkers();
    }
  };

  const getWorker = async () => {
    if (isPaused) {
      log("[Host] Resuming paused worker for incoming request.");
      await resumeWatchersAndWorkers(true);
    }
    const session = await getWorkerSession({
      extensionRoot: context.extensionPath,
      env: process.env,
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

  // Registered before language features so its `api.list` can feed
  // `Connections.sql "…"` name completion.
  const connections = registerConnectionFeatures({
    getWorker,
    secrets: context.secrets,
    log,
    isPaused: () => isPaused,
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
      const picked = await vscode.window.showSaveDialog({
        defaultUri: vscode.Uri.joinPath(vscode.Uri.parse(documentUri), "..", defaultName),
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
          sqlite2Path?: string;
          csvPath: string;
          csv2Path?: string;
          notebookPath: string;
          notebookPtPath?: string;
        };

        log(
          `[Workspace] Sample data created successfully: SQLite=${data.sqlitePath}, SQLite2=${data.sqlite2Path}, CSV=${data.csvPath}, CSV2=${data.csv2Path}, Notebook=${data.notebookPath}, NotebookPt=${data.notebookPtPath}`,
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
          ? "Bancos de dados SQLite ('sample_store.db', 'inventory.db'), CSVs ('sales_feed.csv', 'customers_feed.csv') e notebooks interativos ('sample_demo_pt.fsqnb', 'sample_demo.fsqnb') criados com sucesso!"
          : "Sample SQLite databases ('sample_store.db', 'inventory.db'), CSVs ('sales_feed.csv', 'customers_feed.csv'), and interactive notebooks ('sample_demo.fsqnb', 'sample_demo_pt.fsqnb') created successfully!";

        void runSyncDataSources(targetDir, true);
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

  const initEmptyProjectCommand = vscode.commands.registerCommand(
    "fs-query-studio.initEmptyProject",
    async (resource?: vscode.Uri) => {
      let targetDir: string | undefined;
      if (resource && resource.fsPath) {
        targetDir = resource.fsPath;
      } else {
        const wsFolders = vscode.workspace.workspaceFolders;
        if (wsFolders && wsFolders.length === 1) {
          targetDir = wsFolders[0].uri.fsPath;
        } else if (wsFolders && wsFolders.length > 1) {
          const pick = await vscode.window.showQuickPick(
            wsFolders.map((f) => ({ label: f.name, description: f.uri.fsPath, uri: f.uri })),
            { placeHolder: "Select a workspace folder to initialize empty project" },
          );
          if (!pick) return;
          targetDir = pick.uri.fsPath;
        } else {
          const picked = await vscode.window.showOpenDialog({
            canSelectFiles: false,
            canSelectFolders: true,
            canSelectMany: false,
            openLabel: "Select Folder to Initialize Empty Project",
          });
          if (!picked || picked.length === 0) return;
          targetDir = picked[0].fsPath;
        }
      }

      log(`[Workspace] Initializing empty project in directory: ${targetDir}`);
      try {
        const worker = await getWorker();
        const res = await worker.send({
          id: randomUUID(),
          method: "workspace/initEmptyProject",
          params: { targetDir },
        });

        if (res.error) {
          log(`[Workspace] Failed to initialize project: ${res.error.message}`);
          outputChannel?.show(true);
          void vscode.window.showErrorMessage(`Failed to initialize project: ${res.error.message}`);
          return;
        }

        log(`[Workspace] Empty project initialized in ${targetDir}`);
        await vscode.commands.executeCommand("fs-query-studio.connections.refresh");

        // Create a starter notebook if none exists
        const starterNbUri = vscode.Uri.joinPath(vscode.Uri.file(targetDir), "notebook.fsqnb");
        let exists = false;
        try {
          await vscode.workspace.fs.stat(starterNbUri);
          exists = true;
        } catch {
          exists = false;
        }

        if (!exists) {
          const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
          const starterContent = {
            version: 1,
            cells: [
              {
                kind: 1, // Markdown
                language: "markdown",
                value: isPt
                  ? "# Projeto F# Query Studio\nUse **Adicionar Conexão** ou crie consultas F# abaixo."
                  : "# F# Query Studio Project\nUse **Add Connection** or write F# queries below.",
              },
              {
                kind: 2, // Code
                language: "fsharp",
                value: isPt
                  ? "// Digite seu código F# ou consulte conexões aqui\nprintfn \"Pronto!\""
                  : "// Write your F# queries or reference connections here\nprintfn \"Ready!\"",
              },
            ],
          };
          await vscode.workspace.fs.writeFile(
            starterNbUri,
            Buffer.from(JSON.stringify(starterContent, null, 2), "utf8"),
          );
        }

        try {
          const doc = await vscode.workspace.openNotebookDocument(starterNbUri);
          await vscode.window.showNotebookDocument(doc);
        } catch {
          // ignore if notebook view fails
        }

        void vscode.window.showInformationMessage(
          `F# Query Studio: Empty project initialized in ${targetDir}`,
        );
      } catch (err) {
        const msg = (err as Error).message;
        log(`[Workspace] Error initializing empty project: ${msg}`);
        void vscode.window.showErrorMessage(`Error initializing empty project: ${msg}`);
      }
    },
  );

  const autoImportFolderCommand = vscode.commands.registerCommand(
    "fs-query-studio.autoImportFolder",
    async (resource?: vscode.Uri) => {
      let targetDir: string | undefined;
      if (resource && resource.fsPath) {
        try {
          const stat = await vscode.workspace.fs.stat(resource);
          if (stat.type === vscode.FileType.Directory) {
            targetDir = resource.fsPath;
          } else {
            targetDir = path.dirname(resource.fsPath);
          }
        } catch {
          targetDir = resource.fsPath;
        }
      } else {
        const wsFolders = vscode.workspace.workspaceFolders;
        if (wsFolders && wsFolders.length === 1) {
          targetDir = wsFolders[0].uri.fsPath;
        } else if (wsFolders && wsFolders.length > 1) {
          const pick = await vscode.window.showQuickPick(
            wsFolders.map((f) => ({ label: f.name, description: f.uri.fsPath, uri: f.uri })),
            { placeHolder: "Select a workspace folder to auto-import data sources" },
          );
          if (!pick) return;
          targetDir = pick.uri.fsPath;
        } else {
          const picked = await vscode.window.showOpenDialog({
            canSelectFiles: false,
            canSelectFolders: true,
            canSelectMany: false,
            openLabel: "Select Workspace Folder",
            title: "Select Workspace Root Folder for Data Sources Auto-Import",
          });
          if (!picked || picked.length === 0) return;
          targetDir = picked[0].fsPath;
        }
      }

      log(`[Workspace] Auto-importing data sources in: ${targetDir}`);
      try {
        const worker = await getWorker();
        const res = await worker.send({
          id: randomUUID(),
          method: "workspace/autoImportDataSources",
          params: { targetDir },
        });

        if (res.error) {
          log(`[Workspace] Failed to auto-import data sources: ${res.error.message}`);
          outputChannel?.show(true);
          void vscode.window.showErrorMessage(`Failed to auto-import data sources: ${res.error.message}`);
          return;
        }

        const data = res.result as {
          targetDir: string;
          dataSourcesDir: string;
          folderCreated: boolean;
          foundFilesCount: number;
          importedCount: number;
          imported: Array<{ name: string; kind: string; filePath: string; relativePath: string }>;
          skippedCount: number;
        };

        log(
          `[Workspace] Auto-import completed: ${data.importedCount} imported, ${data.skippedCount} skipped in ${data.dataSourcesDir}`,
        );

        // Start real-time automatic detection watcher for data_sources
        startDataSourcesWatcher();

        await vscode.commands.executeCommand("fs-query-studio.connections.refresh");

        const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
        if (data.importedCount > 0) {
          const names = data.imported.map((x) => x.name).join(", ");
          const msg = isPt
            ? `${data.importedCount} fonte(s) de dados importada(s) automaticamente da pasta 'data_sources': ${names}. Detecção automática em tempo real ativada.`
            : `Auto-imported ${data.importedCount} data source(s) from 'data_sources': ${names}. Real-time automatic detection is now active.`;
          void runSyncDataSources(targetDir, true);
          void vscode.window.showInformationMessage(msg);
        } else {
          const msg = isPt
            ? `Pasta 'data_sources' pronta em '${data.dataSourcesDir}'. Detecção automática ativada: qualquer arquivo .csv, .xlsx, .db ou .sqlite adicionado nela será detectado e sincronizado automaticamente.`
            : `Folder 'data_sources' is ready at '${data.dataSourcesDir}'. Real-time detection active: place your .csv, .xlsx, or .sqlite files there and they will be auto-imported.`;
          const actionText = isPt ? "Mostrar no Explorador" : "Reveal in Explorer";
          const choice = await vscode.window.showInformationMessage(msg, actionText);
          if (choice === actionText) {
            const folderUri = vscode.Uri.file(data.dataSourcesDir);
            await vscode.commands.executeCommand("revealInExplorer", folderUri);
          }
        }
      } catch (err) {
        const msg = (err as Error).message;
        log(`[Workspace] Error auto-importing data sources: ${msg}`);
        void vscode.window.showErrorMessage(`Error auto-importing data sources: ${msg}`);
      }
    },
  );

  const upgradeProjectCommand = vscode.commands.registerCommand(
    "fs-query-studio.upgradeProject",
    async (resource?: vscode.Uri) => {
      let targetDir: string | undefined;
      if (resource && resource.fsPath) {
        targetDir = resource.fsPath;
      } else {
        const wsFolders = vscode.workspace.workspaceFolders;
        if (wsFolders && wsFolders.length === 1) {
          targetDir = wsFolders[0].uri.fsPath;
        } else if (wsFolders && wsFolders.length > 1) {
          const pick = await vscode.window.showQuickPick(
            wsFolders.map((f) => ({ label: f.name, description: f.uri.fsPath, uri: f.uri })),
            { placeHolder: "Select a workspace folder to upgrade" },
          );
          if (!pick) return;
          targetDir = pick.uri.fsPath;
        } else {
          const picked = await vscode.window.showOpenDialog({
            canSelectFiles: false,
            canSelectFolders: true,
            canSelectMany: false,
            openLabel: "Select Project Folder to Upgrade",
          });
          if (!picked || picked.length === 0) return;
          targetDir = picked[0].fsPath;
        }
      }

      log(`[Workspace] Upgrading project in directory: ${targetDir}`);
      try {
        const worker = await getWorker();
        const res = await worker.send({
          id: randomUUID(),
          method: "workspace/upgrade",
          params: { workspaceDir: targetDir },
        });

        if (res.error) {
          log(`[Workspace] Failed to upgrade project: ${res.error.message}`);
          void vscode.window.showErrorMessage(`Failed to upgrade project: ${res.error.message}`);
          return;
        }

        const report = res.result as { upgraded: boolean; currentVersion: number; actions: string[] };
        if (report.upgraded) {
          log(`[Workspace] Project upgraded to Schema v${report.currentVersion}: ${report.actions.join("; ")}`);
          void vscode.window.showInformationMessage(
            `F# Query Studio: Project upgraded to Schema v${report.currentVersion}.`,
          );
          await vscode.commands.executeCommand("fs-query-studio.connections.refresh");
        } else {
          log(`[Workspace] Project is already up-to-date (Schema v${report.currentVersion}).`);
          void vscode.window.showInformationMessage(
            `F# Query Studio: Project is already up-to-date (Schema v${report.currentVersion}).`,
          );
        }
      } catch (err) {
        const msg = (err as Error).message;
        log(`[Workspace] Error upgrading project: ${msg}`);
        void vscode.window.showErrorMessage(`Error upgrading project: ${msg}`);
      }
    },
  );

  const syncDataSourcesCommand = vscode.commands.registerCommand(
    "fs-query-studio.syncDataSources",
    async (resource?: vscode.Uri) => {
      let targetDir: string | undefined;
      if (resource && resource.fsPath) {
        targetDir = resource.fsPath;
      } else {
        const wsFolders = vscode.workspace.workspaceFolders;
        if (wsFolders && wsFolders.length === 1) {
          targetDir = wsFolders[0].uri.fsPath;
        } else if (wsFolders && wsFolders.length > 1) {
          const pick = await vscode.window.showQuickPick(
            wsFolders.map((f) => ({ label: f.name, description: f.uri.fsPath, uri: f.uri })),
            { placeHolder: "Select a workspace folder to sync data sources cache" },
          );
          if (!pick) return;
          targetDir = pick.uri.fsPath;
        } else {
          const picked = await vscode.window.showOpenDialog({
            canSelectFiles: false,
            canSelectFolders: true,
            canSelectMany: false,
            openLabel: "Select Workspace Folder to Sync",
          });
          if (!picked || picked.length === 0) return;
          targetDir = picked[0].fsPath;
        }
      }
      await runSyncDataSources(targetDir, false);
    },
  );

  const pauseCommand = vscode.commands.registerCommand(
    "fs-query-studio.pauseWatchersAndWorkers",
    () => pauseWatchersAndWorkers(),
  );

  const resumeCommand = vscode.commands.registerCommand(
    "fs-query-studio.resumeWatchersAndWorkers",
    () => resumeWatchersAndWorkers(),
  );

  const togglePauseResumeCommand = vscode.commands.registerCommand(
    "fs-query-studio.togglePauseResume",
    () => togglePauseResume(),
  );

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
    autoImportFolderCommand,
    syncDataSourcesCommand,
    initEmptyProjectCommand,
    upgradeProjectCommand,
    openDocsCommand,
    pauseCommand,
    resumeCommand,
    togglePauseResumeCommand,
    languageFeatures,
    connections.disposable,
    outputChannel,
    statusBar,
    {
      dispose: () => {
        stopDataSourcesWatcher();
      },
    },
  );

  return {
    connections: connections.api,
    background: {
      isPaused: () => isPaused,
      isDataSourcesWatcherActive: () => dataSourcesWatcherActive,
      pause: pauseWatchersAndWorkers,
      resume: resumeWatchersAndWorkers,
      toggle: togglePauseResume,
      startDataSourcesWatcher,
      stopDataSourcesWatcher,
    },
  };
}

export async function deactivate(): Promise<void> {
  disposeWorkerSession();
  outputChannel?.dispose();
  outputChannel = undefined;
}
