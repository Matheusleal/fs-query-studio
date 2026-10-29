import {
  type ConnectionDefinition,
  type ConnectorDescriptorDto,
  type ConnectorKindName,
} from "../protocol";

export type TargetConnectorCategory = "Database" | "Csv" | "Excel" | "RestJson";

export interface ConnectionFormInput {
  kind: ConnectorKindName;
  name: string;
  filePath?: string;
  delimiter?: string;
  connectionString?: string;
  password?: string;
  url?: string;
  configValues?: Record<string, unknown>;
}

/**
 * Single source of truth in the frontend for all connector-specific
 * metadata, default descriptors, credential sanitization, and category mapping.
 * Adding support for a new connector in the frontend only requires updates in this file.
 */
export class ConnectorFrontRegistry {
  /**
   * Default fallback connector descriptors used before the worker initializes.
   */
  static readonly defaultDescriptors: ConnectorDescriptorDto[] = [
    {
      kindId: "SqlServer",
      displayName: "SQL Server",
      category: "sql",
      icon: "database",
      formFields: [
        {
          name: "connectionString",
          label: "SQL Server Connection String",
          type: "string",
          required: true,
          placeholder: "Server=localhost;Database=master;TrustServerCertificate=True;",
          secretHandling: "connectionString:{password}",
        },
        {
          name: "password",
          label: "Password",
          type: "password",
          required: false,
          secretHandling: "isolated",
        },
      ],
    },
    {
      kindId: "PostgreSql",
      displayName: "PostgreSQL",
      category: "sql",
      icon: "database",
      formFields: [
        {
          name: "connectionString",
          label: "PostgreSQL Connection String",
          type: "string",
          required: true,
          placeholder: "Host=localhost;Database=mydb;Username=postgres",
          secretHandling: "connectionString:{password}",
        },
        {
          name: "password",
          label: "Password",
          type: "password",
          required: false,
          secretHandling: "isolated",
        },
      ],
    },
    {
      kindId: "Sqlite",
      displayName: "SQLite",
      category: "sql",
      icon: "database",
      formFields: [
        {
          name: "filePath",
          label: "Absolute file path",
          type: "file",
          required: true,
          placeholder: "/path/to/database.db",
          secretHandling: "none",
        },
      ],
    },
    {
      kindId: "Csv",
      displayName: "CSV",
      category: "csv",
      icon: "file",
      formFields: [
        {
          name: "filePath",
          label: "Absolute file path",
          type: "file",
          required: true,
          placeholder: "/path/to/data.csv",
          secretHandling: "none",
        },
        {
          name: "delimiter",
          label: "Delimiter (optional: comma, semicolon, tab. Leave blank for auto-detect)",
          type: "string",
          required: false,
          placeholder: "Leave blank for auto-detect (, ; \\t |)",
          secretHandling: "none",
        },
      ],
    },
    {
      kindId: "Excel",
      displayName: "Excel",
      category: "excel",
      icon: "file",
      formFields: [
        {
          name: "filePath",
          label: "Absolute file path",
          type: "file",
          required: true,
          placeholder: "/path/to/spreadsheet.xlsx",
          secretHandling: "none",
        },
      ],
    },
    {
      kindId: "RestJson",
      displayName: "REST API (JSON)",
      category: "rest",
      icon: "globe",
      formFields: [
        {
          name: "url",
          label: "Endpoint URL",
          type: "string",
          required: true,
          placeholder: "https://api.example.com/data",
          secretHandling: "none",
        },
      ],
    },
  ];

