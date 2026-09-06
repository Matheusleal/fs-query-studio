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
};

export const workspace = {
  registerNotebookSerializer(_type: string, _serializer: unknown): Disposable {
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
export const Uri = { parse: (s: string) => ({ toString: () => s }) };

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
};

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
};
