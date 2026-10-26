import * as vscode from "vscode";
import { FSHARP_CELL_SELECTOR } from "./providers";

/**
 * The one editor position where this extension's own registered
 * connections — not FSAC's identifier completion, which has nothing to
 * say inside a string literal — are the useful completion source: the
 * first, still-open string argument of
 * `Connections.sql/csv/excel/rest "…"` (the controlled connection API,
 * see `FSharpQueryStudio.Connectors.Connections`), where a script author
 * would otherwise type a connection's name from memory.
 *
 * Ported from the retired Avalonia `ConnectionNameCompletion.fs`
 * (deleted at F6.2). Single-line only — a string that wraps across lines
 * falls through, matching the original.
 *
 * `"[^"]*$` anchors to the end of the line prefix being tested: "cursor
 * is here, inside the quotes, no closing quote yet". `\(?` covers both
 * `Connections.sql "…` and `Connections.sql("…`.
 */
const PATTERN = /Connections\.(?:sql|csv|excel|rest|connect|get|query|queryCsv|queryExcel|getConnectionString)\s*\(?\s*"[^"]*$/;

/** Pure. `linePrefix` is the text of the cursor's line up to the cursor. */
export function isInsideConnectionNameArgument(linePrefix: string): boolean {
  return PATTERN.test(linePrefix);
}

export interface ConnectionNameCompletionDeps {
  /** Registered connection names, freshest available. Queried only when
   *  the cursor is actually inside a connection-name argument. */
  listConnectionNames: () => Promise<string[]>;
}

function linePrefixAt(text: string, position: vscode.Position): string {
  const line = text.split(/\r?\n/)[position.line] ?? "";
  return line.slice(0, position.character);
}

/**
 * A completion provider that answers only inside
 * `Connections.(sql|csv|excel|rest) "…"` and returns `undefined`
 * everywhere else, so it never competes with the FSAC-forwarding
 * provider in `providers.ts`.
 */
export function registerConnectionNameCompletion(
  deps: ConnectionNameCompletionDeps,
): vscode.Disposable {
  return vscode.languages.registerCompletionItemProvider(
    FSHARP_CELL_SELECTOR,
    {
      async provideCompletionItems(document, position, token) {
        const prefix = linePrefixAt(document.getText(), position);
        if (!isInsideConnectionNameArgument(prefix) || token.isCancellationRequested) {
          return undefined;
        }
        const names = await deps.listConnectionNames();
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
