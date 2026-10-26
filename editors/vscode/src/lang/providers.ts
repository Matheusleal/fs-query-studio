import * as vscode from "vscode";
import {
  analysisDocumentUri,
  buildAnalysisInput,
  versionFor,
  CODE_CELL_KIND,
  type AnalysisCell,
  type CellDiagnostic,
} from "./cell-context";
import {
  completeAnalysis,
  definitionAnalysis,
  hoverAnalysis,
  renameAnalysis,
  signatureHelpAnalysis,
  type WorkerLike,
} from "./analysis-client";
import { DiagnosticsManager } from "./diagnostics";
import { registerConnectionNameCompletion } from "./connection-name-completion";

export const FSHARP_CELL_SELECTOR = {
  language: "fsharp",
  notebookType: "fsquery-notebook",
} as const;

const DIAGNOSTIC_SOURCE = "F# Query Studio";

export interface LanguageFeaturesDeps {
  getWorker: () => Promise<WorkerLike>;
  log: (line: string) => void;
  /** Registered connection names, for `Connections.sql "…"` completion.
   *  Absent in tests that don't exercise that provider. */
  listConnectionNames?: () => Promise<string[]>;
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

function mapSourcePositionToCell(
  cells: readonly AnalysisCell[],
  target: AnalysisCell,
  sourceLine: number,
  sourceColumn: number,
): { uri: vscode.Uri; position: vscode.Position } {
  const targetUri = target.document.uri.toString();
  const prefixCells = cells
    .filter(
      (c) =>
        c.kind === CODE_CELL_KIND &&
        c.document.languageId === "fsharp" &&
        c.document.uri.toString() !== targetUri &&
        c.executionSummary?.executionOrder != null,
    )
    .sort(
      (a, b) =>
        (a.executionSummary!.executionOrder ?? 0) - (b.executionSummary!.executionOrder ?? 0),
    );

  let currentLine = 0;
  for (const cell of prefixCells) {
    const text = cell.document.getText();
    const lineCount = text === "" ? 1 : text.split("\n").length;
    if (sourceLine >= currentLine && sourceLine < currentLine + lineCount) {
      return {
        uri: vscode.Uri.parse(cell.document.uri.toString()),
        position: new vscode.Position(sourceLine - currentLine, sourceColumn),
      };
    }
    currentLine += lineCount;
  }

  return {
    uri: vscode.Uri.parse(target.document.uri.toString()),
    position: new vscode.Position(Math.max(0, sourceLine - currentLine), sourceColumn),
  };
}

function mapSourceRangeToCell(
  cells: readonly AnalysisCell[],
  target: AnalysisCell,
  startLine: number,
  startCol: number,
  endLine: number,
  endCol: number,
): { uri: vscode.Uri; range: vscode.Range } {
  const start = mapSourcePositionToCell(cells, target, startLine, startCol);
  const end = mapSourcePositionToCell(cells, target, endLine, endCol);
  return { uri: start.uri, range: new vscode.Range(start.position, end.position) };
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

  const definition = vscode.languages.registerDefinitionProvider(FSHARP_CELL_SELECTOR, {
    async provideDefinition(document, position, token) {
      const resolved = resolveCell(document);
      if (!resolved || token.isCancellationRequested) {
        return undefined;
      }
      const { source, line, column } = buildAnalysisInput(resolved.cells, resolved.target, {
        line: position.line,
        character: position.character,
      });
      const version = versionFor(document.uri.toString(), source);
      const loc = await definitionAnalysis(await deps.getWorker(), {
        documentUri: analysisDocumentUri(document.uri.toString()),
        version,
        source,
        line,
        column,
      });
      if (!loc) {
        return undefined;
      }
      const docUriStr = analysisDocumentUri(document.uri.toString());
      if (loc.filePath === docUriStr || loc.filePath.includes("vscode-notebook-cell")) {
        const mapped = mapSourceRangeToCell(
          resolved.cells,
          resolved.target,
          loc.startLine,
          loc.startColumn,
          loc.endLine,
          loc.endColumn,
        );
        return new vscode.Location(mapped.uri, mapped.range);
      }
      const fileUri = vscode.Uri.file(loc.filePath);
      const range = new vscode.Range(loc.startLine, loc.startColumn, loc.endLine, loc.endColumn);
      return new vscode.Location(fileUri, range);
    },
  });

  const rename = vscode.languages.registerRenameProvider(FSHARP_CELL_SELECTOR, {
    async provideRenameEdits(document, position, newName, token) {
      const resolved = resolveCell(document);
      if (!resolved || token.isCancellationRequested) {
        return undefined;
      }
      const { source, line, column } = buildAnalysisInput(resolved.cells, resolved.target, {
        line: position.line,
        character: position.character,
      });
      const version = versionFor(document.uri.toString(), source);
      const edits = await renameAnalysis(await deps.getWorker(), {
        documentUri: analysisDocumentUri(document.uri.toString()),
        version,
        source,
        line,
        column,
      });
      if (!edits || edits.length === 0) {
        return undefined;
      }
      const workspaceEdit = new vscode.WorkspaceEdit();
      for (const edit of edits) {
        const mapped = mapSourceRangeToCell(
          resolved.cells,
          resolved.target,
          edit.startLine,
          edit.startColumn,
          edit.endLine,
          edit.endColumn,
        );
        workspaceEdit.replace(mapped.uri, mapped.range, newName);
      }
      return workspaceEdit;
    },
  });

  const connectionNameCompletion = registerConnectionNameCompletion({
    listConnectionNames: deps.listConnectionNames ?? (async () => []),
  });

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
    connectionNameCompletion,
    hover,
    signature,
    definition,
    rename,
    collection,
    { dispose: () => manager.dispose() },
    onChange,
    onOpen,
    onClose,
    onNotebookChange,
  );
}
