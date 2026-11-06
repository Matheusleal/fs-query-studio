import {
  ConnectorSharedUtils,
  type ConnectionFormInput,
  type FrontendConnectorModule,
} from "../connector-shared";

const descriptor = {
  kindId: "Xml",
  displayName: "XML File",
  category: "xml",
  icon: "code",
  formFields: [
    {
      name: "filePath",
      label: "XML File Path (.xml)",
      type: "file" as const,
      required: true,
      placeholder: "/path/to/data.xml",
      secretHandling: "none",
    },
    {
      name: "rowElement",
      label: "Row Element Tag (optional)",
      type: "string" as const,
      required: false,
      placeholder: "Leave blank to auto-discover all collections",
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

export const xmlFrontendModule: FrontendConnectorModule = {
  descriptor,
  autocompleteCategory: "Xml",
  completionTriggerMembers: ["xml", "getXmlConnection", "queryXml", "queryXmlTable"],

  buildConfiguration(input: ConnectionFormInput): unknown {
    const config: Record<string, unknown> = {
      filePath: ConnectorSharedUtils.cleanFilePath(input.filePath),
    };
    if (input.rowElement && typeof input.rowElement === "string" && input.rowElement.trim().length > 0) {
      config.rowElement = input.rowElement.trim();
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

