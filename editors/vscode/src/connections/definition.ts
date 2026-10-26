import { randomUUID } from "node:crypto";
import { makeUnionTag, type ConnectorKindName, type ConnectionDefinition } from "../protocol";

export interface ConnectionFormInput {
  kind: ConnectorKindName;
  name: string;
  filePath?: string;
  connectionString?: string;
  password?: string;
  url?: string;
}

export interface BuiltConnection {
  definition: ConnectionDefinition;
  /** Present only when a new password was entered — the caller stores it. */
  secret?: string;
  /** Present when the definition carries a SecretReference (new or kept). */
  secretReference?: string;
}

function configurationFor(input: ConnectionFormInput): unknown {
  switch (input.kind) {
    case "Sqlite":
    case "Csv":
    case "Excel":
      return { filePath: input.filePath ?? "" };
    case "SqlServer":
    case "PostgreSql":
      return { connectionString: input.connectionString ?? "" };
    case "RestJson":
      return { url: input.url ?? "", method: "GET", headers: [], queryParams: [], body: null };
  }
}

/** Pure. Assembles a `ConnectionDefinition` from form input, applying the
 *  Avalonia dialog's secret rules: a blank password on edit keeps the
 *  existing `SecretReference` (and the credential it points at) untouched;
 *  a typed password reuses an existing reference or mints `fqs-conn-<hex>`. */
export function buildConnectionDefinition(
  input: ConnectionFormInput,
  existing?: ConnectionDefinition,
): BuiltConnection {
  const hasNewSecret = !!input.password && input.password.trim().length > 0;

  const secretReference =
    existing?.secretReference ??
    (hasNewSecret ? `fqs-conn-${randomUUID().replace(/-/g, "")}` : null);

  const definition: ConnectionDefinition = {
    id: existing?.id ?? randomUUID(),
    name: input.name,
    kind: makeUnionTag(input.kind),
    configuration: configurationFor(input),
    secretReference,
    isTrusted: existing?.isTrusted ?? false,
  };

  return {
    definition,
    secret: hasNewSecret ? input.password : undefined,
    secretReference: secretReference ?? undefined,
  };
}
