import {
  ConnectorSharedUtils,
  type ConnectionFormInput,
  type FrontendConnectorModule,
} from "../connector-shared";

const descriptor = {
  kindId: "Json",
  displayName: "JSON File",
  category: "json",
  icon: "json",
  formFields: [
    {
      name: "filePath",
      label: "JSON File Path (.json)",
      type: "file" as const,
      required: true,
      placeholder: "/path/to/data.json",
      secretHandling: "none",
    },
    {
      name: "jsonPath",
      label: "JSON Property Path (optional)",
      type: "string" as const,
      required: false,
      placeholder: "Leave blank to auto-discover all collections (or e.g. data.items)",
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

export const jsonFrontendModule: FrontendConnectorModule = {
  descriptor,
  autocompleteCategory: "Json",
  completionTriggerMembers: ["json", "getJsonConnection", "queryJsonFile", "queryJsonFileTable"],

  buildConfiguration(input: ConnectionFormInput): unknown {
    const config: Record<string, unknown> = {
      filePath: ConnectorSharedUtils.cleanFilePath(input.filePath),
    };
    if (input.jsonPath && typeof input.jsonPath === "string" && input.jsonPath.trim().length > 0) {
      config.jsonPath = input.jsonPath.trim();
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

