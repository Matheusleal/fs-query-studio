import { randomUUID } from "node:crypto";
import { makeUnionTag, type ConnectorKindName, type ConnectionDefinition } from "../protocol";
import {
  ConnectorFrontRegistry,
  type ConnectionFormInput,
} from "./connector-registry";

export type { ConnectionFormInput };

export interface BuiltConnection {
  definition: ConnectionDefinition;
  /** Present only when a new password was entered — the caller stores it. */
  secret?: string;
  /** Present when the definition carries a SecretReference (new or kept). */
  secretReference?: string;
}

export function sanitizeConnectionStringAndPassword(
  kind: ConnectorKindName,
  rawConnStr: string,
  rawPassword?: string,
): { connectionString: string; password?: string } {
  return ConnectorFrontRegistry.sanitizeConnectionStringAndPassword(kind, rawConnStr, rawPassword);
}

/** Pure. Assembles a `ConnectionDefinition` from form input, applying the
 *  secret rules: a blank password on edit keeps the existing `SecretReference`
 *  (and the credential it points at) untouched; a typed or extracted password
 *  reuses an existing reference or mints `fqs-conn-<hex>`. Plaintext passwords
 *  in connection strings are automatically extracted and replaced with `{password}`. */
export function buildConnectionDefinition(
  input: ConnectionFormInput,
  existing?: ConnectionDefinition,
): BuiltConnection {
  let formInput = { ...input };

  const connStr =
    formInput.connectionString ??
    (formInput.configValues?.connectionString as string | undefined);

  if (connStr !== undefined) {
    const sanitized = ConnectorFrontRegistry.sanitizeConnectionStringAndPassword(
      formInput.kind,
      connStr,
      formInput.password,
    );
    formInput.connectionString = sanitized.connectionString;
    formInput.password = sanitized.password;
    if (formInput.configValues) {
      formInput.configValues = {
        ...formInput.configValues,
        connectionString: sanitized.connectionString,
      };
    }
  }

  const hasNewSecret = !!formInput.password && formInput.password.trim().length > 0;

  const effectiveConnStr =
    formInput.connectionString ??
    (formInput.configValues?.connectionString as string | undefined) ??
    "";

  const isWindowsAuth = ConnectorFrontRegistry.isWindowsAuthentication(
    formInput.kind,
    effectiveConnStr,
    hasNewSecret,
  );

  const secretReference = isWindowsAuth
    ? null
    : (existing?.secretReference ??
      (hasNewSecret ? `fqs-conn-${randomUUID().replace(/-/g, "")}` : null));

  const definition: ConnectionDefinition = {
    id: existing?.id ?? randomUUID(),
    name: formInput.name,
    kind: makeUnionTag(formInput.kind),
    configuration: ConnectorFrontRegistry.configurationFor(formInput),
    secretReference,
    isTrusted: existing?.isTrusted ?? false,
  };

  return {
    definition,
    secret: hasNewSecret ? formInput.password : undefined,
    secretReference: secretReference ?? undefined,
  };
}
