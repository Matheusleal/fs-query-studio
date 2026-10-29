import {
  ConnectorSharedUtils,
  type ConnectionFormInput,
  type FrontendConnectorModule,
} from "../connector-shared";

const descriptor = {
  kindId: "SqlServer",
  displayName: "SQL Server",
  category: "sql",
  icon: "database",
  formFields: [
    {
      name: "connectionString",
      label: "SQL Server Connection String",
      type: "string" as const,
      required: true,
      placeholder: "Server=localhost;Database=master;TrustServerCertificate=True;",
      secretHandling: "connectionString:{password}",
    },
    {
      name: "password",
      label: "Password",
      type: "password" as const,
      required: false,
      secretHandling: "isolated",
    },
  ],
};

const WINDOWS_AUTH_REGEX =
  /(integrated\s+security\s*=\s*(true|sspi)|trusted_connection\s*=\s*(true|yes))/i;

export const sqlServerFrontendModule: FrontendConnectorModule = {
  descriptor,
  autocompleteCategory: "Database",
  completionTriggerMembers: ["sql", "connect", "get", "query", "getConnectionString"],
  defaultWindowsAuthConnectionString:
    "Server=localhost;Database=master;Integrated Security=True;TrustServerCertificate=True;",

  isWindowsAuthentication(connectionString: string, hasPassword = false): boolean {
    if (hasPassword) {
      return false;
    }
    return WINDOWS_AUTH_REGEX.test(connectionString);
  },

  supportsWindowsAuthPrompt(fieldName: string): boolean {
    return fieldName === "connectionString";
  },

  normalizeWindowsAuthConnectionString(rawConnStr: string): string {
    let normalized = rawConnStr.trim();
    if (!/integrated\s+security\s*=/i.test(normalized) && !/trusted_connection\s*=/i.test(normalized)) {
      normalized = normalized.replace(/;?$/, ";Integrated Security=True;");
    }
    if (!/trustservercertificate\s*=/i.test(normalized)) {
      normalized = normalized.replace(/;?$/, ";TrustServerCertificate=True;");
    }
    return normalized;
  },

  sanitizeCredentials(rawConnStr: string, rawPassword?: string) {
    const connStr = rawConnStr.trim();
    const pwd = rawPassword?.trim() ? rawPassword.trim() : undefined;
    if (WINDOWS_AUTH_REGEX.test(connStr) && !pwd) {
      return { connectionString: connStr, password: undefined };
    }
    return ConnectorSharedUtils.sanitizeKeyValueConnectionString(connStr, pwd);
  },

  buildConfiguration(input: ConnectionFormInput): unknown {
    return { connectionString: input.connectionString ?? "" };
  },

  validateConfiguration(config: Record<string, unknown>): string | undefined {
    return ConnectorSharedUtils.validateRequiredFields(descriptor, config);
  },
};

