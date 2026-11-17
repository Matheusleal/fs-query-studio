import {
  ConnectorSharedUtils,
  type ConnectionFormInput,
  type FrontendConnectorModule,
} from "../connector-shared";

const descriptor = {
  kindId: "Oracle",
  displayName: "Oracle",
  category: "sql",
  icon: "database",
  defaultPort: 1521,
  formFields: [
    {
      name: "connectionString",
      label: "Oracle Connection String",
      type: "string" as const,
      required: true,
      placeholder: "Data Source=localhost:1521/FREEPDB1;User Id=system;Password={password};",
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

export const oracleFrontendModule: FrontendConnectorModule = {
  descriptor,
  defaultPort: 1521,
  autocompleteCategory: "Database",
  completionTriggerMembers: ["sql", "connect", "get", "query", "getConnectionString"],

  sanitizeCredentials(rawConnStr: string, rawPassword?: string) {
    const trimmed = rawConnStr.trim();
    if (/^oracle:\/\//i.test(trimmed)) {
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
