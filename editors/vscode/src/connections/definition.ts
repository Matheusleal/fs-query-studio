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

export function sanitizeConnectionStringAndPassword(
  kind: ConnectorKindName,
  rawConnStr: string,
  rawPassword?: string,
): { connectionString: string; password?: string } {
  let connStr = rawConnStr.trim();
  let pwd = rawPassword?.trim() ? rawPassword.trim() : undefined;

  // 1. Check for plaintext password in standard key=value connection string:
  // e.g. "Password=secret", "Pwd=secret", "User Id=sa;Password=secret;"
  const kvPwdRegex = /(^|;)\s*(password|pwd)\s*=\s*([^;]+)/i;
  const kvMatch = connStr.match(kvPwdRegex);
  if (kvMatch) {
    const extractedVal = kvMatch[3].trim();
    if (!extractedVal.toLowerCase().includes("{password}")) {
      if (!pwd) {
        pwd = extractedVal;
      }
      connStr = connStr.replace(kvPwdRegex, "$1$2={password}");
    }
  }

  // 2. Check for URI format: e.g. postgres://user:secret@host:port/db
  const uriPwdRegex = /^(postgres(?:ql)?:\/\/[^:]+):([^@]+)(@.+)$/i;
  const uriMatch = connStr.match(uriPwdRegex);
  if (uriMatch) {
    const extractedUriPwd = decodeURIComponent(uriMatch[2]);
    if (!extractedUriPwd.toLowerCase().includes("{password}")) {
      if (!pwd) {
        pwd = extractedUriPwd;
      }
      connStr = connStr.replace(uriPwdRegex, "$1:{password}$3");
    }
  }

  // 3. If password was provided (either extracted or typed), but connection string does NOT have {password} placeholder:
  if (pwd && !connStr.toLowerCase().includes("{password}")) {
    if (
      connStr.toLowerCase().startsWith("postgres://") ||
      connStr.toLowerCase().startsWith("postgresql://")
    ) {
      const uriUserRegex = /^(postgres(?:ql)?:\/\/[^@]+)(@.+)$/i;
      if (uriUserRegex.test(connStr)) {
        connStr = connStr.replace(uriUserRegex, "$1:{password}$2");
      }
    } else {
      const trimmed = connStr.endsWith(";") ? connStr.slice(0, -1) : connStr;
      connStr = trimmed ? `${trimmed};Password={password};` : "Password={password};";
    }
  }

  return { connectionString: connStr, password: pwd };
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
 *  secret rules: a blank password on edit keeps the existing `SecretReference`
 *  (and the credential it points at) untouched; a typed or extracted password
 *  reuses an existing reference or mints `fqs-conn-<hex>`. Plaintext passwords
 *  in connection strings are automatically extracted and replaced with `{password}`. */
export function buildConnectionDefinition(
  input: ConnectionFormInput,
  existing?: ConnectionDefinition,
): BuiltConnection {
  let formInput = { ...input };

  if (formInput.kind === "SqlServer" || formInput.kind === "PostgreSql") {
    const sanitized = sanitizeConnectionStringAndPassword(
      formInput.kind,
      formInput.connectionString ?? "",
      formInput.password,
    );
    formInput.connectionString = sanitized.connectionString;
    formInput.password = sanitized.password;
  }

  const hasNewSecret = !!formInput.password && formInput.password.trim().length > 0;

  const secretReference =
    existing?.secretReference ??
    (hasNewSecret ? `fqs-conn-${randomUUID().replace(/-/g, "")}` : null);

  const definition: ConnectionDefinition = {
    id: existing?.id ?? randomUUID(),
    name: formInput.name,
    kind: makeUnionTag(formInput.kind),
    configuration: configurationFor(formInput),
    secretReference,
    isTrusted: existing?.isTrusted ?? false,
  };

  return {
    definition,
    secret: hasNewSecret ? formInput.password : undefined,
    secretReference: secretReference ?? undefined,
  };
}
