import type { ConnectorDescriptorDto, ConnectorKindName } from "../protocol";

export type TargetConnectorCategory = "Database" | "Csv" | "Excel" | "Json" | "Xml" | "RestJson" | (string & {});

export interface ConnectionFormInput {
  kind: ConnectorKindName;
  name: string;
  filePath?: string;
  delimiter?: string;
  sheetName?: string;
  jsonPath?: string;
  rowElement?: string;
  columnTypes?: string;
  columnTransforms?: string;
  connectionString?: string;
  password?: string;
  url?: string;
  configValues?: Record<string, unknown>;
  [key: string]: unknown;
}

/**
 * Contract implemented by each self-contained frontend connector module.
 * Each connector encapsulates its own descriptor, validation, credential sanitization,
 * authentication rules, and configuration builder.
 */
export interface FrontendConnectorModule {
  readonly descriptor: ConnectorDescriptorDto;
  readonly autocompleteCategory: TargetConnectorCategory;
  /**
   * Prefix function/member names under `Connections.<member>` that target this connector's category.
   */
  readonly completionTriggerMembers: readonly string[];
  buildConfiguration(input: ConnectionFormInput): unknown;
  validateConfiguration(config: Record<string, unknown>): string | undefined;
  sanitizeCredentials?(
    rawConnStr: string,
    rawPassword?: string,
  ): { connectionString: string; password?: string };
  isWindowsAuthentication?(connectionString: string, hasPassword?: boolean): boolean;
  supportsWindowsAuthPrompt?(fieldName: string): boolean;
  readonly defaultWindowsAuthConnectionString?: string;
  normalizeWindowsAuthConnectionString?(rawConnStr: string): string;
}

/**
 * Shared, connector-agnostic utilities reusable across connector modules.
 */
export class ConnectorSharedUtils {
  /**
   * Trims surrounding whitespace and quotes from a local file path.
   */
  static cleanFilePath(raw?: string): string {
    return (raw ?? "").trim().replace(/^["']|["']$/g, "").trim();
  }

  /**
   * Validates that all required non-password fields declared in a descriptor are present and non-empty.
   */
  static validateRequiredFields(
    descriptor: ConnectorDescriptorDto,
    config: Record<string, unknown>,
  ): string | undefined {
    for (const field of descriptor.formFields) {
      if (field.required && field.type !== "password") {
        const val = config[field.name];
        if (val === undefined || val === null || String(val).trim().length === 0) {
          return `Required field '${field.name}' (${field.label}) is missing or empty.`;
        }
      }
    }
    return undefined;
  }

  /**
   * Generic key-value (`Password=...` / `Pwd=...`) connection string credential extraction
   * and `{password}` placeholder substitution.
   */
  static sanitizeKeyValueConnectionString(
    rawConnStr: string,
    rawPassword?: string,
  ): { connectionString: string; password?: string } {
    let connStr = rawConnStr.trim();
    let pwd = rawPassword?.trim() ? rawPassword.trim() : undefined;

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

    if (pwd && !connStr.toLowerCase().includes("{password}")) {
      const trimmed = connStr.endsWith(";") ? connStr.slice(0, -1) : connStr;
      connStr = trimmed ? `${trimmed};Password={password};` : "Password={password};";
    }

    return { connectionString: connStr, password: pwd };
  }

  /**
   * Generic RFC 3986 URI (`scheme://user:password@host...`) credential extraction
   * and `{password}` placeholder substitution.
   */
  static sanitizeUriConnectionString(
    rawConnStr: string,
    rawPassword?: string,
  ): { connectionString: string; password?: string } {
    let connStr = rawConnStr.trim();
    let pwd = rawPassword?.trim() ? rawPassword.trim() : undefined;

    const uriPwdRegex = /^([a-z][a-z0-9+.-]*:\/\/[^/:@]+):([^@]+)(@.+)$/i;
    const uriMatch = connStr.match(uriPwdRegex);
    if (uriMatch) {
      const extractedUriPwd = decodeURIComponent(uriMatch[2]);
      if (!extractedUriPwd.toLowerCase().includes("{password}")) {
        if (!pwd) {
          pwd = extractedUriPwd;
        }
        connStr = connStr.replace(uriPwdRegex, "$1:{password}$3");
      }
    } else if (pwd && !connStr.toLowerCase().includes("{password}")) {
      const uriUserRegex = /^([a-z][a-z0-9+.-]*:\/\/[^@]+)(@.+)$/i;
      if (uriUserRegex.test(connStr)) {
        connStr = connStr.replace(uriUserRegex, "$1:{password}$2");
      }
    }

    return { connectionString: connStr, password: pwd };
  }
}

