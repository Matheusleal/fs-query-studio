import * as vscode from "vscode";
import { FSHARP_CELL_SELECTOR } from "./providers";
import type { ConnectorKindName } from "../protocol";

export type TargetConnectorCategory = "Database" | "Csv" | "Excel" | "RestJson";

export interface ConnectionItem {
  name: string;
  kind?: ConnectorKindName;
}

/**
 * Returns the target connection category for the function call before the cursor,
 * or `undefined` if the cursor is not inside a recognized connection-name string argument.
 */
export function getTargetConnectorCategory(linePrefix: string): TargetConnectorCategory | undefined {
  if (/Connections\.(?:sql|connect|get|query|getConnectionString)\b\s*\(?\s*"[^"]*$/.test(linePrefix)) {
    return "Database";
  }
  if (/Connections\.(?:csv|connectCsv|queryCsv)\b\s*\(?\s*"[^"]*$/.test(linePrefix)) {
    return "Csv";
  }
  if (/Connections\.(?:excel|connectExcel|queryExcel|queryExcelSheet)\b\s*\(?\s*"[^"]*$/.test(linePrefix)) {
    return "Excel";
  }
  if (/Connections\.(?:rest|connectRest|queryJson)\b\s*\(?\s*"[^"]*$/.test(linePrefix)) {
    return "RestJson";
  }
  return undefined;
}

/** Pure. `linePrefix` is the text of the cursor's line up to the cursor. */
export function isInsideConnectionNameArgument(linePrefix: string): boolean {
  return getTargetConnectorCategory(linePrefix) !== undefined;
}

export interface ConnectionNameCompletionDeps {
  /** Registered connections with their kinds, freshest available. Filtered to match the target API kind. */
  listConnections?: () => Promise<ConnectionItem[]>;
  /** Fallback: registered connection names. Queried only when `listConnections` is not supplied. */
  listConnectionNames?: () => Promise<string[]>;
}

function linePrefixAt(text: string, position: vscode.Position): string {
  const line = text.split(/\r?\n/)[position.line] ?? "";
  return line.slice(0, position.character);
}

/**
 * A completion provider that answers only inside
 * `Connections.(sql|csv|excel|rest|...) "…"` and returns `undefined`
 * everywhere else, so it never competes with the FSAC-forwarding
 * provider in `providers.ts`.
 *
 * Automatically filters completions based on the specific connection method being called
 * (e.g. database-only for `Connections.connect`/`sql`, CSV-only for `Connections.connectCsv`/`csv`, etc.).
 */
export function registerConnectionNameCompletion(
  deps: ConnectionNameCompletionDeps,
): vscode.Disposable {
  return vscode.languages.registerCompletionItemProvider(
    FSHARP_CELL_SELECTOR,
    {
      async provideCompletionItems(document, position, token) {
        const prefix = linePrefixAt(document.getText(), position);
        const category = getTargetConnectorCategory(prefix);
        if (!category || token.isCancellationRequested) {
          return undefined;
        }

        if (deps.listConnections) {
          const all = await deps.listConnections();
          if (token.isCancellationRequested) {
            return undefined;
          }
          const filtered = all.filter((c) => {
            if (!c.kind) {
              return true;
            }
            switch (category) {
              case "Database":
                return c.kind === "SqlServer" || c.kind === "PostgreSql" || c.kind === "Sqlite";
              case "Csv":
                return c.kind === "Csv";
              case "Excel":
                return c.kind === "Excel";
              case "RestJson":
                return c.kind === "RestJson";
            }
          });
          return filtered.map((c) => {
            const item = new vscode.CompletionItem(
              c.kind ? { label: c.name, description: c.kind } : c.name,
              vscode.CompletionItemKind.Value,
            );
            if (c.kind) {
              item.detail = c.kind;
            }
            return item;
          });
        }

        const names = deps.listConnectionNames ? await deps.listConnectionNames() : [];
        if (token.isCancellationRequested) {
          return undefined;
        }
        // The cursor is already between the quotes, so the insert text is
        // the bare name — matching the retired Avalonia behaviour.
        return names.map(
          (name) => new vscode.CompletionItem(name, vscode.CompletionItemKind.Value),
        );
      },
    },
    '"',
  );
}
