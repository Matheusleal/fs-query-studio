import * as vscode from "vscode";
import * as path from "node:path";
import { randomUUID } from "node:crypto";
import type { WorkerResponseFrame } from "../protocol";

export type WorkerLike = {
  send: (req: { id: string; method: string; params: unknown }) => Promise<WorkerResponseFrame>;
};

export interface DataSourcesWatcherDeps {
  getWorker: () => Promise<WorkerLike>;
  log: (line: string) => void;
  isPaused: () => boolean;
  onRefreshConnections?: () => Promise<void>;
  statusBar?: vscode.StatusBarItem;
  debounceMs?: number;
}

export function isDataSourcesCandidate(filePath: string): boolean {
  const baseName = path.basename(filePath);
  if (baseName.startsWith(".") || baseName.startsWith("~$") || baseName.toLowerCase() === "readme.txt") {
    return false;
  }
  const ext = path.extname(filePath).toLowerCase();
  return [".csv", ".xlsx", ".xls", ".json", ".xml", ".sqlite", ".sqlite3", ".db"].includes(ext);
}

export function resolveTargetDirFromUri(uri: vscode.Uri | { fsPath: string }): string {
  const wsFolder = typeof vscode.workspace.getWorkspaceFolder === "function"
    ? vscode.workspace.getWorkspaceFolder(uri as vscode.Uri)
    : undefined;
  if (wsFolder?.uri.fsPath) {
    return wsFolder.uri.fsPath;
  }
  const parts = uri.fsPath.split(/[/\\]/);
  const dsIdx = parts.lastIndexOf("data_sources");
  if (dsIdx > 0) {
    return parts.slice(0, dsIdx).join(path.sep);
  }
  return path.dirname(uri.fsPath);
}

export class DataSourcesWatcher implements vscode.Disposable {
  private active = false;
  private watcherDisposable: vscode.Disposable | undefined;
  private debounceTimer: NodeJS.Timeout | undefined;
  private resetSyncStatusTimer: NodeJS.Timeout | undefined;
  private readonly debounceMs: number;

  constructor(private readonly deps: DataSourcesWatcherDeps) {
    this.debounceMs = deps.debounceMs ?? 600;
  }

  public isActive(): boolean {
    return this.active;
  }

  public start(): void {
    this.active = true;
    if (this.watcherDisposable) {
      return;
    }
    const watcher = vscode.workspace.createFileSystemWatcher("**/data_sources/**");
    const d1 = watcher.onDidCreate((uri) => this.onDataSourcesFileChanged(uri));
    const d2 = watcher.onDidChange((uri) => this.onDataSourcesFileChanged(uri));
    const d3 = watcher.onDidDelete((uri) => this.onDataSourcesFileChanged(uri));

    this.watcherDisposable = {
      dispose() {
        d1.dispose();
        d2.dispose();
        d3.dispose();
        watcher.dispose();
      },
    };
    this.deps.log("[Watcher] Automatic detection for 'data_sources' initialized and active.");
  }

  public stop(): void {
    if (this.debounceTimer) {
      clearTimeout(this.debounceTimer);
      this.debounceTimer = undefined;
    }
    if (this.watcherDisposable) {
      this.watcherDisposable.dispose();
      this.watcherDisposable = undefined;
      this.deps.log("[Watcher] Automatic detection for 'data_sources' stopped.");
    }
  }

  public dispose(): void {
    this.active = false;
    this.stop();
    if (this.resetSyncStatusTimer) {
      clearTimeout(this.resetSyncStatusTimer);
      this.resetSyncStatusTimer = undefined;
    }
  }

  private onDataSourcesFileChanged(uri: vscode.Uri): void {
    if (this.deps.isPaused()) {
      return;
    }
    if (!isDataSourcesCandidate(uri.fsPath)) {
      return;
    }
    if (this.debounceTimer) {
      clearTimeout(this.debounceTimer);
    }
    this.debounceTimer = setTimeout(async () => {
      this.debounceTimer = undefined;
      if (this.deps.isPaused()) {
        return;
      }
      const targetDir = resolveTargetDirFromUri(uri);
      this.deps.log(
        `[Watcher] Change detected in data_sources (${path.basename(uri.fsPath)}). Auto-syncing: ${targetDir}`,
      );
      await this.autoImportAndSyncDataSources(targetDir, true);
    }, this.debounceMs);
  }

