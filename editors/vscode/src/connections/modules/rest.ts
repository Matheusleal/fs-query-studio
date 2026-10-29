import {
  ConnectorSharedUtils,
  type ConnectionFormInput,
  type FrontendConnectorModule,
} from "../connector-shared";

const descriptor = {
  kindId: "RestJson",
  displayName: "REST API (JSON)",
  category: "rest",
  icon: "globe",
  formFields: [
    {
      name: "url",
      label: "Endpoint URL",
      type: "string" as const,
      required: true,
      placeholder: "https://api.example.com/data",
      secretHandling: "none",
    },
  ],
};

export const restFrontendModule: FrontendConnectorModule = {
  descriptor,
  autocompleteCategory: "RestJson",
  completionTriggerMembers: ["rest", "connectRest", "queryJson"],

  buildConfiguration(input: ConnectionFormInput): unknown {
    return {
      url: input.url ?? "",
      method: "GET",
      headers: [],
      queryParams: [],
      body: null,
    };
  },

  validateConfiguration(config: Record<string, unknown>): string | undefined {
    return ConnectorSharedUtils.validateRequiredFields(descriptor, config);
  },
};

