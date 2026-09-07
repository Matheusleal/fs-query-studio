// Minimal stand-in for the `vscode` module in unit tests. Only the surface
// the extension actually touches is implemented; extend as later tasks need.
type Disposable = { dispose: () => void };

export const window = {
  createOutputChannel(_name: string) {
    const lines: string[] = [];
    return {
      lines,
      appendLine: (line: string) => lines.push(line),
      show: () => {},
      dispose: () => {},
    };
  },
  showErrorMessage: async (_message: string, ..._items: string[]) => undefined as string | undefined,
  showInformationMessage: async (_message: string, ..._items: string[]) =>
    undefined as string | undefined,
  activeNotebookEditor: undefined as unknown,
  showSaveDialog: async (_opts?: unknown) => undefined as { fsPath: string } | undefined,
  showQuickPick: async (_items: unknown, _opts?: unknown) => undefined as unknown,
  showInputBox: async (_opts?: unknown) => undefined as string | undefined,
  showWarningMessage: async (_message: string, _opts?: unknown, ..._items: string[]) =>
    undefined as string | undefined,
  registerTreeDataProvider(_id: string, _provider: unknown): Disposable {
    return { dispose: () => {} };
  },
};

export const workspace = {
  registerNotebookSerializer(_type: string, _serializer: unknown): Disposable {
    return { dispose: () => {} };
  },
  notebookDocuments: [] as Array<{
    notebookType: string;
    getCells: () => unknown[];
  }>,
  onDidChangeTextDocument(_fn: (e: unknown) => unknown): Disposable {
    return { dispose: () => {} };
  },
  onDidOpenTextDocument(_fn: (e: unknown) => unknown): Disposable {
    return { dispose: () => {} };
  },
  onDidCloseTextDocument(_fn: (e: unknown) => unknown): Disposable {
    return { dispose: () => {} };
  },
  onDidChangeNotebookDocument(_fn: (e: unknown) => unknown): Disposable {
    return { dispose: () => {} };
  },
};

export const commands = {
  _handlers: new Map<string, (...args: unknown[]) => unknown>(),
  registerCommand(id: string, handler: (...args: unknown[]) => unknown): Disposable {
    this._handlers.set(id, handler);
    return { dispose: () => this._handlers.delete(id) };
  },
  async executeCommand(id: string, ...args: unknown[]) {
    return this._handlers.get(id)?.(...args);
  },
};

export const env = { openExternal: async (_uri: unknown) => true };
export const Uri = {
  parse: (s: string) => ({ toString: () => s, fsPath: s }),
  file: (p: string) => ({ toString: () => `file://${p}`, fsPath: p }),
  joinPath: (base: { toString(): string }, ...segments: string[]) => {
    const joined = [base.toString().replace(/\/+$/, ""), ...segments].join("/");
    return { toString: () => joined, fsPath: joined };
  },
};

// --- notebook document model (F2) ---
export enum NotebookCellKind {
  Markup = 1,
  Code = 2,
}

export class NotebookCellData {
  metadata?: Record<string, unknown>;
  constructor(
    public kind: NotebookCellKind,
    public value: string,
    public languageId: string,
  ) {}
}

export class NotebookData {
  metadata?: Record<string, unknown>;
  constructor(public cells: NotebookCellData[]) {}
}

// --- notebook execution model (F2) ---
export class NotebookCellOutputItem {
  constructor(
    public data: unknown,
    public mime: string,
  ) {}
  static stdout(value: string) {
    return new NotebookCellOutputItem(value, "application/vnd.code.notebook.stdout");
  }
  static stderr(value: string) {
    return new NotebookCellOutputItem(value, "application/vnd.code.notebook.stderr");
  }
  static text(value: string, mime = "text/plain") {
    return new NotebookCellOutputItem(value, mime);
  }
  static json(value: unknown, mime = "application/json") {
    return new NotebookCellOutputItem(new TextEncoder().encode(JSON.stringify(value)), mime);
  }
  static error(err: { name: string; message: string }) {
    return new NotebookCellOutputItem(err, "application/vnd.code.notebook.error");
  }
}

export class NotebookCellOutput {
  constructor(public items: NotebookCellOutputItem[]) {}
}

export const notebooks = {
  _controllers: [] as unknown[],
  createNotebookController(id: string, notebookType: string, label: string) {
    const executions: Array<Record<string, unknown>> = [];
    const controller: Record<string, unknown> = {
      id,
      notebookType,
      label,
      supportedLanguages: [] as string[],
      supportsExecutionOrder: false,
      description: "",
      executeHandler: undefined,
      interruptHandler: undefined,
      _executions: executions,
      createNotebookCellExecution(cell: unknown) {
        const exec: Record<string, unknown> = {
          cell,
          executionOrder: 0,
          outputs: [] as unknown[],
          started: false,
          ended: undefined as boolean | undefined,
          start() {
            (this as { started: boolean }).started = true;
          },
          end(ok?: boolean) {
            (this as { ended: boolean | undefined }).ended = ok;
          },
          replaceOutput(out: unknown) {
            (this as { outputs: unknown[] }).outputs = Array.isArray(out) ? out : [out];
          },
          clearOutput() {
            (this as { outputs: unknown[] }).outputs = [];
          },
          token: { isCancellationRequested: false },
        };
        executions.push(exec);
        return exec;
      },
      dispose() {},
    };
    this._controllers.push(controller);
    return controller;
  },
  _rendererMessagingIds: [] as string[],
  createRendererMessaging(id: string) {
    this._rendererMessagingIds.push(id);
    const posted: Array<{ message: unknown; editor: unknown }> = [];
    let listener: ((e: { editor: unknown; message: unknown }) => unknown) | undefined;
    return {
      _id: id,
      _posted: posted,
      onDidReceiveMessage(fn: (e: { editor: unknown; message: unknown }) => unknown) {
        listener = fn;
        return { dispose: () => (listener = undefined) };
      },
      postMessage(message: unknown, editor?: unknown) {
        posted.push({ message, editor });
        return Promise.resolve(true);
      },
      _emit(e: { editor: unknown; message: unknown }) {
        return listener?.(e);
      },
    };
  },
};

