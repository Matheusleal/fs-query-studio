import {
  newRequestId,
  readUnionTag,
  type ConnectionDefinition,
  type ConnectorKindName,
} from "../protocol";
import {
  buildConnectionDefinition,
  type ConnectionFormInput,
} from "./definition";
import type { WorkerLike } from "./tree";

export interface ConnectionUi {
  pickKind(): Promise<ConnectorKindName | undefined>;
  input(opts: {
    prompt: string;
    value?: string;
    password?: boolean;
    placeHolder?: string;
  }): Promise<string | undefined>;
  confirmRemove(name: string): Promise<boolean>;
}

export interface SecretWriter {
  store(key: string, value: string): Thenable<void>;
  delete(key: string): Thenable<void>;
}

export interface ConnectionFlowDeps {
  getWorker: () => Promise<WorkerLike>;
  ui: ConnectionUi;
  secrets: SecretWriter;
  refresh: () => void;
  log: (line: string) => void;
}

function stringProp(configuration: unknown, key: string): string {
  const value = (configuration as Record<string, unknown> | null | undefined)?.[key];
  return typeof value === "string" ? value : "";
}

async function collectFields(
  ui: ConnectionUi,
  kind: ConnectorKindName,
  existing?: ConnectionDefinition,
): Promise<ConnectionFormInput | undefined> {
  const name = await ui.input({
    prompt: "Connection name",
    value: existing?.name,
  });
  if (!name || name.trim().length === 0) {
    return undefined;
  }

  const input: ConnectionFormInput = { kind, name: name.trim() };

  if (kind === "Sqlite" || kind === "Csv" || kind === "Excel") {
    const rawFilePath = await ui.input({
      prompt: "Absolute file path",
      value: existing ? stringProp(existing.configuration, "filePath") : undefined,
      placeHolder: "/path/to/file",
    });
    if (rawFilePath === undefined) return undefined;
    input.filePath = rawFilePath.trim().replace(/^["']|["']$/g, "").trim();

    if (kind === "Csv") {
      const delim = await ui.input({
        prompt: "Delimiter (optional: comma, semicolon, tab. Leave blank for auto-detect)",
        value: existing ? stringProp(existing.configuration, "delimiter") : undefined,
        placeHolder: "Leave blank for auto-detect (, ; \\t |)",
      });
      if (delim !== undefined && delim.trim().length > 0) {
        input.delimiter = delim.trim();
      }
    }
  } else if (kind === "SqlServer" || kind === "PostgreSql") {
    const isPg = kind === "PostgreSql";
    const connectionString = await ui.input({
      prompt: isPg
        ? "PostgreSQL Connection String (e.g. Host=localhost;Database=mydb;Username=postgres)"
        : "SQL Server Connection String (e.g. Server=localhost;Database=master;User Id=sa)",
      value: existing ? stringProp(existing.configuration, "connectionString") : undefined,
      placeHolder: isPg
        ? "Host=localhost;Database=mydb;Username=postgres;Password={password}"
        : "Server=localhost;Database=master;User Id=sa;Password={password};TrustServerCertificate=True",
    });
    if (connectionString === undefined) return undefined;
    input.connectionString = connectionString;
    const password = await ui.input({
      prompt: existing?.secretReference
        ? "Password (leave blank to keep the current one)"
        : "Password (leave blank if already specified in connection string or not required)",
      password: true,
    });
    if (password === undefined) return undefined;
    input.password = password;
  } else {
    const url = await ui.input({
      prompt: "Base URL",
      value: existing ? stringProp(existing.configuration, "url") : undefined,
      placeHolder: "https://example.com/api",
    });
    if (url === undefined) return undefined;
    input.url = url;
  }

  return input;
}

async function register(
  deps: ConnectionFlowDeps,
  built: ReturnType<typeof buildConnectionDefinition>,
): Promise<boolean> {
  if (built.secret && built.secretReference) {
    await deps.secrets.store(built.secretReference, built.secret);
  }
  const worker = await deps.getWorker();
  const frame = await worker.send({
    id: newRequestId(),
    method: "connection/register",
    params: built.definition,
  });
  if (frame.error) {
    deps.log(`connection/register failed: ${frame.error.message}`);
    return false;
  }
  return true;
}

export async function runAddConnection(deps: ConnectionFlowDeps): Promise<void> {
  const kind = await deps.ui.pickKind();
  if (!kind) return;
  const input = await collectFields(deps.ui, kind);
  if (!input) return;
  if (await register(deps, buildConnectionDefinition(input))) {
    deps.refresh();
  }
}

export async function runEditConnection(
  deps: ConnectionFlowDeps,
  existing: ConnectionDefinition,
): Promise<void> {
  const kind = readUnionTag(existing.kind) as ConnectorKindName | undefined;
  if (!kind) {
    deps.log(`connection ${existing.id} has an unrecognised kind; cannot edit`);
    return;
  }
  const input = await collectFields(deps.ui, kind, existing);
  if (!input) return;
  if (await register(deps, buildConnectionDefinition(input, existing))) {
    deps.refresh();
  }
}

export async function runRemoveConnection(
  deps: ConnectionFlowDeps,
  existing: ConnectionDefinition,
): Promise<void> {
  if (!(await deps.ui.confirmRemove(existing.name))) return;
  const worker = await deps.getWorker();
  const frame = await worker.send({
    id: newRequestId(),
    method: "connection/remove",
    params: { id: existing.id },
  });
  if (frame.error) {
    deps.log(`connection/remove failed: ${frame.error.message}`);
    return;
  }
  if (existing.secretReference) {
    await deps.secrets.delete(existing.secretReference);
  }
  deps.refresh();
}
