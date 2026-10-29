import {
  ConnectorSharedUtils,
  type ConnectionFormInput,
  type FrontendConnectorModule,
} from "../connector-shared";

const descriptor = {
  kindId: "Csv",
  displayName: "CSV",
  category: "csv",
  icon: "file",
  formFields: [
    {
      name: "filePath",
      label: "Absolute file path",
      type: "file" as const,
      required: true,
      placeholder: "/path/to/data.csv",
      secretHandling: "none",
    },
    {
      name: "delimiter",
      label: "Delimiter (optional: comma, semicolon, tab. Leave blank for auto-detect)",
      type: "string" as const,
      required: false,
      placeholder: "Leave blank for auto-detect (, ; \\t |)",
      secretHandling: "none",
    },
  ],
};

export const csvFrontendModule: FrontendConnectorModule = {
  descriptor,
  autocompleteCategory: "Csv",
  completionTriggerMembers: ["csv", "connectCsv", "queryCsv"],

  buildConfiguration(input: ConnectionFormInput): unknown {
    const config: { filePath: string; delimiter?: string } = {
      filePath: ConnectorSharedUtils.cleanFilePath(input.filePath),
    };
    if (input.delimiter && input.delimiter.trim().length > 0) {
      config.delimiter = input.delimiter.trim();
    }
    return config;
  },

  validateConfiguration(config: Record<string, unknown>): string | undefined {
    return ConnectorSharedUtils.validateRequiredFields(descriptor, config);
  },
};

