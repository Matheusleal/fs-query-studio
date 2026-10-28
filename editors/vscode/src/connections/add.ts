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

export type ConnectionScope = "local" | "global";
export type SqlServerAuthType = "windows" | "sql";

export interface WorkspaceFolderInfo {
  name: string;
  path: string;
}

export interface ConnectionScopePick {
  scope: ConnectionScope;
  targetDir?: string;
}

export interface ConnectionUi {
  pickKind(): Promise<ConnectorKindName | undefined>;
  pickScope?(folders: WorkspaceFolderInfo[]): Promise<ConnectionScopePick | undefined>;
  pickSqlServerAuth?(): Promise<SqlServerAuthType | undefined>;
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
  getWorkspaceFolders?: () => WorkspaceFolderInfo[];
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
  } else if (kind === "SqlServer") {
    let authType: SqlServerAuthType = "sql";
    if (ui.pickSqlServerAuth) {
      const existingConnStr = existing ? stringProp(existing.configuration, "connectionString") : "";
      const isExistingWindows = /(integrated\s+security\s*=\s*(true|sspi)|trusted_connection\s*=\s*(true|yes))/i.test(existingConnStr);
      const pickedAuth = await ui.pickSqlServerAuth();
      if (!pickedAuth) return undefined;
      authType = pickedAuth;
    }

    if (authType === "windows") {
      const defaultConnStr = existing
        ? stringProp(existing.configuration, "connectionString")
        : "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;";
      const connectionString = await ui.input({
        prompt: "SQL Server Connection String (Windows Authentication / Integrated Security)",
        value: defaultConnStr,
        placeHolder: "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;",
      });
      if (connectionString === undefined) return undefined;

      let normalized = connectionString.trim();
      if (!/integrated\s+security\s*=/i.test(normalized) && !/trusted_connection\s*=/i.test(normalized)) {
        normalized = normalized.replace(/;?$/, ";Integrated Security=True;");
      }
      if (!/trustservercertificate\s*=/i.test(normalized)) {
        normalized = normalized.replace(/;?$/, ";TrustServerCertificate=True;");
      }
      input.connectionString = normalized;
      // Windows Authentication uses current Windows identity — no password needed!
    } else {
      const connectionString = await ui.input({
        prompt: "SQL Server Connection String (e.g. Server=localhost;Database=master;User Id=sa)",
        value: existing ? stringProp(existing.configuration, "connectionString") : undefined,
        placeHolder: "Server=localhost;Database=master;User Id=sa;Password={password};TrustServerCertificate=True",
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
    }
  } else if (kind === "PostgreSql") {
    const connectionString = await ui.input({
      prompt: "PostgreSQL Connection String (e.g. Host=localhost;Database=mydb;Username=postgres)",
      value: existing ? stringProp(existing.configuration, "connectionString") : undefined,
      placeHolder: "Host=localhost;Database=mydb;Username=postgres;Password={password}",
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
  workspaceDir?: string,
): Promise<boolean> {
  if (built.secret && built.secretReference) {
    await deps.secrets.store(built.secretReference, built.secret);
  }
  const worker = await deps.getWorker();
  const scopeDesc = workspaceDir ? ` in local workspace: ${workspaceDir}` : " (global)";
  deps.log(
    `Registering connection '${built.definition.name}' (${readUnionTag(built.definition.kind)})${scopeDesc}...`,
  );
  const params: Record<string, unknown> = { ...built.definition };
  if (workspaceDir) {
    params.workspaceDir = workspaceDir;
  }
  const frame = await worker.send({
    id: newRequestId(),
    method: "connection/register",
    params,
  });
  if (frame.error) {
    deps.log(
      `connection/register for '${built.definition.name}' failed: [${frame.error.code}] ${frame.error.message}`,
    );
    return false;
  }
  deps.log(`connection/register for '${built.definition.name}' succeeded.`);
  return true;
}

export async function runAddConnection(deps: ConnectionFlowDeps): Promise<void> {
  let targetDir: string | undefined;
  const folders = deps.getWorkspaceFolders ? deps.getWorkspaceFolders() : [];

  if (folders.length > 0 && deps.ui.pickScope) {
    const scopeChoice = await deps.ui.pickScope(folders);
    if (!scopeChoice) return;
    if (scopeChoice.scope === "local") {
      targetDir = scopeChoice.targetDir;
    }
  }

  const kind = await deps.ui.pickKind();
  if (!kind) return;
  const input = await collectFields(deps.ui, kind);
  if (!input) return;
  if (await register(deps, buildConnectionDefinition(input), targetDir)) {
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
  const folders = deps.getWorkspaceFolders ? deps.getWorkspaceFolders() : [];
  const targetDir = folders.length > 0 ? folders[0].path : undefined;
  if (await register(deps, buildConnectionDefinition(input, existing), targetDir)) {
    deps.refresh();
  }
}

export async function runRemoveConnection(
  deps: ConnectionFlowDeps,
  existing: ConnectionDefinition,
): Promise<void> {
  if (!(await deps.ui.confirmRemove(existing.name))) return;
  const worker = await deps.getWorker();
  const folders = deps.getWorkspaceFolders ? deps.getWorkspaceFolders() : [];
  const params: Record<string, unknown> = { id: existing.id };
  if (folders.length > 0) {
    params.workspaceDir = folders[0].path;
  }
  const frame = await worker.send({
    id: newRequestId(),
    method: "connection/remove",
    params,
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
