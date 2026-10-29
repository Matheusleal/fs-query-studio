import type { ConnectorDescriptorDto, ConnectorKindName } from "../protocol";
import {
  ConnectorSharedUtils,
  type ConnectionFormInput,
  type FrontendConnectorModule,
  type TargetConnectorCategory,
} from "./connector-shared";
import { BUILTIN_FRONTEND_CONNECTORS } from "./modules";

export type { ConnectionFormInput, FrontendConnectorModule, TargetConnectorCategory };

/**
 * Central, connector-agnostic frontend registry.
 * Delegates all connector-specific validation, credential sanitization, authentication rules,
 * and configuration building to each registered `FrontendConnectorModule`, falling back
 * to shared generic utilities when a dynamic/unknown connector is encountered.
 */
export class ConnectorFrontRegistry {
  private static readonly modules = new Map<string, FrontendConnectorModule>(
    BUILTIN_FRONTEND_CONNECTORS.map((m) => [m.descriptor.kindId.toLowerCase(), m]),
  );

  /**
   * Registers or replaces a frontend connector module at runtime.
   */
  static register(module: FrontendConnectorModule): void {
    this.modules.set(module.descriptor.kindId.toLowerCase(), module);
  }

  /**
   * Looks up a registered frontend connector module by kind identifier (case-insensitive).
   */
  static getModule(kind: ConnectorKindName | string | undefined): FrontendConnectorModule | undefined {
    if (!kind) {
      return undefined;
    }
    return this.modules.get(kind.toLowerCase());
  }

  /**
   * Default fallback connector descriptors derived from all registered frontend connector modules.
   */
  static get defaultDescriptors(): ConnectorDescriptorDto[] {
    return Array.from(this.modules.values()).map((m) => m.descriptor);
  }

  /**
   * Sanitizes a connection string and extracts/replaces plaintext credentials with `{password}`
   * by delegating to the connector's own `sanitizeCredentials` rule or shared generic helpers.
   */
  static sanitizeConnectionStringAndPassword(
    kind: ConnectorKindName,
    rawConnStr: string,
    rawPassword?: string,
  ): { connectionString: string; password?: string } {
    const mod = this.getModule(kind);
    if (mod?.sanitizeCredentials) {
      return mod.sanitizeCredentials(rawConnStr, rawPassword);
    }
    const trimmed = rawConnStr.trim();
    if (/^[a-z][a-z0-9+.-]*:\/\//i.test(trimmed)) {
      return ConnectorSharedUtils.sanitizeUriConnectionString(trimmed, rawPassword);
    }
    return ConnectorSharedUtils.sanitizeKeyValueConnectionString(trimmed, rawPassword);
  }

  /**
   * Checks whether a connection uses Windows Authentication (Integrated Security) without a password,
   * delegated to the connector module.
   */
  static isWindowsAuthentication(
    kind: ConnectorKindName,
    connectionString: string,
    hasPassword = false,
  ): boolean {
    const mod = this.getModule(kind);
    return mod?.isWindowsAuthentication ? mod.isWindowsAuthentication(connectionString, hasPassword) : false;
  }

  /**
   * Checks whether the connector and field support interactive Windows Authentication selection.
   */
  static supportsWindowsAuthPrompt(kind: ConnectorKindName, fieldName: string): boolean {
    const mod = this.getModule(kind);
    return mod?.supportsWindowsAuthPrompt ? mod.supportsWindowsAuthPrompt(fieldName) : false;
  }

  /**
   * Returns the default Windows Authentication connection string for a connector.
   */
  static getDefaultWindowsAuthConnectionString(kind: ConnectorKindName): string {
    return this.getModule(kind)?.defaultWindowsAuthConnectionString ?? "";
  }

  /**
   * Normalizes a Windows Authentication connection string using the connector's own rule.
   */
  static normalizeWindowsAuthConnectionString(kind: ConnectorKindName, rawConnStr: string): string {
    const mod = this.getModule(kind);
    return mod?.normalizeWindowsAuthConnectionString
      ? mod.normalizeWindowsAuthConnectionString(rawConnStr)
      : rawConnStr.trim();
  }

  /**
   * Builds the configuration object from form input by delegating to the connector module.
   */
  static configurationFor(input: ConnectionFormInput): unknown {
    if (input.configValues && Object.keys(input.configValues).length > 0) {
      return { ...input.configValues };
    }
    const mod = this.getModule(input.kind);
    if (mod) {
      return mod.buildConfiguration(input);
    }
    return {};
  }

  /**
   * Validates a configuration object against the connector module's own validation logic.
   */
  static validateConfiguration(
    kind: ConnectorKindName,
    config: Record<string, unknown>,
  ): string | undefined {
    const mod = this.getModule(kind);
    if (mod) {
      return mod.validateConfiguration(config);
    }
    return undefined;
  }

  /**
   * Returns the target connection category for the function call before the cursor,
   * derived dynamically from each registered connector module's `completionTriggerMembers`.
   */
  static getTargetConnectorCategory(linePrefix: string): TargetConnectorCategory | undefined {
    for (const mod of this.modules.values()) {
      if (mod.completionTriggerMembers.length === 0) {
        continue;
      }
      const escapedMembers = mod.completionTriggerMembers.join("|");
      const pattern = new RegExp(`Connections\\.(?:${escapedMembers})\\b\\s*\\(?\\s*"[^"]*$`);
      if (pattern.test(linePrefix)) {
        return mod.autocompleteCategory;
      }
    }
    return undefined;
  }

  /**
   * Checks whether a connector kind belongs to a target autocomplete category
   * by delegating to the registered connector module.
   */
  static isInCategory(
    kind: ConnectorKindName | string | undefined,
    category: TargetConnectorCategory,
  ): boolean {
    if (!kind) {
      return true;
    }
    const mod = this.getModule(kind);
    if (mod) {
      return mod.autocompleteCategory === category;
    }
    return kind.toLowerCase() === category.toLowerCase();
  }
}
