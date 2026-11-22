import * as vscode from "vscode";
import * as path from "node:path";
import { type StagingProgressEvent } from "../secrets/bridge";

export interface StatusBarController {
  statusBar: vscode.StatusBarItem;
  updateEntityMappingStatus: (status: "started" | "completed" | "failed") => void;
  updateStagingProgress: (event: StagingProgressEvent) => void;
  dispose: () => void;
}

export function createStatusBarController(log: (line: string) => void): StatusBarController {
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

  return {
    statusBar,
    updateEntityMappingStatus,
    updateStagingProgress,
    dispose: () => {
      if (resetStatusBarTimer) clearTimeout(resetStatusBarTimer);
      if (resetStagingStatusTimer) clearTimeout(resetStagingStatusTimer);
      statusBar.dispose();
    },
  };
}
