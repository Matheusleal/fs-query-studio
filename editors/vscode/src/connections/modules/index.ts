import type { FrontendConnectorModule } from "../connector-shared";
import { sqlServerFrontendModule } from "./sqlserver";
import { postgreSqlFrontendModule } from "./postgresql";
import { sqliteFrontendModule } from "./sqlite";
import { csvFrontendModule } from "./csv";
import { excelFrontendModule } from "./excel";
import { restFrontendModule } from "./rest";

/**
 * Single canonical registration list of built-in frontend connector modules.
 * Adding a new connector in the frontend only requires adding its module here.
 */
export const BUILTIN_FRONTEND_CONNECTORS: readonly FrontendConnectorModule[] = [
  sqlServerFrontendModule,
  postgreSqlFrontendModule,
  sqliteFrontendModule,
  csvFrontendModule,
  excelFrontendModule,
  restFrontendModule,
];