  public async autoImportAndSyncDataSources(targetDir: string, silent = true): Promise<void> {
    try {
      const worker = await this.deps.getWorker();
      const res = await worker.send({
        id: randomUUID(),
        method: "workspace/autoImportDataSources",
        params: { targetDir },
      });

      if (res.error) {
        this.deps.log(`[Watcher] Auto-import error: ${res.error.message}`);
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
        this.deps.log(
          `[Watcher] data_sources updated: ${data.importedCount} imported, ${data.removedCount ?? 0} removed in ${data.dataSourcesDir}`,
        );
        if (this.deps.onRefreshConnections) {
          await this.deps.onRefreshConnections();
        } else {
          await vscode.commands.executeCommand("fs-query-studio.connections.refresh");
        }

        if (!silent) {
          const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
          const msg = isPt
            ? `${data.importedCount} fonte(s) importada(s) automaticamente da pasta 'data_sources'.`
            : `Auto-imported ${data.importedCount} data source(s) from 'data_sources'.`;
          void vscode.window.showInformationMessage(msg);
        }
      }

      // Always trigger cache sync on file change/creation/deletion
      await this.runSyncDataSources(targetDir, true);
    } catch (err) {
      this.deps.log(`[Watcher] Error auto-syncing data sources: ${(err as Error).message}`);
    }
  }

  public async runSyncDataSources(targetDir?: string, silent = false): Promise<void> {
    const statusBar = this.deps.statusBar;
    try {
      if (this.resetSyncStatusTimer) {
        clearTimeout(this.resetSyncStatusTimer);
        this.resetSyncStatusTimer = undefined;
      }
      if (statusBar) {
        statusBar.text = "$(sync~spin) Syncing data sources...";
        statusBar.tooltip = "F# Query Studio: Pre-staging CSV and Excel data into SQLite cache...";
        statusBar.show();
      }

      const worker = await this.deps.getWorker();
      const res = await worker.send({
        id: randomUUID(),
        method: "workspace/syncDataSources",
        params: { targetDir },
      });

      if (res.error) {
        this.deps.log(`[Workspace] Sync data sources failed: ${res.error.message}`);
        if (statusBar) {
          statusBar.text = "$(error) Data sync failed";
          statusBar.tooltip = `F# Query Studio: Data sources sync failed: ${res.error.message}`;
          this.resetSyncStatusTimer = setTimeout(() => {
            statusBar.text = "$(database) F# Query Studio";
            statusBar.tooltip = "F# Query Studio: Show Output Logs";
          }, 5000);
        }
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

      this.deps.log(
        `[Workspace] Data sources synced: ${summary.stagedCount} staged, ${summary.skippedCount} up-to-date (total ${summary.total}) in ${summary.elapsedMs}ms`,
      );

      if (statusBar) {
        statusBar.text = "$(database) Data sources ready";
        statusBar.tooltip = `F# Query Studio: ${summary.total} sources synced (${summary.stagedCount} new/updated, ${summary.skippedCount} cached) in ${summary.elapsedMs}ms`;
        this.resetSyncStatusTimer = setTimeout(() => {
          statusBar.text = "$(database) F# Query Studio";
          statusBar.tooltip = "F# Query Studio: Show Output Logs";
        }, 4000);
      }

      if (!silent) {
        const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
        const msg = isPt
          ? `Sincronização concluída: ${summary.stagedCount} arquivo(s) carregados, ${summary.skippedCount} já atualizados em cache (${summary.elapsedMs}ms).`
          : `Sync completed: ${summary.stagedCount} file(s) staged, ${summary.skippedCount} up-to-date in disk cache (${summary.elapsedMs}ms).`;
        void vscode.window.showInformationMessage(msg);
      }
    } catch (err) {
      const msg = (err as Error).message;
      this.deps.log(`[Workspace] Error during sync: ${msg}`);
      if (statusBar) {
        statusBar.text = "$(error) Data sync error";
        this.resetSyncStatusTimer = setTimeout(() => {
          statusBar.text = "$(database) F# Query Studio";
          statusBar.tooltip = "F# Query Studio: Show Output Logs";
        }, 5000);
      }
      if (!silent) {
        void vscode.window.showErrorMessage(`Error syncing data sources: ${msg}`);
      }
    }
  }
}
