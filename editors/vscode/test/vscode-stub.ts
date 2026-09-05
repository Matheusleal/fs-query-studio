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

export default { window, commands, env, Uri };
