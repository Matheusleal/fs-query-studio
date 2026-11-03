/** Answers the worker's host-bound `secret/get` requests from VS Code
 *  `SecretStorage` (F5 reverse channel). No `vscode` import — `secrets` and
 *  `client` are injected structurally so this is unit-testable. Only
 *  `secret/get` is supported: the extension performs every credential
 *  write itself around `connection/register` / the remove command. */

export interface SecretReader {
  get(key: string): Thenable<string | undefined>;
}

export interface HostRequestClient {
  setHostRequestHandler(
    fn: (
      method: string,
      params: unknown,
    ) => Promise<{ result?: unknown; error?: { code: string; message: string } }>,
  ): void;
}

export interface StagingProgressEvent {
  tableName?: string;
  filePath?: string;
  phase: "Reading" | "Writing" | "Indexing" | "Completed" | "Skipped" | "Failed";
  rowCount?: number | null;
  currentIndex?: number | null;
  totalCount?: number | null;
  elapsedMs?: number | null;
  message?: string;
}

export function registerSecretBridge(deps: {
  client: HostRequestClient;
  secrets: SecretReader;
  onEntityMappingStatus?: (status: "started" | "completed" | "failed") => void;
  onStagingProgress?: (event: StagingProgressEvent) => void;
}): void {
  deps.client.setHostRequestHandler(async (method, params) => {
    if (method === "entityMapping/status") {
      const status = (params as { status?: unknown } | null)?.status;
      if (status === "started" || status === "completed" || status === "failed") {
        deps.onEntityMappingStatus?.(status);
        return { result: { acknowledged: true } };
      }
      return { error: { code: "host/badParams", message: "status must be 'started', 'completed', or 'failed'" } };
    }
    if (method === "staging/progress") {
      const event = params as StagingProgressEvent | null;
      if (event && typeof event.phase === "string") {
        deps.onStagingProgress?.(event);
        return { result: { acknowledged: true } };
      }
      return { error: { code: "host/badParams", message: "staging/progress requires an event with 'phase'" } };
    }
    if (method !== "secret/get") {
      return { error: { code: "host/unknownMethod", message: `Unsupported host method: ${method}` } };
    }
    const reference = (params as { reference?: unknown } | null)?.reference;
    if (typeof reference !== "string") {
      return { error: { code: "host/badParams", message: "secret/get requires a string 'reference'" } };
    }
    try {
      const secret = await deps.secrets.get(reference);
      return { result: { secret: secret ?? null } };
    } catch (err) {
      // `String(err)` for a non-`Error` throw: a dropped `message` key becomes
      // a worker-killing `JsonException` on the non-optional
      // `WorkerErrorInfo.Message`.
      return {
        error: {
          code: "host/secretReadFailed",
          message: err instanceof Error ? err.message : String(err),
        },
      };
    }
  });
}
