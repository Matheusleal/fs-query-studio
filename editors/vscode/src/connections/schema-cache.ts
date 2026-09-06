import type { SchemaTable } from "../protocol";

/** Per-session cache of `connection/schema` results, keyed by connection
 *  id. Invalidated only by the explicit Refresh command (F5 spec). */
export class SchemaCache {
  private readonly entries = new Map<string, SchemaTable[]>();

  get(id: string): SchemaTable[] | undefined {
    return this.entries.get(id);
  }

  set(id: string, tables: SchemaTable[]): void {
    this.entries.set(id, tables);
  }

  clear(): void {
    this.entries.clear();
  }
}
