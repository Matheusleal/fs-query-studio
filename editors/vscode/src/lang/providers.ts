import * as vscode from "vscode";
import {
  analysisDocumentUri,
  buildAnalysisInput,
  versionFor,
  type AnalysisCell,
  type CellDiagnostic,
} from "./cell-context";
import {
  completeAnalysis,
  hoverAnalysis,
  signatureHelpAnalysis,
  type WorkerLike,
} from "./analysis-client";
import { DiagnosticsManager } from "./diagnostics";

export const FSHARP_CELL_SELECTOR = {
  language: "fsharp",
  notebookType: "fsquery-notebook",
} as const;

const DIAGNOSTIC_SOURCE = "F# Query Studio";

export interface LanguageFeaturesDeps {
  getWorker: () => Promise<WorkerLike>;
  log: (line: string) => void;
}

interface ResolvedCell {
  cells: AnalysisCell[];
  target: AnalysisCell;
}

/** Find the notebook cell backed by `doc`, plus its sibling cells. */
function resolveCell(doc: { uri: { toString(): string } }): ResolvedCell | undefined {
  const uri = doc.uri.toString();
  for (const notebook of vscode.workspace.notebookDocuments) {
    if (notebook.notebookType !== FSHARP_CELL_SELECTOR.notebookType) {
      continue;
    }
    const cells = notebook.getCells() as unknown as AnalysisCell[];
    const target = cells.find((c) => c.document.uri.toString() === uri);
    if (target) {
      return { cells, target };
    }
  }
  return undefined;
}

function isFsharpCell(doc: {
  uri: { scheme?: string; toString(): string };
  languageId: string;
}): boolean {
  return doc.uri.scheme === "vscode-notebook-cell" && doc.languageId === "fsharp";
}

function severityOf(s: CellDiagnostic["severity"]): vscode.DiagnosticSeverity {
  switch (s) {
    case "Warning":
      return vscode.DiagnosticSeverity.Warning;
    case "Information":
      return vscode.DiagnosticSeverity.Information;
    default:
      return vscode.DiagnosticSeverity.Error;
  }
}

function toVsDiagnostic(d: CellDiagnostic): vscode.Diagnostic {
  const diag = new vscode.Diagnostic(
    new vscode.Range(d.startLine, d.startColumn, d.endLine, d.endColumn),
    d.message,
    severityOf(d.severity),
  );
  if (d.code !== null) {
    diag.code = d.code;
  }
  diag.source = DIAGNOSTIC_SOURCE;
  return diag;
}

function hoverMarkdown(info: {
  displayName: string;
  type: string;
  signature: string | null;
  xmlDoc: string | null;
}): vscode.MarkdownString {
  const md = new vscode.MarkdownString();
  md.appendCodeblock(info.signature ?? `${info.displayName} : ${info.type}`, "fsharp");
  if (info.xmlDoc) {
    md.appendMarkdown(`\n${info.xmlDoc}`);
  }
  return md;
}

export function registerLanguageFeatures(deps: LanguageFeaturesDeps): vscode.Disposable {
  const completion = vscode.languages.registerCompletionItemProvider(
    FSHARP_CELL_SELECTOR,
    {
      async provideCompletionItems(document, position, token) {
        const resolved = resolveCell(document);
        if (!resolved || token.isCancellationRequested) {
          return undefined;
        }
        const { source, line, column } = buildAnalysisInput(resolved.cells, resolved.target, {
          line: position.line,
          character: position.character,
        });
        const version = versionFor(document.uri.toString(), source);
        const items = await completeAnalysis(await deps.getWorker(), {
          documentUri: analysisDocumentUri(document.uri.toString()),
          version,
          source,
          line,
          column,
        });
        return items.map(
          (name) => new vscode.CompletionItem(name, vscode.CompletionItemKind.Value),
        );
      },
    },
    ".",
  );

  const hover = vscode.languages.registerHoverProvider(FSHARP_CELL_SELECTOR, {
    async provideHover(document, position, token) {
      const resolved = resolveCell(document);
      if (!resolved || token.isCancellationRequested) {
        return undefined;
      }
      const { source, line, column } = buildAnalysisInput(resolved.cells, resolved.target, {
        line: position.line,
        character: position.character,
      });
      const version = versionFor(document.uri.toString(), source);
      const info = await hoverAnalysis(await deps.getWorker(), {
        documentUri: analysisDocumentUri(document.uri.toString()),
        version,
        source,
        line,
        column,
      });
      return info ? new vscode.Hover(hoverMarkdown(info)) : undefined;
    },
  });

  const signature = vscode.languages.registerSignatureHelpProvider(
    FSHARP_CELL_SELECTOR,
    {
      async provideSignatureHelp(document, position, token) {
        const resolved = resolveCell(document);
        if (!resolved || token.isCancellationRequested) {
          return undefined;
        }
        const { source, line, column } = buildAnalysisInput(resolved.cells, resolved.target, {
          line: position.line,
          character: position.character,
        });
        const version = versionFor(document.uri.toString(), source);
        const info = await signatureHelpAnalysis(await deps.getWorker(), {
          documentUri: analysisDocumentUri(document.uri.toString()),
          version,
          source,
          line,
          column,
        });
        if (!info) {
          return undefined;
        }
        const sig = new vscode.SignatureInformation(
          `${info.displayName}(${info.parameters.join(", ")}) : ${info.returnType}`,
        );
        sig.parameters = info.parameters.map((p) => new vscode.ParameterInformation(p));
        const help = new vscode.SignatureHelp();
        help.signatures = [sig];
        help.activeSignature = 0;
        help.activeParameter = 0;
        return help;
      },
    },
    "(",
    ",",
  );

  const collection = vscode.languages.createDiagnosticCollection(DIAGNOSTIC_SOURCE);
  const manager = new DiagnosticsManager({
    getWorker: deps.getWorker,
    log: deps.log,
    findNotebookOf: (cellUri) => {
      const resolved = resolveCell({ uri: { toString: () => cellUri } });
      return resolved?.cells;
    },
    publish: (cellUri, diags) =>
      collection.set(vscode.Uri.parse(cellUri), diags.map(toVsDiagnostic)),
    clear: (cellUri) => collection.delete(vscode.Uri.parse(cellUri)),
  });

  const onChange = vscode.workspace.onDidChangeTextDocument((e) => {
    if (isFsharpCell(e.document)) {
      manager.onCellChanged(e.document.uri.toString());
    }
  });
  const onOpen = vscode.workspace.onDidOpenTextDocument((doc) => {
    if (isFsharpCell(doc)) {
      manager.onCellOpened(doc.uri.toString());
    }
  });
  const onClose = vscode.workspace.onDidCloseTextDocument((doc) => {
    if (isFsharpCell(doc)) {
      manager.onCellClosed(doc.uri.toString());
    }
  });
  const onNotebookChange = vscode.workspace.onDidChangeNotebookDocument((e) => {
    for (const change of e.contentChanges ?? []) {
      for (const cell of change.removedCells ?? []) {
        manager.onCellClosed(cell.document.uri.toString());
      }
    }
  });

  return vscode.Disposable.from(
    completion,
    hover,
    signature,
    collection,
    { dispose: () => manager.dispose() },
    onChange,
    onOpen,
    onClose,
    onNotebookChange,
  );
}
