/**
 * In-memory shape of a `.fsqnb` file, independent of any VS Code type.
 * The format is a purpose-built JSON schema under this project's control
 * (see the migration design doc, "`.fsqnb` format"). Outputs are never
 * part of the file.
 */
export type FsqnbCellKind = "code" | "markdown";

export interface FsqnbCell {
  kind: FsqnbCellKind;
  /** `"markdown"` for markdown cells; the code cell's language otherwise. */
  language: string;
  value: string;
}

export interface FsqnbDocument {
  version: 1;
  cells: FsqnbCell[];
  metadata: Record<string, unknown>;
}

export const FSQNB_VERSION = 1 as const;

export class FsqnbError extends Error {
  readonly userMessage: string;
  constructor(message: string, userMessage?: string) {
    super(message);
    this.userMessage = userMessage ?? message;
  }
}

export class FsqnbVersionError extends FsqnbError {
  constructor(readonly found: unknown) {
    super(
      `unsupported .fsqnb version: ${JSON.stringify(found)}`,
      `This .fsqnb file declares version ${JSON.stringify(found)}, but this ` +
        `build of F# Query Studio only understands version ${FSQNB_VERSION}.`,
    );
  }
}

function emptyDocument(): FsqnbDocument {
  return { version: FSQNB_VERSION, cells: [], metadata: {} };
}

export function deserializeFsqnb(text: string): FsqnbDocument {
  const trimmed = text.trim();
  if (trimmed.length === 0) {
    return emptyDocument();
  }

  let raw: unknown;
  try {
    raw = JSON.parse(trimmed);
  } catch (cause) {
    throw new FsqnbError(`.fsqnb is not valid JSON: ${(cause as Error).message}`);
  }
  if (typeof raw !== "object" || raw === null || Array.isArray(raw)) {
    throw new FsqnbError(".fsqnb root must be a JSON object");
  }

  const obj = raw as Record<string, unknown>;
  if (obj.version !== FSQNB_VERSION) {
    throw new FsqnbVersionError(obj.version);
  }

  const cellsIn = Array.isArray(obj.cells) ? obj.cells : [];
  const cells: FsqnbCell[] = cellsIn.map((entry) => {
    const cell = (entry ?? {}) as Record<string, unknown>;
    const kind: FsqnbCellKind = cell.kind === "markdown" ? "markdown" : "code";
    const value = typeof cell.value === "string" ? cell.value : "";
    const language =
      kind === "markdown"
        ? "markdown"
        : typeof cell.language === "string" && cell.language.length > 0
          ? cell.language
          : "fsharp";
    return { kind, language, value };
  });

  const metadata =
    typeof obj.metadata === "object" && obj.metadata !== null && !Array.isArray(obj.metadata)
      ? (obj.metadata as Record<string, unknown>)
      : {};

  return { version: FSQNB_VERSION, cells, metadata };
}

export function serializeFsqnb(doc: FsqnbDocument): string {
  const out = {
    version: FSQNB_VERSION,
    cells: doc.cells.map((c) =>
      c.kind === "markdown"
        ? { kind: "markdown", value: c.value }
        : { kind: "code", language: c.language || "fsharp", value: c.value },
    ),
    metadata: doc.metadata ?? {},
  };
  return JSON.stringify(out, null, 2) + "\n";
}
