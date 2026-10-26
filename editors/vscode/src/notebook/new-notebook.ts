import * as vscode from "vscode";
import { FSQNB_NOTEBOOK_TYPE } from "./serializer";
import { serializeFsqnb, type FsqnbDocument } from "./fsqnb";

const FSQNB_EXT = ".fsqnb";

/** A new notebook opens with one empty F# code cell — ready to type. */
export function emptyNotebookData(): vscode.NotebookData {
  const cell = new vscode.NotebookCellData(vscode.NotebookCellKind.Code, "", "fsharp");
  const data = new vscode.NotebookData([cell]);
  data.metadata = {};
  return data;
}

function emptyFsqnbDocument(): FsqnbDocument {
  return {
    version: 1,
    cells: [{ kind: "code", language: "fsharp", value: "" }],
    metadata: {},
  };
}

/** Bytes for a fresh on-disk `.fsqnb` — a valid v1 document, one empty cell. */
export function emptyFsqnbBytes(): Uint8Array {
  return new TextEncoder().encode(serializeFsqnb(emptyFsqnbDocument()));
}

export interface OpenUntitledNotebookDeps {
  openNotebookDocument: (type: string, data: vscode.NotebookData) => Promise<unknown>;
  showNotebookDocument: (doc: unknown) => Promise<unknown>;
}

/**
 * Open a new **untitled** F# Query Notebook. Backs the Command Palette and
 * the File > New File… entry; the user chooses a path on first save.
 */
export async function openNewUntitledNotebook(deps: OpenUntitledNotebookDeps): Promise<void> {
  const doc = await deps.openNotebookDocument(FSQNB_NOTEBOOK_TYPE, emptyNotebookData());
  await deps.showNotebookDocument(doc);
}

export class NotebookFileExistsError extends Error {
  readonly userMessage: string;
  constructor(readonly path: string) {
    super(`file already exists: ${path}`);
    this.userMessage = `A file named "${path}" already exists.`;
  }
}

export interface CreateNotebookFileDeps {
  showInputBox: (opts: {
    prompt: string;
    value: string;
    validateInput: (value: string) => string | undefined;
  }) => Promise<string | undefined>;
  fileExists: (fsPath: string) => Promise<boolean>;
  writeFile: (fsPath: string, bytes: Uint8Array) => Promise<void>;
  openNotebook: (fsPath: string) => Promise<void>;
  joinPath: (dirFsPath: string, name: string) => string;
}

/** Reject empty / whitespace-only names and anything with a path separator. */
export function validateNotebookName(raw: string): string | undefined {
  const name = raw.trim();
  if (name.length === 0) return "Enter a file name.";
  if (/[\\/]/.test(name)) return "The name cannot contain a path separator.";
  return undefined;
}

function ensureExtension(name: string): string {
  return name.endsWith(FSQNB_EXT) ? name : name + FSQNB_EXT;
}

/**
 * Prompt for a name, write an empty `.fsqnb` into `folderFsPath`, and open
 * it. Backs the Explorer folder context-menu entry. Returns the created
 * path, or `undefined` if the prompt was cancelled. Throws
 * {@link NotebookFileExistsError} if the target already exists.
 */
export async function createNotebookFileInFolder(
  folderFsPath: string,
  deps: CreateNotebookFileDeps,
): Promise<string | undefined> {
  const typed = await deps.showInputBox({
    prompt: "Name for the new F# Query Notebook",
    value: `query${FSQNB_EXT}`,
    validateInput: validateNotebookName,
  });
  if (typed === undefined) return undefined;

  const target = deps.joinPath(folderFsPath, ensureExtension(typed.trim()));
  if (await deps.fileExists(target)) {
    throw new NotebookFileExistsError(target);
  }

  await deps.writeFile(target, emptyFsqnbBytes());
  await deps.openNotebook(target);
  return target;
}
