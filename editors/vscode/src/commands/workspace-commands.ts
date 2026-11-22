import * as vscode from "vscode";
import * as path from "node:path";
import { randomUUID } from "node:crypto";
import { WorkerClient } from "../worker/client";
import { DataSourcesWatcher } from "../services/data-sources-watcher";

export interface WorkspaceCommandsDeps {
  getWorker: () => Promise<WorkerClient>;
  log: (line: string) => void;
  dataSourcesWatcher: DataSourcesWatcher;
  outputChannel?: vscode.OutputChannel;
  extensionUri: vscode.Uri;
}

export function registerWorkspaceCommands(deps: WorkspaceCommandsDeps): vscode.Disposable[] {
  const { getWorker, log, dataSourcesWatcher, outputChannel, extensionUri } = deps;

  const openDocsCommand = vscode.commands.registerCommand(
    "fs-query-studio.openDocs",
    async () => {
      const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
      const primaryDoc = isPt ? "USER_GUIDE_PT.md" : "USER_GUIDE.md";
      const secondaryDoc = isPt ? "USER_GUIDE.md" : "USER_GUIDE_PT.md";
      const candidates = [
        vscode.Uri.joinPath(extensionUri, "docs", primaryDoc),
        vscode.Uri.joinPath(extensionUri, primaryDoc),
        vscode.Uri.joinPath(extensionUri, "docs", secondaryDoc),
        vscode.Uri.joinPath(extensionUri, secondaryDoc),
        vscode.Uri.joinPath(extensionUri, "docs", "README.md"),
        vscode.Uri.joinPath(extensionUri, "README.md"),
        vscode.Uri.joinPath(extensionUri, "readme.md"),
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
        dataSourcesWatcher.start();

        await vscode.commands.executeCommand("fs-query-studio.connections.refresh");

        const isPt = vscode.env.language?.toLowerCase().startsWith("pt") ?? false;
        if (data.importedCount > 0) {
          const names = data.imported.map((x) => x.name).join(", ");
          const msg = isPt
            ? `${data.importedCount} fonte(s) de dados importada(s) automaticamente da pasta 'data_sources': ${names}. Detecção automática em tempo real ativada.`
            : `Auto-imported ${data.importedCount} data source(s) from 'data_sources': ${names}. Real-time automatic detection is now active.`;
          void dataSourcesWatcher.runSyncDataSources(targetDir, true);
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
      await dataSourcesWatcher.runSyncDataSources(targetDir, false);
    },
  );

  return [
    openDocsCommand,
    initEmptyProjectCommand,
    autoImportFolderCommand,
    upgradeProjectCommand,
    syncDataSourcesCommand,
  ];
}