  /**
   * Sanitizes connection string and extracts/replaces plaintext credentials with `{password}`.
   */
  static sanitizeConnectionStringAndPassword(
    kind: ConnectorKindName,
    rawConnStr: string,
    rawPassword?: string,
  ): { connectionString: string; password?: string } {
    let connStr = rawConnStr.trim();
    let pwd = rawPassword?.trim() ? rawPassword.trim() : undefined;

    // If SQL Server has Integrated Security or Trusted_Connection and no raw password:
    const isIntegrated =
      kind === "SqlServer" &&
      /(integrated\s+security\s*=\s*(true|sspi)|trusted_connection\s*=\s*(true|yes))/i.test(connStr);
    if (isIntegrated && !pwd) {
      return { connectionString: connStr, password: undefined };
    }

    // 1. Key=value connection string:
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

    // 2. URI format (e.g. postgres://user:secret@host:port/db):
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

    // 3. Fallback appending:
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

  /**
   * Checks whether a connection uses Windows Authentication (Integrated Security) without a password.
   */
  static isWindowsAuthentication(kind: ConnectorKindName, connectionString: string, hasPassword = false): boolean {
    if (kind !== "SqlServer" || hasPassword) {
      return false;
    }
    return /(integrated\s+security\s*=\s*(true|sspi)|trusted_connection\s*=\s*(true|yes))/i.test(connectionString);
  }

  /**
   * Checks whether the connector and field support interactive Windows Authentication selection.
   */
  static supportsWindowsAuthPrompt(kind: ConnectorKindName, fieldName: string): boolean {
    return kind === "SqlServer" && fieldName === "connectionString";
  }

  /**
   * Default Windows Authentication connection string for connectors that support it.
   */
  static readonly defaultWindowsAuthConnectionString =
    "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;";

  /**
   * Normalizes a connection string to include Integrated Security and TrustServerCertificate.
   */
  static normalizeWindowsAuthConnectionString(rawConnStr: string): string {
    let normalized = rawConnStr.trim();
    if (!/integrated\s+security\s*=/i.test(normalized) && !/trusted_connection\s*=/i.test(normalized)) {
      normalized = normalized.replace(/;?$/, ";Integrated Security=True;");
    }
    if (!/trustservercertificate\s*=/i.test(normalized)) {
      normalized = normalized.replace(/;?$/, ";TrustServerCertificate=True;");
    }
    return normalized;
  }

  /**
   * Returns the target connection category for the function call before the cursor,
   * or `undefined` if the cursor is not inside a recognized connection-name string argument.
   */
  static getTargetConnectorCategory(linePrefix: string): TargetConnectorCategory | undefined {
    if (/Connections\.(?:sql|connect|get|query|getConnectionString)\b\s*\(?\s*"[^"]*$/.test(linePrefix)) {
      return "Database";
    }
    if (/Connections\.(?:csv|connectCsv|queryCsv)\b\s*\(?\s*"[^"]*$/.test(linePrefix)) {
      return "Csv";
    }
    if (/Connections\.(?:excel|connectExcel|queryExcel|queryExcelSheet)\b\s*\(?\s*"[^"]*$/.test(linePrefix)) {
      return "Excel";
    }
    if (/Connections\.(?:rest|connectRest|queryJson)\b\s*\(?\s*"[^"]*$/.test(linePrefix)) {
      return "RestJson";
    }
    return undefined;
  }

  /**
   * Builds the configuration dictionary from form input.
   */
  static configurationFor(input: ConnectionFormInput): unknown {
    if (input.configValues && Object.keys(input.configValues).length > 0) {
      return { ...input.configValues };
    }
    const cleanPath = (input.filePath ?? "").trim().replace(/^["']|["']$/g, "").trim();
    switch (input.kind) {
      case "Sqlite":
      case "Excel":
        return { filePath: cleanPath };
      case "Csv": {
        const config: { filePath: string; delimiter?: string } = { filePath: cleanPath };
        if (input.delimiter && input.delimiter.trim().length > 0) {
          config.delimiter = input.delimiter.trim();
        }
        return config;
      }
      case "SqlServer":
      case "PostgreSql":
        return { connectionString: input.connectionString ?? "" };
      case "RestJson":
        return { url: input.url ?? "", method: "GET", headers: [], queryParams: [], body: null };
      default:
        return {};
    }
  }

  /**
   * Maps a connector kind to its autocomplete category.
   */
  static isInCategory(kind: ConnectorKindName | string | undefined, category: TargetConnectorCategory): boolean {
    if (!kind) {
      return true;
    }
    switch (category) {
      case "Database":
        return kind === "SqlServer" || kind === "PostgreSql" || kind === "Sqlite";
      case "Csv":
        return kind === "Csv";
      case "Excel":
        return kind === "Excel";
      case "RestJson":
        return kind === "RestJson";
      default:
        return false;
    }
  }
}
