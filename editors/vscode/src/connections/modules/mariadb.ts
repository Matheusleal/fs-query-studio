import {
  ConnectorSharedUtils,
  type ConnectionFormInput,
  type FrontendConnectorModule,
} from "../connector-shared";

const descriptor = {
  kindId: "MariaDb",
  displayName: "MariaDB",
  category: "sql",
  icon: "database",
  defaultPort: 3306,
  formFields: [
    {
      name: "connectionString",
      label: "MariaDB Connection String",
      type: "string" as const,
      required: true,
      placeholder: "Server=localhost;Port=3307;Database=mydb;User ID=root;Password={password};",
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

export const mariaDbFrontendModule: FrontendConnectorModule = {
  descriptor,
  defaultPort: 3306,
  autocompleteCategory: "Database",
  completionTriggerMembers: ["sql", "connect", "get", "query", "getConnectionString"],

  sanitizeCredentials(rawConnStr: string, rawPassword?: string) {
    const trimmed = rawConnStr.trim();
    if (/^mariadb:\/\//i.test(trimmed) || /^mysql:\/\//i.test(trimmed)) {
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
