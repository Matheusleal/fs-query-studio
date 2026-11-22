import * as vscode from "vscode";

export type ConnectionStatus = "connected" | "disconnected" | "connecting";

export interface StatusChangeEvent {
  id: string;
  status: ConnectionStatus;
}

export interface NotificationEvent {
  type: "warning" | "error" | "info";
  message: string;
}

export interface ConnectionHealthOptions {
  retryDelayMs?: number;
  backoffFactor?: number;
  maxDelayMs?: number;
  log?: (line: string) => void;
  isPortuguese?: () => boolean;
}

export interface CheckRetryOptions<T> {
  connId: string;
  connName: string;
  maxRetries?: number;
  checkFn: () => Promise<T | undefined>;
  getErrorMessage?: () => string | undefined;
}

export class ConnectionHealthService implements vscode.Disposable {
  private readonly _onStatusChanged = new vscode.EventEmitter<StatusChangeEvent>();
  readonly onStatusChanged = this._onStatusChanged.event;

  private readonly _onNotify = new vscode.EventEmitter<NotificationEvent>();
  readonly onNotify = this._onNotify.event;

  private readonly connectionStatus = new Map<string, ConnectionStatus>();
  private readonly retryCounts = new Map<string, number>();
  private readonly retryTimers = new Map<string, NodeJS.Timeout>();
  private readonly operationTokens = new Map<string, number>();

  constructor(private readonly options: ConnectionHealthOptions = {}) {}

  getConnectionStatus(id: string): ConnectionStatus {
    return this.connectionStatus.get(id) ?? "connecting";
  }

  setConnectionStatus(id: string, status: ConnectionStatus): void {
    this.connectionStatus.set(id, status);
    this._onStatusChanged.fire({ id, status });
  }

  getRetryCount(id: string): number {
    return this.retryCounts.get(id) ?? 0;
  }

  hasActiveTimer(id: string): boolean {
    return this.retryTimers.has(id);
  }

  cancelRetries(connId: string): void {
    const timer = this.retryTimers.get(connId);
    if (timer) {
      clearTimeout(timer);
      this.retryTimers.delete(connId);
    }
    this.retryCounts.delete(connId);
    const nextToken = (this.operationTokens.get(connId) ?? 0) + 1;
    this.operationTokens.set(connId, nextToken);
  }

  calculateDelay(attempt: number): number {
    const base = this.options.retryDelayMs ?? 3000;
    const factor = this.options.backoffFactor ?? 1;
    const maxDelay = this.options.maxDelayMs ?? 30000;
    const delay = Math.round(base * Math.pow(factor, Math.max(0, attempt - 1)));
    return Math.min(delay, maxDelay);
  }

  private isPt(): boolean {
    if (this.options.isPortuguese) {
      return this.options.isPortuguese();
    }
    return Boolean(vscode.env?.language?.startsWith("pt"));
  }

  async checkWithRetry<T>(options: CheckRetryOptions<T>): Promise<T | undefined> {
    const { connId, connName, maxRetries = 3, checkFn, getErrorMessage } = options;

    const token = (this.operationTokens.get(connId) ?? 0) + 1;
    this.operationTokens.set(connId, token);

    this.setConnectionStatus(connId, "connecting");

    const result = await checkFn();

    // Check if operation was cancelled while awaiting checkFn
    if (this.operationTokens.get(connId) !== token) {
      return result;
    }

    if (result !== undefined) {
      const hadPreviousRetries = (this.retryCounts.get(connId) ?? 0) > 0;
      this.cancelRetries(connId);
      this.setConnectionStatus(connId, "connected");

      if (hadPreviousRetries) {
        const isPt = this.isPt();
        const msg = isPt
          ? `Conexão com '${connName}' estabelecida com sucesso.`
          : `Connection to '${connName}' established successfully.`;
        this._onNotify.fire({ type: "info", message: msg });
      }
      return result;
    }

    const currentAttempt = (this.retryCounts.get(connId) ?? 0) + 1;
    const isPt = this.isPt();
    const errMsg = getErrorMessage?.() ?? "Connection error";

    if (currentAttempt <= maxRetries) {
      this.retryCounts.set(connId, currentAttempt);
      this.setConnectionStatus(connId, "connecting");

      const warnMsg = isPt
        ? `A conexão com '${connName}' não foi estabelecida (${errMsg}). Tentando reconectar (tentativa ${currentAttempt} de ${maxRetries})...`
        : `Connection to '${connName}' could not be established (${errMsg}). Retrying (attempt ${currentAttempt} of ${maxRetries})...`;
      this._onNotify.fire({ type: "warning", message: warnMsg });

      const delay = this.calculateDelay(currentAttempt);
      const timer = setTimeout(() => {
        this.retryTimers.delete(connId);
        void this.checkWithRetry(options);
      }, delay);
      this.retryTimers.set(connId, timer);
      return undefined;
    } else {
      this.retryCounts.delete(connId);
      this.setConnectionStatus(connId, "disconnected");

      const failMsg = isPt
        ? `Não foi possível estabelecer conexão com '${connName}' após ${maxRetries} tentativas. Verifique se o serviço está em execução.`
        : `Could not establish connection to '${connName}' after ${maxRetries} attempts. Please verify if the database service is running.`;
      this._onNotify.fire({ type: "error", message: failMsg });
      return undefined;
    }
  }

  clear(): void {
    for (const timer of this.retryTimers.values()) {
      clearTimeout(timer);
    }
    this.retryTimers.clear();
    this.retryCounts.clear();
    this.connectionStatus.clear();
    this.operationTokens.clear();
  }

  dispose(): void {
    this.clear();
    this._onStatusChanged.dispose();
    this._onNotify.dispose();
  }
}
