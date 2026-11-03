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
    {
      name: "columnTypes",
      label: "Column Types Override (optional)",
      type: "string" as const,
      required: false,
      placeholder: "Col1=decimal, Col2=int (or JSON object)",
      secretHandling: "none",
    },
    {
      name: "columnTransforms",
      label: "Column Transforms / Formulas (optional)",
      type: "string" as const,
      required: false,
      placeholder: "Col1=replace(',', '.'), Col2=trim",
      secretHandling: "none",
    },
  ],
};

export const csvFrontendModule: FrontendConnectorModule = {
  descriptor,
  autocompleteCategory: "Csv",
  completionTriggerMembers: ["csv", "connectCsv", "queryCsv"],

  buildConfiguration(input: ConnectionFormInput): unknown {
    const config: Record<string, unknown> = {
      filePath: ConnectorSharedUtils.cleanFilePath(input.filePath),
    };
    if (input.delimiter && typeof input.delimiter === "string" && input.delimiter.trim().length > 0) {
      config.delimiter = input.delimiter.trim();
    }
    if (input.columnTypes && typeof input.columnTypes === "string" && input.columnTypes.trim().length > 0) {
      config.columnTypes = input.columnTypes.trim();
    }
    if (input.columnTransforms && typeof input.columnTransforms === "string" && input.columnTransforms.trim().length > 0) {
      config.columnTransforms = input.columnTransforms.trim();
    }
    return config;
  },

  validateConfiguration(config: Record<string, unknown>): string | undefined {
    return ConnectorSharedUtils.validateRequiredFields(descriptor, config);
  },
};

