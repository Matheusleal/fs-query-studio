import {
  ConnectorSharedUtils,
  type ConnectionFormInput,
  type FrontendConnectorModule,
} from "../connector-shared";

const descriptor = {
  kindId: "PostgreSql",
  displayName: "PostgreSQL",
  category: "sql",
  icon: "database",
  formFields: [
    {
      name: "connectionString",
      label: "PostgreSQL Connection String",
      type: "string" as const,
      required: true,
      placeholder: "Host=localhost;Database=mydb;Username=postgres",
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

export const postgreSqlFrontendModule: FrontendConnectorModule = {
  descriptor,
  autocompleteCategory: "Database",
  completionTriggerMembers: ["sql", "connect", "get", "query", "getConnectionString"],

  sanitizeCredentials(rawConnStr: string, rawPassword?: string) {
    const trimmed = rawConnStr.trim();
    if (/^postgres(?:ql)?:\/\//i.test(trimmed)) {
      return ConnectorSharedUtils.sanitizeUriConnectionString(trimmed, rawPassword);
    }
    return ConnectorSharedUtils.sanitizeKeyValueConnectionString(trimmed, rawPassword);
  },

  buildConfiguration(input: ConnectionFormInput): unknown {
    return { connectionString: input.connectionString ?? "" };
  },

  validateConfiguration(config: Record<string, unknown>): string | undefined {
    return ConnectorSharedUtils.validateRequiredFields(descriptor, config);
  },
};

