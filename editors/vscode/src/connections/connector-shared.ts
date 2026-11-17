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
  readonly defaultPort?: number;
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
   * Validates if a port number or string represents a valid TCP port (1-65535).
   */
  static isValidPort(port: unknown): boolean {
    if (port === undefined || port === null) {
      return false;
    }
    const num = typeof port === "number" ? port : Number(String(port).trim());
    return Number.isInteger(num) && num >= 1 && num <= 65535;
  }

  /**
   * Returns an error message if the port is invalid, or undefined if valid.
   */
  static validatePort(port: unknown): string | undefined {
    if (this.isValidPort(port)) {
      return undefined;
    }
    return "Port must be an integer between 1 and 65535.";
  }

  /**
   * Validates if a host/server identifier is non-empty and well-formed.
   * Accepts localhost, IPv4, IPv6, hostnames, domain names, and database instance syntax.
   */
  static isValidHost(host: unknown): boolean {
    if (typeof host !== "string") {
      return false;
    }
    const trimmed = host.trim();
    if (!trimmed) {
      return false;
    }
    if (trimmed === "." || trimmed === "(local)") {
      return true;
    }
    const hostRegex = /^((\(localdb\)\\[\w.-]+)|localhost|\[?[0-9a-fA-F:]+\]?|([a-zA-Z0-9_.-]+))(\\.*)?(:\d+)?$/;
    return hostRegex.test(trimmed);
  }

  /**
   * Returns an error message if the host is invalid, or undefined if valid.
   */
  static validateHost(host: unknown): string | undefined {
    if (this.isValidHost(host)) {
      return undefined;
    }
    return "Host or server cannot be empty and must be a valid hostname, IP address, or server identifier.";
  }

  /**
   * Validates if a file path is non-empty and ends with one of the allowed extensions.
   */
  static isValidFilePath(filePath: unknown, allowedExtensions?: readonly string[]): boolean {
    if (typeof filePath !== "string") {
      return false;
    }
    const cleaned = this.cleanFilePath(filePath);
    if (!cleaned) {
      return false;
    }
    if (!allowedExtensions || allowedExtensions.length === 0) {
      return true;
    }
    const lower = cleaned.toLowerCase();
    return allowedExtensions.some((ext) => {
      const normalized = ext.startsWith(".") ? ext.toLowerCase() : `.${ext.toLowerCase()}`;
      return lower.endsWith(normalized);
    });
  }

  /**
   * Returns an error message if the file path is invalid, or undefined if valid.
   */
  static validateFilePath(filePath: unknown, allowedExtensions?: readonly string[]): string | undefined {
    if (typeof filePath !== "string" || !this.cleanFilePath(filePath)) {
      return "File path cannot be empty.";
    }
    if (allowedExtensions && allowedExtensions.length > 0 && !this.isValidFilePath(filePath, allowedExtensions)) {
      return `File path must end with one of: ${allowedExtensions.join(", ")}.`;
    }
    return undefined;
  }

  /**
   * Validates if a URL is a valid absolute URL with allowed protocols (defaulting to http: and https:).
   */
  static isValidUrl(url: unknown, allowedProtocols: readonly string[] = ["http:", "https:"]): boolean {
    if (typeof url !== "string") {
      return false;
    }
    const trimmed = url.trim();
    if (!trimmed) {
      return false;
    }
    try {
      const parsed = new URL(trimmed);
      return allowedProtocols.includes(parsed.protocol.toLowerCase());
    } catch {
      return false;
    }
  }

  /**
   * Returns an error message if the URL is invalid, or undefined if valid.
   */
  static validateUrl(url: unknown, allowedProtocols: readonly string[] = ["http:", "https:"]): string | undefined {
    if (this.isValidUrl(url, allowedProtocols)) {
      return undefined;
    }
    return `URL must be a valid absolute URL with protocol ${allowedProtocols.join(" or ")}.`;
  }

  /**
   * Builds a key-value connection string (e.g. `Server=localhost;Port=1433;Database=app;`).
   */
  static buildKeyValueConnectionString(params: Record<string, string | number | boolean | undefined>): string {
    const parts = Object.entries(params)
      .filter(([_, v]) => v !== undefined && v !== null && String(v).trim().length > 0)
      .map(([k, v]) => `${k}=${v}`);
    return parts.length > 0 ? `${parts.join(";")};` : "";
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

