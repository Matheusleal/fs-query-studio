import {
  ConnectorSharedUtils,
  type ConnectionFormInput,
  type FrontendConnectorModule,
} from "../connector-shared";

const descriptor = {
  kindId: "Excel",
  displayName: "Excel",
  category: "excel",
  icon: "file",
  formFields: [
    {
      name: "filePath",
      label: "Absolute file path",
      type: "file" as const,
      required: true,
      placeholder: "/path/to/spreadsheet.xlsx",
      secretHandling: "none",
    },
  ],
};

export const excelFrontendModule: FrontendConnectorModule = {
  descriptor,
  autocompleteCategory: "Excel",
  completionTriggerMembers: ["excel", "connectExcel", "queryExcel", "queryExcelSheet"],

  buildConfiguration(input: ConnectionFormInput): unknown {
    return { filePath: ConnectorSharedUtils.cleanFilePath(input.filePath) };
  },

  validateConfiguration(config: Record<string, unknown>): string | undefined {
    return ConnectorSharedUtils.validateRequiredFields(descriptor, config);
  },
};

