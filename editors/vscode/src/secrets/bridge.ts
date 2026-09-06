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

export function registerSecretBridge(deps: { client: HostRequestClient; secrets: SecretReader }): void {
  deps.client.setHostRequestHandler(async (method, params) => {
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
      return { error: { code: "host/secretReadFailed", message: (err as Error).message } };
    }
  });
}
