import {
  newRequestId,
  readUnionTag,
  type ConnectionDefinition,
  type ConnectorKindName,
  type ConnectorDescriptorDto,
  type ConnectorFormFieldDto,
} from "../protocol";
import {
  buildConnectionDefinition,
  type ConnectionFormInput,
} from "./definition";
import { ConnectorFrontRegistry } from "./connector-registry";
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
  pickKind(descriptors?: ConnectorDescriptorDto[]): Promise<ConnectorKindName | undefined>;
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
  getDescriptors?: () => Promise<ConnectorDescriptorDto[]>;
}

export const DEFAULT_CONNECTOR_DESCRIPTORS: ConnectorDescriptorDto[] =
  ConnectorFrontRegistry.defaultDescriptors;

function stringProp(configuration: unknown, key: string): string {
  const value = (configuration as Record<string, unknown> | null | undefined)?.[key];
  return typeof value === "string" ? value : "";
}

export async function collectFields(
  ui: ConnectionUi,
  kind: ConnectorKindName,
  existing?: ConnectionDefinition,
  descriptor?: ConnectorDescriptorDto,
): Promise<ConnectionFormInput | undefined> {
  const name = await ui.input({
    prompt: "Connection name",
    value: existing?.name,
  });
  if (!name || name.trim().length === 0) {
    return undefined;
  }

  const input: ConnectionFormInput = { kind, name: name.trim() };
  const configValues: Record<string, unknown> = {};
  let isWindowsAuth = false;

  const effectiveDescriptor =
    descriptor ??
    DEFAULT_CONNECTOR_DESCRIPTORS.find((d) => d.kindId.toLowerCase() === kind.toLowerCase());

  if (!effectiveDescriptor) {
    return input;
  }

  for (const field of effectiveDescriptor.formFields) {
    const existingVal = existing ? stringProp(existing.configuration, field.name) : undefined;
    const defaultVal = existingVal || field.defaultValue || undefined;

    if (field.name === "password" || field.type === "password") {
      if (isWindowsAuth) {
        continue;
      }
      const password = await ui.input({
        prompt: existing?.secretReference
          ? `${field.label} (leave blank to keep the current one)`
          : `${field.label} (leave blank if already specified in connection string or not required)`,
        password: true,
      });
      if (password === undefined) return undefined;
      input.password = password;
      continue;
    }

    if (ConnectorFrontRegistry.supportsWindowsAuthPrompt(kind, field.name) && ui.pickSqlServerAuth) {
      const pickedAuth = await ui.pickSqlServerAuth();
      if (!pickedAuth) return undefined;

      if (pickedAuth === "windows") {
        isWindowsAuth = true;
        const defaultConnStr = existing
          ? stringProp(existing.configuration, field.name)
          : ConnectorFrontRegistry.defaultWindowsAuthConnectionString;
        const connectionString = await ui.input({
          prompt: `${field.label} (Windows Authentication / Integrated Security)`,
          value: defaultConnStr,
          placeHolder: ConnectorFrontRegistry.defaultWindowsAuthConnectionString,
        });
        if (connectionString === undefined) return undefined;

        const normalized = ConnectorFrontRegistry.normalizeWindowsAuthConnectionString(connectionString);
        input.connectionString = normalized;
        configValues[field.name] = normalized;
        // Windows Authentication uses current Windows identity — skip password
        continue;
      }
    }


    const entered = await ui.input({
      prompt: field.label,
      value: defaultVal,
      placeHolder: field.placeholder ?? undefined,
    });
    if (entered === undefined) return undefined;

    const trimmed = entered.trim();
    if (field.type === "file") {
      const cleanPath = trimmed.replace(/^["']|["']$/g, "").trim();
      configValues[field.name] = cleanPath;
      input.filePath = cleanPath;
    } else {
      configValues[field.name] = trimmed;
      if (field.name === "connectionString") {
        input.connectionString = trimmed;
      } else if (field.name === "delimiter") {
        input.delimiter = trimmed;
      } else if (field.name === "url") {
        input.url = trimmed;
      }
    }
  }

  input.configValues = configValues;
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

  const descriptors = deps.getDescriptors
    ? await deps.getDescriptors()
    : DEFAULT_CONNECTOR_DESCRIPTORS;

  const kind = await deps.ui.pickKind(descriptors);
  if (!kind) return;

  const desc = descriptors.find((d) => d.kindId.toLowerCase() === kind.toLowerCase());
  const input = await collectFields(deps.ui, kind, undefined, desc);
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
  const descriptors = deps.getDescriptors
    ? await deps.getDescriptors()
    : DEFAULT_CONNECTOR_DESCRIPTORS;
  const desc = descriptors.find((d) => d.kindId.toLowerCase() === kind.toLowerCase());
  const input = await collectFields(deps.ui, kind, existing, desc);
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