// --- language features (F4) ---
export class Position {
  constructor(
    public line: number,
    public character: number,
  ) {}
}

export class Range {
  constructor(
    public startLine: number,
    public startCharacter: number,
    public endLine: number,
    public endCharacter: number,
  ) {}
}

export enum DiagnosticSeverity {
  Error = 0,
  Warning = 1,
  Information = 2,
  Hint = 3,
}

export class Diagnostic {
  code: string | number | undefined;
  source: string | undefined;
  constructor(
    public range: Range,
    public message: string,
    public severity: DiagnosticSeverity = DiagnosticSeverity.Error,
  ) {}
}

export class MarkdownString {
  value = "";
  constructor(value?: string) {
    if (value) this.value = value;
  }
  appendText(s: string) {
    this.value += s;
    return this;
  }
  appendMarkdown(s: string) {
    this.value += s;
    return this;
  }
  appendCodeblock(code: string, language = "") {
    this.value += `\n\`\`\`${language}\n${code}\n\`\`\`\n`;
    return this;
  }
}

export class Hover {
  constructor(
    public contents: unknown,
    public range?: Range,
  ) {}
}

export enum CompletionItemKind {
  Text = 0,
  Method = 1,
  Function = 2,
  Field = 4,
  Variable = 5,
  Value = 11,
}

export class CompletionItem {
  constructor(
    public label: string,
    public kind?: CompletionItemKind,
  ) {}
}

export class ParameterInformation {
  constructor(
    public label: string,
    public documentation?: string | MarkdownString,
  ) {}
}

export class SignatureInformation {
  parameters: ParameterInformation[] = [];
  constructor(
    public label: string,
    public documentation?: string | MarkdownString,
  ) {}
}

export class SignatureHelp {
  signatures: SignatureInformation[] = [];
  activeSignature = 0;
  activeParameter = 0;
}

export const Disposable = {
  from: (...items: Array<{ dispose: () => void }>) => ({
    dispose: () => items.forEach((i) => i.dispose?.()),
  }),
};

export const languages = {
  _completion: [] as Array<{ selector: unknown; provider: unknown; triggers: string[] }>,
  _hover: [] as Array<{ selector: unknown; provider: unknown }>,
  _signature: [] as Array<{ selector: unknown; provider: unknown; triggers: string[] }>,
  _collections: [] as Array<{ name: string; set: unknown }>,
  registerCompletionItemProvider(
    selector: unknown,
    provider: unknown,
    ...triggers: string[]
  ): Disposable {
    this._completion.push({ selector, provider, triggers });
    return { dispose: () => {} };
  },
  registerHoverProvider(selector: unknown, provider: unknown): Disposable {
    this._hover.push({ selector, provider });
    return { dispose: () => {} };
  },
  registerSignatureHelpProvider(
    selector: unknown,
    provider: unknown,
    ...triggers: string[]
  ): Disposable {
    this._signature.push({ selector, provider, triggers });
    return { dispose: () => {} };
  },
  createDiagnosticCollection(name: string) {
    const map = new Map<string, unknown>();
    const collection = {
      name,
      _map: map,
      set: (uri: { toString(): string }, diags: unknown) => map.set(uri.toString(), diags),
      delete: (uri: { toString(): string }) => map.delete(uri.toString()),
      get: (uri: { toString(): string }) => map.get(uri.toString()),
      clear: () => map.clear(),
      dispose: () => map.clear(),
    };
    this._collections.push({ name, set: collection.set });
    return collection;
  },
};

// --- tree view (F5) ---
export enum TreeItemCollapsibleState {
  None = 0,
  Collapsed = 1,
  Expanded = 2,
}

export class TreeItem {
  description?: string;
  contextValue?: string;
  iconPath?: unknown;
  constructor(
    public label: string,
    public collapsibleState: TreeItemCollapsibleState = TreeItemCollapsibleState.None,
  ) {}
}

export class ThemeIcon {
  constructor(public id: string) {}
}

export class EventEmitter<T> {
  private listeners: Array<(e: T) => unknown> = [];
  event = (listener: (e: T) => unknown) => {
    this.listeners.push(listener);
    return { dispose: () => (this.listeners = this.listeners.filter((l) => l !== listener)) };
  };
  fire(data: T) {
    for (const l of this.listeners) l(data);
  }
  dispose() {
    this.listeners = [];
  }
}

export default {
  window,
  workspace,
  commands,
  env,
  Uri,
  NotebookCellKind,
  NotebookCellData,
  NotebookData,
  NotebookCellOutputItem,
  NotebookCellOutput,
  notebooks,
  languages,
  Position,
  Range,
  DiagnosticSeverity,
  Diagnostic,
  MarkdownString,
  Hover,
  CompletionItem,
  CompletionItemKind,
  SignatureHelp,
  SignatureInformation,
  ParameterInformation,
  Disposable,
  TreeItem,
  TreeItemCollapsibleState,
  ThemeIcon,
  EventEmitter,
};
