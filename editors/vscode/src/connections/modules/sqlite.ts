import {
  ConnectorSharedUtils,
  type ConnectionFormInput,
  type FrontendConnectorModule,
} from "../connector-shared";

const descriptor = {
  kindId: "Sqlite",
  displayName: "SQLite",
  category: "sql",
  icon: "database",
  formFields: [
    {
      name: "filePath",
      label: "Absolute file path",
      type: "file" as const,
      required: true,
      placeholder: "/path/to/database.db",
      secretHandling: "none",
    },
  ],
};

export const sqliteFrontendModule: FrontendConnectorModule = {
  descriptor,
  autocompleteCategory: "Database",
  completionTriggerMembers: ["sql", "connect", "get", "query", "getConnectionString"],

  buildConfiguration(input: ConnectionFormInput): unknown {
    return { filePath: ConnectorSharedUtils.cleanFilePath(input.filePath) };
  },

  validateConfiguration(config: Record<string, unknown>): string | undefined {
    return ConnectorSharedUtils.validateRequiredFields(descriptor, config);
  },
};

